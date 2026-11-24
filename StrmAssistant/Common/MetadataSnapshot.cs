using MediaBrowser.Controller.Entities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Reads the metadata fields the <c>metadata.update</c> notification reports off a BaseItem.
    /// The diff itself lives in <see cref="MetadataDiff"/> (free of Emby types, so the
    /// verification project can test it); this type supplies the field map that diff consumes.
    /// People are deliberately absent: they are not a BaseItem property (they live in the item
    /// repository) and can be large; the shortcut menu's duplicate-person command covers edits.
    /// </summary>
    public sealed class MetadataSnapshot
    {
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>();

        public string this[string key]
        {
            get => _values.TryGetValue(key ?? string.Empty, out var value) ? value : null;
            private set => _values[key ?? string.Empty] = value;
        }

        /// <summary>Reads the reported fields off an item into a field map.</summary>
        public static MetadataSnapshot FromItem(BaseItem item)
        {
            var snapshot = new MetadataSnapshot();

            if (item is null) return snapshot;

            string Join(IEnumerable<string> values) =>
                values is null
                    ? string.Empty
                    : string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v)));

            snapshot["Name"] = item.Name;
            snapshot["OriginalTitle"] = item.OriginalTitle;
            snapshot["SortName"] = item.SortName;
            snapshot["Overview"] = item.Overview;
            snapshot["Tagline"] = item.Tagline;
            snapshot["OfficialRating"] = item.OfficialRating;
            snapshot["CustomRating"] = item.CustomRating;
            snapshot["CommunityRating"] =
                item.CommunityRating?.ToString(CultureInfo.InvariantCulture);
            snapshot["CriticRating"] = item.CriticRating?.ToString(CultureInfo.InvariantCulture);
            snapshot["ProductionYear"] =
                item.ProductionYear?.ToString(CultureInfo.InvariantCulture);
            snapshot["PremiereDate"] =
                item.PremiereDate?.ToString("O", CultureInfo.InvariantCulture);
            snapshot["EndDate"] = item.EndDate?.ToString("O", CultureInfo.InvariantCulture);
            snapshot["RunTimeTicks"] =
                item.RunTimeTicks?.ToString(CultureInfo.InvariantCulture);
            snapshot["Genres"] = Join(item.Genres);
            snapshot["Tags"] = Join(item.Tags);
            snapshot["Studios"] = Join(item.Studios);
            snapshot["ProviderIds"] = item.ProviderIds is null
                ? string.Empty
                : string.Join(", ", item.ProviderIds
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => p.Key + ":" + p.Value));
            snapshot["ProductionLocations"] = Join(item.ProductionLocations);
            snapshot["PreferredMetadataLanguage"] = item.PreferredMetadataLanguage;
            snapshot["PreferredMetadataCountryCode"] = item.PreferredMetadataCountryCode;

            return snapshot;
        }

        /// <summary>The captured field map, consumed by <see cref="MetadataDiff.Compare"/>.</summary>
        internal IReadOnlyDictionary<string, string> Values => _values;
    }
}
