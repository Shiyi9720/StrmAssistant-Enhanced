namespace StrmAssistant.Common
{
    /// <summary>
    /// Result of one <see cref="SubtitleApi.DownloadSubtitles"/> attempt. The scheduled task
    /// needs to tell apart "nothing to download" (normal) from "the provider is missing"
    /// (a real failure that used to be counted as success).
    /// </summary>
    public enum SubtitleDownloadOutcome
    {
        /// <summary>At least one subtitle was downloaded.</summary>
        Downloaded,

        /// <summary>Every target language already had a subtitle or had no match.</summary>
        NoResult,

        /// <summary>The library explicitly disables the OpenSubtitles fetcher.</summary>
        DisabledForLibrary,

        /// <summary>Emby's subtitle manager or the OpenSubtitles provider is not available.</summary>
        ProviderUnavailable
    }
}
