using MediaBrowser.Controller.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Base class for the item blacklists. An item is excluded when it, or any of its
    /// parents (for example the season or series of an episode), carries the configured
    /// tag, or when its name or path matches one of the configured keywords.
    /// </summary>
    public abstract class ItemBlacklist
    {
        private static readonly char[] ListSeparators = { ',', ';', '\r', '\n', '|' };

        private sealed class ParsedKeywords
        {
            public List<string> Keywords;
            public List<Regex> Wildcards;
        }

        // The same keyword string is parsed once and shared by every instance and batch. The
        // compiled wildcard regexes live here, so constructing a blacklist for a batch no longer
        // recompiles them per item.
        private static readonly ConcurrentDictionary<string, ParsedKeywords> ParsedKeywordCache =
            new ConcurrentDictionary<string, ParsedKeywords>(StringComparer.Ordinal);

        // A single parsed instance per subclass and option value. Reusing the instance is what
        // makes _ownMatches useful: one batch no longer pays for the same parent/tag checks over
        // and over. Item changes and option saves clear it so tag edits are never stale.
        private static readonly ConcurrentDictionary<string, object> InstanceCache =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        private readonly ParsedKeywords _parsedKeywords;
        private readonly ConcurrentDictionary<long, bool> _ownMatches =
            new ConcurrentDictionary<long, bool>();
        private readonly string _excludeTag;
        private readonly HashSet<string> _configuredTags;

        protected ItemBlacklist(string excludeTag, string keywords)
            : this(excludeTag, keywords, null)
        {
        }

        /// <summary>
        /// <paramref name="tags"/> is the user-configured tag blacklist (same separators as the
        /// keywords). A tag match excludes the item just like the fixed <paramref name="excludeTag"/>.
        /// </summary>
        protected ItemBlacklist(string excludeTag, string keywords, string tags)
        {
            _excludeTag = excludeTag;
            _parsedKeywords = ParsedKeywordCache.GetOrAdd(keywords ?? string.Empty, ParseKeywords);
            _configuredTags = TagListMatcher.Parse(tags);
        }

        public bool HasKeywords => _parsedKeywords.Keywords.Count > 0;

        protected static T GetShared<T>(string key, Func<T> factory) where T : class
        {
            var cacheKey = typeof(T).FullName + "|" + (key ?? string.Empty);

            return (T) InstanceCache.GetOrAdd(cacheKey, _ => factory());
        }

        /// <summary>
        /// Drops the shared parsed-keyword and instance caches. Called when options are saved; a
        /// changed keyword string would miss the cache anyway, this just releases the old entries.
        /// </summary>
        public static void InvalidateParsedCache()
        {
            ParsedKeywordCache.Clear();
            InstanceCache.Clear();
        }

        /// <summary>
        /// Drops cached per-item decisions after an item changed (for example its tags), while
        /// keeping the parsed keywords and compiled wildcards.
        /// </summary>
        public static void InvalidateInstanceCache()
        {
            InstanceCache.Clear();
        }

        public bool IsExcluded(BaseItem item)
        {
            if (item is null) return false;

            if (MatchesOwnProperties(item)) return true;

            return item.GetParents().Any(MatchesOwnProperties);
        }

        private bool MatchesOwnProperties(BaseItem item)
        {
            if (item is null) return false;

            if (_ownMatches.TryGetValue(item.InternalId, out var cached)) return cached;

            var result = MatchesTag(item) || MatchesKeyword(item);

            _ownMatches[item.InternalId] = result;

            return result;
        }

        private bool MatchesTag(BaseItem item)
        {
            var tags = item.Tags;

            if (tags is null || tags.Length == 0) return false;

            if (_configuredTags.Count > 0)
            {
                foreach (var tag in tags)
                {
                    if (!string.IsNullOrWhiteSpace(tag) && _configuredTags.Contains(tag.Trim())) return true;
                }
            }

            return tags.Any(tag => string.Equals(tag?.Trim(), _excludeTag, StringComparison.OrdinalIgnoreCase));
        }

        private bool MatchesKeyword(BaseItem item)
        {
            if (_parsedKeywords.Keywords.Count == 0) return false;

            return MatchesKeyword(item.Name) || MatchesKeyword(item.Path) ||
                   MatchesKeyword(item.FileNameWithoutExtension);
        }

        private bool MatchesKeyword(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            foreach (var keyword in _parsedKeywords.Keywords)
            {
                if (value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            foreach (var wildcard in _parsedKeywords.Wildcards)
            {
                if (wildcard.IsMatch(value)) return true;
            }

            return false;
        }

        private static ParsedKeywords ParseKeywords(string value)
        {
            var keywords = SplitList(value);
            var wildcards = new List<Regex>();

            foreach (var keyword in keywords)
            {
                if (keyword.IndexOf('*') < 0 && keyword.IndexOf('?') < 0) continue;

                var pattern = "^" + Regex.Escape(keyword).Replace("\\*", ".*").Replace("\\?", ".") + "$";

                wildcards.Add(new Regex(pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
            }

            return new ParsedKeywords
            {
                Keywords = keywords,
                Wildcards = wildcards
            };
        }

        private static List<string> SplitList(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : value.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries)
                    .Select(i => i.Trim())
                    .Where(i => i.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
    }
}
