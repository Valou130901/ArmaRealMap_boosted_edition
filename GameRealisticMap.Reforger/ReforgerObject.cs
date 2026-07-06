using System.Globalization;
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
        public ReforgerObject(string? prefab, double x, double y, double z, double pitch, double yaw, double roll, double scale)
        {
            Prefab = prefab;
            X = x;
            Y = y;
            Z = z;
            Pitch = pitch;
            Yaw = yaw;
            Roll = roll;
            Scale = scale;
        }

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
                scale: obj.Scale);
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
