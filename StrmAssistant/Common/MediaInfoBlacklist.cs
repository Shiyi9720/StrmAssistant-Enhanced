namespace StrmAssistant.Common
{
    /// <summary>
    /// Excludes items from media info extraction. Items carrying the
    /// <c>mediainfo-exclude</c> tag, or matching the configured keywords, are skipped.
    /// </summary>
    public sealed class MediaInfoBlacklist : ItemBlacklist
    {
        public const string ExcludeTagName = "mediainfo-exclude";

        private MediaInfoBlacklist(string keywords, string tags) : base(ExcludeTagName, keywords, tags)
        {
        }

        public static MediaInfoBlacklist FromOptions()
        {
            var options = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions;
            var keywords = options.MediaInfoExcludeKeywords ?? string.Empty;
            var tags = options.MediaInfoExcludeTags ?? string.Empty;
            var key = keywords + "\u0001" + tags;

            return GetShared(key, () => new MediaInfoBlacklist(keywords, tags));
        }
    }
}
