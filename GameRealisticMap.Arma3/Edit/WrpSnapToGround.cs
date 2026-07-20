namespace GameRealisticMap.Arma3.Edit
{
    public class WrpSnapToGround
    {
        public WrpSnapToGround(string model = "", bool isPattern = false, float minDistance = 0.5f, bool includeBuried = false)
        {
            Model = model;
            IsPattern = isPattern;
            MinDistance = minDistance;
            IncludeBuried = includeBuried;
        }

        /// <summary>
        /// Model filter. Empty matches every object.
        /// </summary>
        public string Model { get; set; }

        /// <summary>
        /// When true, <see cref="Model"/> is a substring matched against the object model path
        /// (case-insensitive), so a whole category can be snapped at once (e.g. "tree", "rock").
        /// </summary>
        public bool IsPattern { get; set; }

        /// <summary>
        /// Objects are snapped only if they are more than this distance (in meters) above the ground.
        /// </summary>
        public float MinDistance { get; set; }

        /// <summary>
        /// When true, objects more than <see cref="MinDistance"/> below the ground are also raised back to it.
        /// </summary>
        public bool IncludeBuried { get; set; }
    }
}
