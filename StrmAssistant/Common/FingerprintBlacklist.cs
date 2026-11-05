namespace StrmAssistant.Common
{
    /// <summary>
    /// Excludes programs and seasons from intro fingerprint extraction. Items carrying
    /// the <c>fingerprint-exclude</c> tag, or matching the configured keywords, are skipped.
    /// </summary>
    public sealed class FingerprintBlacklist : ItemBlacklist
    {
        public const string ExcludeTagName = "fingerprint-exclude";

        private FingerprintBlacklist(string keywords) : base(ExcludeTagName, keywords)
        {
        }

        public static FingerprintBlacklist FromOptions()
        {
            var options = Plugin.Instance.GetPluginOptions().IntroSkipOptions;

            return new FingerprintBlacklist(options.FingerprintExcludeKeywords);
        }
    }
}
