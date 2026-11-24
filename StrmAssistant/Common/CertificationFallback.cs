using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace StrmAssistant.Common
{
    internal sealed class CertificationFallbackResult
    {
        public byte[] Content { get; set; }
        public bool Applied { get; set; }
        public string SourceCountry { get; set; }
        public string SourceRating { get; set; }
        public string TargetRating { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Content-rating fallback for TMDB detail responses.
    ///
    /// Emby's MovieDb provider asks for the library country and then falls back to US (for
    /// movies) or takes the first available rating (for series). That leaves two gaps: a movie
    /// with no rating for either country, and a series whose only ratings are from other
    /// countries. In both cases a rating from the configured country chain is translated through
    /// the cross-system map and written as an entry for each chain country that has no rating.
    ///
    /// The transformation is a no-op when the preferred country or US already has a rating, so an
    /// official rating is never replaced, and an unknown source rating is never guessed.
    /// </summary>
    internal static class CertificationFallback
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly Dictionary<string, string> BuiltInMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["G"] = "G", ["PG"] = "PG", ["PG-13"] = "PG-13", ["R"] = "R", ["NC-17"] = "NC-17",
                ["TV-Y"] = "TV-Y", ["TV-Y7"] = "TV-Y7", ["TV-G"] = "TV-G", ["TV-PG"] = "TV-PG",
                ["TV-14"] = "TV-14", ["TV-MA"] = "TV-MA",
                ["FSK 0"] = "G", ["FSK 6"] = "PG", ["FSK 12"] = "PG-13", ["FSK 16"] = "R",
                ["FSK 18"] = "NC-17",
                ["U"] = "G", ["12"] = "PG-13", ["12A"] = "PG-13", ["15"] = "R", ["18"] = "NC-17",
                ["PG12"] = "PG-13", ["R15+"] = "R", ["R18+"] = "NC-17",
                ["Tout public"] = "G", ["10"] = "PG", ["16"] = "R",
                ["ALL"] = "G", ["12세"] = "PG-13", ["15세"] = "R", ["19세"] = "NC-17"
            };

        public static CertificationFallbackResult Apply(byte[] content, LocalMetadataFileKind kind,
            string countries, string extraMappings)
        {
            var result = new CertificationFallbackResult { Content = content };

            if (content is null || content.Length == 0) return result;
            if (kind != LocalMetadataFileKind.Movie && kind != LocalMetadataFileKind.Series)
                return result;

            var chain = ParseCountries(countries);
            if (chain.Count == 0) return result;

            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(Utf8NoBom.GetString(content));
            }
            catch (Exception)
            {
                result.Reason = "response is not valid JSON";
                return result;
            }

            using (document)
            {
                if (!(MiniJson.Parse(document.RootElement) is MiniJsonObject root))
                {
                    result.Reason = "response is not a JSON object";
                    return result;
                }

                var map = BuildMap(extraMappings);

                if (kind == LocalMetadataFileKind.Movie) ApplyMovie(root, chain, map, result);
                else ApplySeries(root, chain, map, result);

                if (!result.Applied) return result;

                try
                {
                    result.Content = MiniJson.Serialize(root);
                }
                catch (Exception)
                {
                    result.Content = content;
                    result.Applied = false;
                    result.Reason = "serialize failed";
                }
            }

            return result;
        }

        private static void ApplyMovie(MiniJsonObject root, IReadOnlyList<string> countries,
            IReadOnlyDictionary<string, string> map, CertificationFallbackResult result)
        {
            var releases = MiniJson.GetObject(root, "releases");
            var entries = MiniJson.GetArray(releases, "countries");

            if (entries is null) return;

            var existing = new List<(string Country, string Rating, MiniJsonObject Node)>();

            foreach (var node in entries.Items)
            {
                if (!(node is MiniJsonObject entry)) continue;

                var country = MiniJson.ReadString(entry, "iso_3166_1");
                var rating = MiniJson.ReadString(entry, "certification");

                if (!string.IsNullOrWhiteSpace(country)) existing.Add((country, rating, entry));
            }

            if (existing.Any(entry => IsOfficialCountry(entry.Country, entry.Rating, countries)))
                return;

            var source = existing.FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry.Rating));
            if (source.Node is null) return;
            if (!TryMap(source.Rating, map, out var target)) return;

            foreach (var country in countries)
            {
                if (existing.Any(entry =>
                        string.Equals(entry.Country, country, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(entry.Rating)))
                {
                    continue;
                }

                var added = new MiniJsonObject();
                added.Members["iso_3166_1"] = new MiniJsonStringValue(country);
                added.Members["certification"] = new MiniJsonStringValue(target);
                added.Members["primary"] = new MiniJsonBoolValue(false);
                added.Members["type"] = new MiniJsonNumberValue(3);
                entries.Items.Add(added);
            }

            result.Applied = true;
            result.SourceCountry = source.Country;
            result.SourceRating = source.Rating;
            result.TargetRating = target;
        }

        private static void ApplySeries(MiniJsonObject root, IReadOnlyList<string> countries,
            IReadOnlyDictionary<string, string> map, CertificationFallbackResult result)
        {
            var ratings = MiniJson.GetObject(root, "content_ratings");
            var entries = MiniJson.GetArray(ratings, "results");

            if (entries is null) return;

            var existing = new List<(string Country, string Rating)>();

            foreach (var node in entries.Items)
            {
                if (!(node is MiniJsonObject entry)) continue;
                existing.Add((MiniJson.ReadString(entry, "iso_3166_1"),
                    MiniJson.ReadString(entry, "rating")));
            }

            if (existing.Any(entry => IsOfficialCountry(entry.Country, entry.Rating, countries)))
                return;

            var source = existing.FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry.Rating));
            if (string.IsNullOrWhiteSpace(source.Country) && string.IsNullOrWhiteSpace(source.Rating))
                return;
            if (!TryMap(source.Rating, map, out var target)) return;

            foreach (var country in countries)
            {
                if (existing.Any(entry =>
                        string.Equals(entry.Country, country, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(entry.Rating)))
                {
                    continue;
                }

                var added = new MiniJsonObject();
                added.Members["iso_3166_1"] = new MiniJsonStringValue(country);
                added.Members["rating"] = new MiniJsonStringValue(target);
                entries.Items.Add(added);
            }

            result.Applied = true;
            result.SourceCountry = source.Country;
            result.SourceRating = source.Rating;
            result.TargetRating = target;
        }

        /// <summary>
        /// Translates a source rating. Extra mappings win over the built-in table; a value that is
        /// already in the target system is returned unchanged. Unknown ratings are not inferred.
        /// </summary>
        public static bool TryMap(string rating, IReadOnlyDictionary<string, string> map,
            out string target)
        {
            target = null;
            if (string.IsNullOrWhiteSpace(rating)) return false;

            var value = rating.Trim();

            if (map != null && map.TryGetValue(value, out var mapped) &&
                !string.IsNullOrWhiteSpace(mapped))
            {
                target = mapped.Trim();
                return true;
            }

            if (BuiltInMap.TryGetValue(value, out mapped) && !string.IsNullOrWhiteSpace(mapped))
            {
                target = mapped.Trim();
                return true;
            }

            if (value.StartsWith("FSK", StringComparison.OrdinalIgnoreCase))
            {
                var compact = value.Substring(3).Trim();

                if (BuiltInMap.TryGetValue("FSK " + compact, out mapped) &&
                    !string.IsNullOrWhiteSpace(mapped))
                {
                    target = mapped.Trim();
                    return true;
                }
            }

            return false;
        }

        public static IReadOnlyList<string> ParseCountries(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', ';', ' ', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim().ToUpperInvariant())
                .Where(part => part.Length == 2 || part.Length == 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyDictionary<string, string> BuildMap(string extraMappings)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in (extraMappings ?? string.Empty)
                         .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var index = pair.IndexOf('=');
                if (index <= 0) continue;

                var key = pair.Substring(0, index).Trim();
                var value = pair.Substring(index + 1).Trim();

                if (key.Length == 0 || value.Length == 0) continue;

                map[key] = value;
            }

            return map;
        }

        private static bool IsOfficialCountry(string country, string rating,
            IReadOnlyList<string> countries)
        {
            if (string.IsNullOrWhiteSpace(rating) || string.IsNullOrWhiteSpace(country)) return false;

            if (string.Equals(country, countries.FirstOrDefault(), StringComparison.OrdinalIgnoreCase))
                return true;

            return string.Equals(country, "US", StringComparison.OrdinalIgnoreCase);
        }
    }
}
