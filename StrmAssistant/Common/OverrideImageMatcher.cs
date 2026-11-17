using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Naming rules and short-lived directory listings for override image discovery.
    ///
    /// The rules here are the single source of truth used by <see cref="OverrideImageService"/>.
    /// Keeping the directory listing for a couple of minutes (and re-reading it as soon as the
    /// directory's write time changes) avoids one full <see cref="Directory.GetFiles(string)"/>
    /// per episode while preserving the original matching behaviour: zero-padded numbers,
    /// any casing and all extension variants still resolve exactly as before.
    /// </summary>
    internal static class OverrideImageMatcher
    {
        private static readonly Regex SeasonEpisodeImageRegex = new Regex(
            @"^(?:season-(?<season>\d+)|episode-(?<episodeSeason>\d+)-(?<episode>\d+))\.(?<extension>jpg|jpeg|png|webp)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ImageTypeRegex = new Regex(@"^(?<type>[a-z]+)\.[a-z0-9]+$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
        private const int MaxCacheEntries = 512;

        private static readonly ConcurrentDictionary<string, CachedDirectory> DirectoryCache =
            new ConcurrentDictionary<string, CachedDirectory>(StringComparer.Ordinal);

        private static long _enumerationCount;

        /// <summary>
        /// Number of times a directory was actually enumerated. Used by the regression tests to
        /// assert that repeated lookups hit the cache.
        /// </summary>
        internal static long EnumerationCount => Interlocked.Read(ref _enumerationCount);

        /// <summary>
        /// Returns the files in <paramref name="directory"/>. A missing directory yields an empty
        /// list. The listing is reused while both the directory write time and the short TTL are
        /// unchanged, so newly added or removed files are visible immediately on the next lookup.
        /// </summary>
        internal static IReadOnlyList<string> GetFiles(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return Array.Empty<string>();

            if (!Directory.Exists(directory))
            {
                DirectoryCache.TryRemove(directory, out _);
                return Array.Empty<string>();
            }

            var lastWriteUtc = Directory.GetLastWriteTimeUtc(directory);
            var nowTicks = DateTime.UtcNow.Ticks;

            if (DirectoryCache.TryGetValue(directory, out var cached) &&
                cached.LastWriteUtc == lastWriteUtc &&
                nowTicks - cached.CachedAtTicks <= CacheLifetime.Ticks)
            {
                return cached.Files;
            }

            var files = Directory.GetFiles(directory);
            DirectoryCache[directory] = new CachedDirectory
            {
                Files = files,
                LastWriteUtc = lastWriteUtc,
                CachedAtTicks = nowTicks
            };

            Interlocked.Increment(ref _enumerationCount);
            TrimCache(nowTicks);

            return files;
        }

        /// <summary>
        /// Matches the flat override naming rules.
        /// Season mode accepts <c>season-N.ext</c>; episode mode accepts
        /// <c>episode-S-E.ext</c> (and rejects season-only names). Extensions are limited to
        /// jpg/jpeg/png/webp, matching is case-insensitive and leading zeroes are accepted.
        /// </summary>
        internal static bool IsSeasonEpisodeImage(string fileName, int season, int? episode)
        {
            var match = SeasonEpisodeImageRegex.Match(fileName);

            if (!match.Success) return false;

            if (episode.HasValue)
            {
                return match.Groups["episode"].Success &&
                       int.TryParse(match.Groups["episodeSeason"].Value, out var imageSeason) &&
                       int.TryParse(match.Groups["episode"].Value, out var imageEpisode) &&
                       imageSeason == season && imageEpisode == episode.Value;
            }

            return !match.Groups["episode"].Success &&
                   int.TryParse(match.Groups["season"].Value, out var seasonOnly) &&
                   seasonOnly == season;
        }

        /// <summary>
        /// Matches <c>&lt;type&gt;.&lt;extension&gt;</c> as used by the directory-based layout.
        /// The type must be letters only; the extension may be any alphanumeric run (the caller
        /// then restricts the type to the allowed set), exactly as before.
        /// </summary>
        internal static bool TryGetImageTypeName(string fileName, out string typeName)
        {
            var match = ImageTypeRegex.Match(fileName);

            if (!match.Success)
            {
                typeName = null;
                return false;
            }

            typeName = match.Groups["type"].Value.ToLowerInvariant();
            return true;
        }

        internal static void ClearCache()
        {
            DirectoryCache.Clear();
            Interlocked.Exchange(ref _enumerationCount, 0);
        }

        private static void TrimCache(long nowTicks)
        {
            if (DirectoryCache.Count <= MaxCacheEntries) return;

            foreach (var pair in DirectoryCache)
            {
                if (nowTicks - pair.Value.CachedAtTicks > CacheLifetime.Ticks)
                {
                    DirectoryCache.TryRemove(pair.Key, out _);
                }
            }

            if (DirectoryCache.Count > MaxCacheEntries)
            {
                DirectoryCache.Clear();
            }
        }

        private sealed class CachedDirectory
        {
            public string[] Files { get; set; }
            public DateTime LastWriteUtc { get; set; }
            public long CachedAtTicks { get; set; }
        }
    }
}
