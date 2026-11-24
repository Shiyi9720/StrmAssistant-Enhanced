using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Matches an item's tags against a user-configured tag blacklist. Tags are identifiers, so
    /// matching is exact after trimming and case-insensitive; unlike the keyword blacklist,
    /// wildcards are deliberately not expanded here. Accepts the same comma/semicolon/newline
    /// separators as the keyword blacklist so the two sit side by side in the options page.
    ///
    /// Kept free of Emby types so the verification project links it directly.
    /// </summary>
    public static class TagListMatcher
    {
        private static readonly char[] Separators = { ',', ';', '\r', '\n', '|' };

        /// <summary>Parses the configured list into a case-insensitive set.</summary>
        public static HashSet<string> Parse(string configured)
        {
            return (configured ?? string.Empty)
                .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>True when any of the item's tags is in the configured blacklist.</summary>
        public static bool Matches(IEnumerable<string> tags, string configured)
        {
            if (tags is null || string.IsNullOrWhiteSpace(configured)) return false;

            var wanted = Parse(configured);

            if (wanted.Count == 0) return false;

            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;

                if (wanted.Contains(tag.Trim())) return true;
            }

            return false;
        }
    }
}
