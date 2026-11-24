using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Pure rules for localizing credited people with Douban's data. Douban's celebrities payload
    /// carries both the Chinese name and the latin name; TMDB (and therefore Emby) often stored
    /// only the latin name for guest actors. The match is done on the normalized latin name, and
    /// the result is written as one complete people list because UpdatePeople replaces it.
    /// </summary>
    internal static class DoubanCastRules
    {
        public const int ActorLimit = 20;
        public const int DirectorLimit = 10;

        /// <summary>Trim, collapse whitespace and lower-case: "Tom  Hanks" == "tom hanks".</summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            var parts = name.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts).ToLowerInvariant();
        }

        /// <summary>
        /// Builds latin-name -> Chinese-name from credits that carry both. Empty entries and
        /// credits whose name equals the latin name (nothing to localize) are skipped. A later
        /// credit wins, which matches the celebrities payload's actor-then-director order.
        /// </summary>
        public static Dictionary<string, string> BuildLatinNameMap(
            IEnumerable<(string Name, string LatinName)> credits)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var credit in credits ?? Enumerable.Empty<(string, string)>())
            {
                if (string.IsNullOrWhiteSpace(credit.Name) || string.IsNullOrWhiteSpace(credit.LatinName))
                    continue;

                var key = NormalizeName(credit.LatinName);
                if (key.Length == 0) continue;

                var name = credit.Name.Trim();
                if (string.Equals(name, credit.LatinName.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                map[key] = name;
            }

            return map;
        }

        /// <summary>
        /// Resolves the Chinese name for an existing person; false when there is no match or the
        /// match is the same text.
        /// </summary>
        public static bool TryResolveChineseName(string existingName,
            IReadOnlyDictionary<string, string> latinNameMap, out string chineseName)
        {
            chineseName = null;
            if (latinNameMap is null || string.IsNullOrWhiteSpace(existingName)) return false;

            var key = NormalizeName(existingName);
            if (key.Length == 0 || !latinNameMap.TryGetValue(key, out var resolved)) return false;
            if (string.IsNullOrWhiteSpace(resolved)) return false;
            if (string.Equals(existingName.Trim(), resolved.Trim(), StringComparison.Ordinal)) return false;

            chineseName = resolved.Trim();
            return true;
        }
    }
}
