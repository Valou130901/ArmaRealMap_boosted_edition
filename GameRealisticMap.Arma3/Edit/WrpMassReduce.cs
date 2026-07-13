namespace GameRealisticMap.Arma3.Edit
{
    public class WrpMassReduce
    {
        public WrpMassReduce(string model, double removeRatio, bool isPattern = false)
        {
            RemoveRatio = removeRatio;
            Model = model;
            IsPattern = isPattern;
        }

        public double RemoveRatio { get; set; }

        public string Model { get; set; }

        /// <summary>
        /// When true, <see cref="Model"/> is a substring matched against the object model path
        /// (case-insensitive), so a whole category can be reduced at once (e.g. "tree", "bush").
        /// </summary>
        public bool IsPattern { get; set; }
    }
}