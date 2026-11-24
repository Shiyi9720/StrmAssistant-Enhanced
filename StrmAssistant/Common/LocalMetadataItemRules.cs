using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace StrmAssistant.Common
{
    internal sealed class LocalEpisodeOrderEntry
    {
        public int Season { get; set; }
        public int Episode { get; set; }
        public int AirOrder { get; set; }
        public bool HasSeason { get; set; }
    }

    /// <summary>
    /// Pure readers for the per-item advanced files. Emby-free so the parsing and matching rules
    /// are covered by the verification project.
    /// </summary>
    internal static class LocalMetadataItemRules
    {
        private static readonly Dictionary<string, string> ProviderIdAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tmdb"] = "Tmdb",
                ["imdb"] = "Imdb",
                ["tvdb"] = "Tvdb",
                ["tvrage"] = "TvRage",
                ["douban"] = "Douban",
                ["bgm"] = "Bangumi",
                ["bangumi"] = "Bangumi"
            };

        /// <summary>
        /// Normalizes external-id keys to the provider names Emby stores. An empty value is
        /// omitted; numeric JSON values are accepted and converted to their invariant string.
        /// </summary>
        public static Dictionary<string, string> ReadExternalIds(byte[] content)
        {
            var document = TryParseObject(content);
            if (document is null) return null;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var key = string.IsNullOrWhiteSpace(property.Name) ? null : property.Name.Trim();
                if (key is null) continue;

                var provider = ProviderIdAliases.TryGetValue(key, out var canonical) ? canonical : key;
                string value = null;

                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    value = property.Value.GetString();
                }
                else if (property.Value.ValueKind == JsonValueKind.Number)
                {
                    value = property.Value.GetRawText();
                }

                if (string.IsNullOrWhiteSpace(value)) continue;

                result[provider] = value.Trim();
            }

            document.Dispose();

            return result.Count == 0 ? null : result;
        }

        /// <summary>
        /// Parses episodeorder.json. Supported shapes:
        /// <code>
        /// {"episodes": [{"season": 1, "episode": 2, "air_order": 7}]}
        /// {"episodes": [{"episode_number": 5, "order": 3}]}   // legacy specials.json
        /// </code>
        /// A season-less entry is a special (season 0), matching the old plugin.
        /// </summary>
        public static List<LocalEpisodeOrderEntry> ReadEpisodeOrder(byte[] content)
        {
            var document = TryParseObject(content);
            if (document is null) return null;

            JsonElement array = default;
            var found = false;

            if (document.RootElement.TryGetProperty("episodes", out var episodes) &&
                episodes.ValueKind == JsonValueKind.Array)
            {
                array = episodes;
                found = true;
            }
            else if (document.RootElement.TryGetProperty("specials", out var specials) &&
                     specials.ValueKind == JsonValueKind.Array)
            {
                array = specials;
                found = true;
            }

            if (!found)
            {
                document.Dispose();
                return null;
            }

            var entries = new List<LocalEpisodeOrderEntry>();

            foreach (var node in array.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;

                var season = ReadInt(node, "season");
                var episode = ReadInt(node, "episode") ?? ReadInt(node, "episode_number");
                var order = ReadInt(node, "air_order") ?? ReadInt(node, "order") ??
                            ReadInt(node, "sort_index_number") ?? ReadInt(node, "sortIndexNumber");

                if (!episode.HasValue || !order.HasValue) continue;

                entries.Add(new LocalEpisodeOrderEntry
                {
                    Season = season ?? 0,
                    Episode = episode.Value,
                    AirOrder = order.Value,
                    HasSeason = season.HasValue
                });
            }

            document.Dispose();

            return entries.Count == 0 ? null : entries;
        }

        /// <summary>
        /// Finds the air order for (season, episode). A season-less entry only matches season 0.
        /// </summary>
        public static int? TryResolveAirOrder(IReadOnlyList<LocalEpisodeOrderEntry> entries,
            int season, int? episode)
        {
            if (entries is null || !episode.HasValue) return null;

            foreach (var entry in entries)
            {
                if (entry is null || entry.Episode != episode.Value) continue;

                if (entry.HasSeason)
                {
                    if (entry.Season != season) continue;
                }
                else if (season != 0)
                {
                    continue;
                }

                return entry.AirOrder;
            }

            return null;
        }

        private static JsonDocument TryParseObject(byte[] content)
        {
            if (content is null || content.Length == 0) return null;

            try
            {
                var document = JsonDocument.Parse(new UTF8Encoding(false).GetString(content));

                if (document.RootElement.ValueKind == JsonValueKind.Object) return document;

                document.Dispose();
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int? ReadInt(JsonElement node, string name)
        {
            if (!node.TryGetProperty(name, out var value)) return null;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), out var parsed))
                return parsed;

            return null;
        }
    }
}
