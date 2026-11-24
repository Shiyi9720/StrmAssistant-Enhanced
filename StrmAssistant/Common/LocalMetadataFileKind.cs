namespace StrmAssistant.Common
{
    /// <summary>
    /// Emby-free item kind used by the advanced local metadata files. Kept separate from the
    /// Emby-facing LocalMetadataSource target type so the pure helpers can be unit-tested.
    /// </summary>
    internal enum LocalMetadataFileKind
    {
        Movie,
        Series,
        Season,
        Episode,
        Collection
    }
}
