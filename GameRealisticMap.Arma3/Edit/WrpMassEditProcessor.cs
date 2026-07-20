using System.Numerics;
using BIS.WRP;
using GameRealisticMap.Algorithms;
using GameRealisticMap.Arma3.GameEngine;
using GameRealisticMap.Arma3.TerrainBuilder;
using GameRealisticMap.ElevationModel;
using GameRealisticMap.Geometries;
using GameRealisticMap.Reporting;
using Pmad.ProgressTracking;

namespace GameRealisticMap.Arma3.Edit
{
    public class WrpMassEditProcessor
    {
        private readonly IProgressScope _progressSystem;
        private readonly ModelInfoLibrary _library;

        public WrpMassEditProcessor(IProgressScope progressSystem, ModelInfoLibrary library)
        {
            _progressSystem = progressSystem;
            _library = library;
        }

        public int Process(EditableWrp world, WrpMassEditBatch operations)
        {
            var totalChanges = 0;
            List<EditableWrpObject?> objects = world.GetNonDummyObjects().ToList();

            if (operations.Reduce.Count > 0)
            {
                foreach(var reduce in operations.Reduce.WithProgress(_progressSystem, "Reduce"))
                {
                    totalChanges += Reduce(objects, reduce);
                }
            }

            if (operations.Replace.Count > 0)
            {
                foreach (var replace in operations.Replace.WithProgress(_progressSystem, "Replace"))
                {
                    totalChanges += Replace(objects, replace);
                }
            }

            if (operations.SnapToGround.Count > 0)
            {
                var grid = world.ToElevationGrid();
                var modelCache = new Dictionary<string, ModelInfo?>(StringComparer.OrdinalIgnoreCase);
                foreach (var snap in operations.SnapToGround.WithProgress(_progressSystem, "SnapToGround"))
                {
                    totalChanges += SnapToGround(objects, grid, modelCache, snap);
                }
            }

            using (var report = _progressSystem.CreateSingle("Objects"))
            {
                objects = objects.Where(m => m != null)
                    .Concat(new[] { EditableWrpObject.Dummy })
                    .ToList();

                var id = 1;
                foreach (var obj in objects)
                {
                    obj!.ObjectID = id;
                    id++;
                }

                world.Objects = objects;
            }
            return totalChanges;
        }

        private int Replace(List<EditableWrpObject?> objects, WrpMassReplace operation)
        {
            float xShift = (float)(operation.XShift ?? 0.0);
            float zShift = (float)(operation.ZShift ?? 0.0);

            float altShift;
            if (operation.YShift == null)
            {
                var oldModel = _library.ReadModelInfoOnly(operation.SourceModel) 
                    ?? throw new ApplicationException($"ODOL file for model '{operation.SourceModel}' was not found."); 

                var newModel = _library.ReadModelInfoOnly(operation.TargetModel) 
                    ?? throw new ApplicationException($"ODOL file for model '{operation.TargetModel}' was not found.");

                altShift = newModel.BoundingCenter.Y - oldModel.BoundingCenter.Y;
            }
            else
            {
                altShift = (float)operation.YShift.Value;
            }

            var changes = 0;
            foreach (var obj in objects)
            {
                if (obj != null && string.Equals(obj.Model, operation.SourceModel, StringComparison.OrdinalIgnoreCase))
                {
                    obj.Model = operation.TargetModel;
                    if (altShift != 0)
                    {
                        if (obj.Transform.AltitudeScale != 1f)
                        {
                            obj.Transform.Altitude += altShift * obj.Transform.AltitudeScale;
                        }
                        else
                        {
                            obj.Transform.Altitude += altShift;
                        }
                    }
                    if (xShift != 0 || zShift != 0)
                    {
                        var translate = Vector3.Transform(new Vector3(xShift, 0, zShift), obj.Transform.Matrix);
                        obj.Transform.TranslateX = translate.X;
                        obj.Transform.TranslateZ = translate.Z;
                    }
                    changes++;
                }
            }
            _progressSystem.WriteLine($"Replace '{operation.SourceModel}'->'{operation.TargetModel}' with xShift={xShift:0.00}, yShift={altShift:0.00}, zShift={zShift:0.00} -> {changes} changes");
            return changes;
        }

        private int Reduce(List<EditableWrpObject?> objects, WrpMassReduce operation)
        {
            var changes = 0;
            var rnd = RandomHelper.CreateRandom(operation.Model.ToLowerInvariant());
            for (int i = 0; i < objects.Count; ++i)
            {
                var obj = objects[i];
                if (obj != null &&
                    Matches(obj.Model, operation) &&
                    (operation.RemoveRatio == 1 || rnd.NextDouble() <= operation.RemoveRatio))
                {
                    objects[i] = null;
                    changes++;
                }
            }
            Console.WriteLine($"Reduce '{operation.Model}' -> {changes} removed");
            return changes;
        }

        private int SnapToGround(List<EditableWrpObject?> objects, ElevationGrid grid, Dictionary<string, ModelInfo?> modelCache, WrpSnapToGround operation)
        {
            var changes = 0;
            var unknownModels = 0;
            foreach (var obj in objects)
            {
                if (obj == null || string.IsNullOrEmpty(obj.Model) || !MatchesFilter(obj.Model, operation))
                {
                    continue;
                }
                if (!modelCache.TryGetValue(obj.Model, out var model))
                {
                    modelCache.Add(obj.Model, model = _library.TryResolveByPath(obj.Model, out var resolved) ? resolved : null);
                }
                if (model == null)
                {
                    unknownModels++;
                    continue;
                }
                var matrix = obj.Transform.Matrix;
                var rotateOnly = matrix;
                rotateOnly.M41 = 0;
                rotateOnly.M42 = 0;
                rotateOnly.M43 = 0;
                var pointToCenter = Vector3.Transform(model.BoundingCenter, rotateOnly);
                var groundElevation = grid.ElevationAt(new TerrainPoint(matrix.M41 - pointToCenter.X, matrix.M43 - pointToCenter.Z)) + pointToCenter.Y;
                var aboveGround = matrix.M42 - groundElevation;
                if (aboveGround > operation.MinDistance || (operation.IncludeBuried && aboveGround < -operation.MinDistance))
                {
                    obj.Transform.Altitude = groundElevation;
                    changes++;
                }
            }
            _progressSystem.WriteLine($"SnapToGround '{operation.Model}' (minDistance={operation.MinDistance:0.00}, includeBuried={operation.IncludeBuried}) -> {changes} changes");
            if (unknownModels > 0)
            {
                _progressSystem.WriteLine($"SnapToGround: {unknownModels} objects ignored, their model file was not found (mod not loaded?)");
            }
            return changes;
        }

        private static bool MatchesFilter(string model, WrpSnapToGround operation)
        {
            if (string.IsNullOrEmpty(operation.Model))
            {
                return true;
            }
            if (operation.IsPattern)
            {
                return model.Contains(operation.Model, StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(model, operation.Model, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Matches(string? model, WrpMassReduce operation)
        {
            if (string.IsNullOrEmpty(model))
            {
                return false;
            }
            if (operation.IsPattern)
            {
                return model.Contains(operation.Model, StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(model, operation.Model, StringComparison.OrdinalIgnoreCase);
        }
    }
}
