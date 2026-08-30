namespace GameRealisticMap.Reforger.Import
{
    /// <summary>
    /// Coarse family an Arma 3 model belongs to. Used to split the export into one file per family,
    /// so a prefab pool can be assigned per family and so the model port pipeline can be pointed at
    /// the families that have no Reforger equivalent (mainly <see cref="Building"/>).
    /// </summary>
    public enum ReforgerObjectCategory
    {
        Tree,
        Bush,
        Rock,
        Building,
        Wall,
        Fence,
        Infrastructure,
        Water,
        Clutter,
        Other
    }
}
