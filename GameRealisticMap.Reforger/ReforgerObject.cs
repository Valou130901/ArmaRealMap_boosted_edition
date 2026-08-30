using System.Globalization;
using System.Numerics;
using GameRealisticMap.Arma3.TerrainBuilder;
using GameRealisticMap.ElevationModel;

namespace GameRealisticMap.Reforger
{
    /// <summary>
    /// An entity placement ready for the "Import Objects Extended" Enfusion Workbench plugin.
    /// </summary>
    /// <remarks>
    /// Coordinate system is Enfusion world-local, Y-up: X east, Y elevation (ASL), Z north.
    /// Rotation is in degrees, order Pitch / Yaw / Roll. Scale is uniform.
    /// </remarks>
    public sealed class ReforgerObject
    {
        public ReforgerObject(string? prefab, double x, double y, double z, double pitch, double yaw, double roll, double scale, string? modelName = null)
        {
            Prefab = prefab;
            X = x;
            Y = y;
            Z = z;
            Pitch = pitch;
            Yaw = yaw;
            Roll = roll;
            Scale = scale;
            ModelName = modelName;
        }

        /// <summary>Source Arma 3 model this placement came from, kept for reporting and for the model port pipeline.</summary>
        public string? ModelName { get; }

        /// <summary>False when the source transform was degenerate and produced a NaN angle.</summary>
        public bool IsValid => !double.IsNaN(Yaw) && !double.IsNaN(Pitch) && !double.IsNaN(Roll)
            && !double.IsNaN(X) && !double.IsNaN(Y) && !double.IsNaN(Z);

        /// <summary>Reforger prefab ResourceName (<c>{GUID}Prefabs/.../name.et</c>), or null for a pool-driven placement.</summary>
        public string? Prefab { get; }

        public double X { get; }

        public double Y { get; }

        public double Z { get; }

        public double Pitch { get; }

        public double Yaw { get; }

        public double Roll { get; }

        public double Scale { get; }

        /// <summary>
        /// Builds a placement from a generated <see cref="TerrainBuilderObject"/>. The Arma engine
        /// transform (X east / Y up / Z north) maps one-to-one onto Enfusion world-local axes.
        /// </summary>
        public static ReforgerObject FromTerrainBuilderObject(TerrainBuilderObject obj, IElevationGrid grid, string? prefab)
        {
            var matrix = obj.ToWrpTransform(grid);
            return new ReforgerObject(
                prefab,
                x: matrix.M41,
                y: matrix.M42,
                z: matrix.M43,
                pitch: obj.Pitch,
                yaw: obj.Yaw,
                roll: obj.Roll,
                scale: obj.Scale,
                modelName: obj.Model.Name);
        }

        /// <summary>
        /// Builds a placement straight from a world transform read in a wrp file, without needing the
        /// p3d to be readable: the position is taken from the translation row and the angles are
        /// decomposed the same way <see cref="TerrainBuilderObject"/> does.
        /// </summary>
        /// <remarks>
        /// Used to convert an existing Arma 3 map. The Arma engine frame (X east, Y up, Z north,
        /// origin at the south-west terrain corner) matches the Enfusion world frame one-to-one, so
        /// the translation is copied verbatim.
        /// </remarks>
        public static ReforgerObject FromWrpMatrix(Matrix4x4 wrpMatrix, string? prefab, string? modelName = null)
        {
            var rotateOnly = wrpMatrix;
            rotateOnly.M41 = 0;
            rotateOnly.M42 = 0;
            rotateOnly.M43 = 0;

            var scale = 1f;
            if (Matrix4x4.Decompose(wrpMatrix, out var decomposedScale, out _, out _))
            {
                scale = decomposedScale.X; // Assume uniform scale, like the Terrain Builder export
                if (scale != 1f && Matrix4x4.Invert(Matrix4x4.CreateScale(decomposedScale), out var invertScale))
                {
                    rotateOnly = rotateOnly * invertScale;
                }
            }

            const double toDegrees = 180.0 / Math.PI;
            return new ReforgerObject(
                prefab,
                x: wrpMatrix.M41,
                y: wrpMatrix.M42,
                z: wrpMatrix.M43,
                pitch: Math.Asin(Math.Clamp(-rotateOnly.M23, -1.0, 1.0)) * toDegrees,
                yaw: -Math.Atan2(rotateOnly.M13, rotateOnly.M33) * toDegrees,
                roll: Math.Atan2(rotateOnly.M21, rotateOnly.M22) * toDegrees,
                scale: scale,
                modelName: modelName);
        }

        /// <summary>
        /// One whitespace-separated line in the Import Objects Extended format:
        /// <c>[{GUID}path.et] PosX PosY PosZ Pitch Yaw Roll Scale</c>.
        /// </summary>
        public string ToCsvLine()
        {
            var transform = FormattableString.Invariant(
                $"{X:0.###} {Y:0.###} {Z:0.###} {Pitch:0.###} {Yaw:0.###} {Roll:0.###} {Scale:0.###}");
            return string.IsNullOrEmpty(Prefab) ? transform : $"\"{Prefab}\" {transform}";
        }
    }
}
