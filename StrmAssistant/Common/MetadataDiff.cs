using System;
using System.Collections.Generic;

namespace StrmAssistant.Common
{
    /// <summary>
    /// The twenty metadata fields the <c>metadata.update</c> notification reports, and the
    /// before/after comparison that turns two field maps into "字段: 旧 → 新" lines.
    ///
    /// Kept free of Emby types on purpose: the field list and the diff rules are the part with
    /// real logic, and keeping them here lets the verification project link this file directly.
    /// <see cref="MetadataSnapshot.FromItem"/> reads the same keys off a BaseItem, so the two
    /// cannot drift.
    /// </summary>
    public static class MetadataDiff
    {
        private const int MaxValueLength = 120;
        private const int MaxChanges = 20;

        /// <summary>The reported fields, in notification order. (Key, label).</summary>
        public static readonly (string Key, string Label)[] Fields =
        {
            ("Name", "名称"),
            ("OriginalTitle", "原名"),
            ("SortName", "排序名"),
            ("Overview", "简介"),
            ("Tagline", "标语"),
            ("OfficialRating", "分级"),
            ("CustomRating", "自定义分级"),
            ("CommunityRating", "社区评分"),
            ("CriticRating", "影评人评分"),
            ("ProductionYear", "年份"),
            ("PremiereDate", "首播日期"),
            ("EndDate", "结束日期"),
            ("RunTimeTicks", "时长"),
            ("Genres", "类型"),
            ("Tags", "标签"),
            ("Studios", "制片公司"),
            ("ProviderIds", "外部 ID"),
            ("ProductionLocations", "制片地区"),
            ("PreferredMetadataLanguage", "元数据语言"),
            ("PreferredMetadataCountryCode", "元数据国家")
        };

        /// <summary>
        /// Returns one "字段: 旧 → 新" line per changed field, in field order. Null maps (the
        /// first observation after startup has no before-image) yield no lines so no empty
        /// "nothing changed" notification is sent. An empty string and a missing key both mean
        /// 空值, so clearing a field is not reported as a change against a missing key.
        /// </summary>
        public static List<string> Compare(IReadOnlyDictionary<string, string> before,
            IReadOnlyDictionary<string, string> after)
        {
            var changes = new List<string>();

            if (before is null || after is null) return changes;

            foreach (var (key, label) in Fields)
            {
                if (changes.Count >= MaxChanges) break;

                before.TryGetValue(key, out var beforeValue);
                after.TryGetValue(key, out var afterValue);

                var left = Normalize(beforeValue);
                var right = Normalize(afterValue);

                if (string.Equals(left, right, StringComparison.Ordinal)) continue;

                changes.Add(label + ": " + Display(left) + " → " + Display(right));
            }

            return changes;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            return value.Trim();
        }

        private static string Display(string value)
        {
            if (string.IsNullOrEmpty(value)) return "（空）";

            var collapsed = value.Replace("\r", " ").Replace("\n", " ");

            return collapsed.Length <= MaxValueLength
                ? collapsed
                : collapsed.Substring(0, MaxValueLength) + "…";
        }
    }
}
