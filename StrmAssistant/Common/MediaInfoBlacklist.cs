namespace StrmAssistant.Common
{
    /// <summary>
    /// Excludes items from media info extraction. Items carrying the
    /// <c>mediainfo-exclude</c> tag, or matching the configured keywords, are skipped.
    /// </summary>
    public sealed class MediaInfoBlacklist : ItemBlacklist
    {
        public const string ExcludeTagName = "mediainfo-exclude";

        private MediaInfoBlacklist(string keywords) : base(ExcludeTagName, keywords)
        {
        }

        public static MediaInfoBlacklist FromOptions()
        {
            var keywords = Plugin.Instance.GetPluginOptions()
                .MediaInfoExtractOptions.MediaInfoExcludeKeywords ?? string.Empty;

            return GetShared(keywords, () => new MediaInfoBlacklist(keywords));
        }
    }
}
