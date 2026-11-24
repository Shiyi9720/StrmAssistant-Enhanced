using System;
using System.Collections.Generic;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Builds the external-id search keys for one provider value.
    ///
    /// The index stores the bare id ("1291546") so an id copied without a prefix still matches,
    /// and the prefixed form ("douban1291546") so a user can type <c>douban:1291546</c>,
    /// <c>bgm:123456</c>, <c>tmdb:550</c> or <c>imdb:tt0111161</c>. Compact() drops the colon,
    /// so the query projection and the index keys agree without a separate parser. Bangumi is
    /// stored as "Bgm" by some providers and "Bangumi" by others, so both spellings are indexed.
    ///
    /// Kept free of Emby types so the verification project links it directly.
    /// </summary>
    public static class ProviderSearchKeys
    {
        // Canonical provider key (as produced by SearchIndexService.GetSearchableProviderIds)
        // -> the prefixes a user may type. "bgmid" maps to both bangumi spellings.
        private static readonly Dictionary<string, string[]> Prefixes =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["doubanid"] = new[] { "douban" },
                ["bgmid"] = new[] { "bgm", "bangumi" },
                ["imdbid"] = new[] { "imdb" },
                ["tmdbid"] = new[] { "tmdb" },
                ["tvdbid"] = new[] { "tvdb" }
            };

        /// <summary>
        /// The compacted keys to index for one provider pair. Always includes the bare id; adds
        /// the prefixed aliases when the provider is recognised. Empty when the value is blank.
        /// </summary>
        public static IEnumerable<string> KeysFor(string providerKey, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) yield break;

            var bare = SearchTextHelper.Compact(value);
            if (!string.IsNullOrEmpty(bare)) yield return bare;

            var canonical = SearchTextHelper.Compact(providerKey);

            if (canonical is null || !Prefixes.TryGetValue(canonical, out var prefixes)) yield break;

            foreach (var prefix in prefixes)
            {
                var prefixed = SearchTextHelper.Compact(prefix + value);
                if (!string.IsNullOrEmpty(prefixed) && !string.Equals(prefixed, bare, StringComparison.Ordinal))
                {
                    yield return prefixed;
                }
            }
        }
    }
}
