using MediaBrowser.Controller.Entities;
using System;
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

        private readonly List<string> _keywords;
        private readonly Dictionary<long, bool> _ownMatches = new Dictionary<long, bool>();
        private readonly string _excludeTag;

        protected ItemBlacklist(string excludeTag, string keywords)
        {
            _excludeTag = excludeTag;
            _keywords = SplitList(keywords);
        }

        public bool HasKeywords => _keywords.Count > 0;

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

            return tags.Any(tag => string.Equals(tag?.Trim(), _excludeTag, StringComparison.OrdinalIgnoreCase));
        }

        private bool MatchesKeyword(BaseItem item)
        {
            if (_keywords.Count == 0) return false;

            return MatchesKeyword(item.Name) || MatchesKeyword(item.Path) ||
                   MatchesKeyword(item.FileNameWithoutExtension);
        }

        private bool MatchesKeyword(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            return _keywords.Any(keyword =>
                value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                MatchesWildcard(value, keyword));
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

        private static bool MatchesWildcard(string value, string pattern)
        {
            if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0) return false;

            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";

            return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
