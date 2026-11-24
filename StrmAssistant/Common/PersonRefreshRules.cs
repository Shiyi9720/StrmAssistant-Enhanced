using System;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Pure decisions for the enhanced Chinese person refresh. Emby represents the same actor as
    /// several Person rows when different seasons were scraped in different languages; these
    /// rules pin how rows are grouped and which fields are worth writing back.
    /// </summary>
    internal static class PersonRefreshRules
    {
        /// <summary>
        /// Groups rows that are the same actor: by TMDB person id when present, otherwise the row
        /// is its own group. The returned key must stay stable for the whole run.
        /// </summary>
        public static string GroupKey(string tmdbId, long internalId)
        {
            return !string.IsNullOrWhiteSpace(tmdbId)
                ? "tmdb:" + tmdbId.Trim()
                : "id:" + internalId;
        }

        /// <summary>
        /// A name is only written when the provider returned a Chinese name, the field is not
        /// locked and the value actually differs.
        /// </summary>
        public static bool ShouldApplyName(string newName, string existingName, bool locked,
            bool newIsChinese)
        {
            return !locked && newIsChinese && !string.IsNullOrWhiteSpace(newName) &&
                   !string.Equals(existingName ?? string.Empty, newName, StringComparison.Ordinal);
        }

        /// <summary>
        /// Overview is the expensive/churny field. A Chinese overview that is already present is
        /// kept as-is (the whole point is to avoid re-scraping it every run); only an empty or
        /// non-Chinese overview is replaced by a Chinese one.
        /// </summary>
        public static bool ShouldApplyOverview(string newOverview, string existingOverview,
            bool locked, bool newIsChinese, Func<string, bool> isChinese)
        {
            if (locked || !newIsChinese || string.IsNullOrWhiteSpace(newOverview)) return false;
            if (string.Equals(existingOverview ?? string.Empty, newOverview, StringComparison.Ordinal))
                return false;

            return string.IsNullOrWhiteSpace(existingOverview) ||
                   !(isChinese?.Invoke(existingOverview) ?? false);
        }

        /// <summary>Birth/death dates are copied whenever the provider has one and it differs.</summary>
        public static bool ShouldApplyDate(DateTimeOffset? newDate, DateTimeOffset? existingDate)
        {
            return newDate.HasValue && newDate != existingDate;
        }
    }
}
