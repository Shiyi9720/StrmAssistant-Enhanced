using StrmAssistant.Common;
using System.Text;
using System.Text.Json;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Content-rating fallback only fills gaps: an official preferred/US rating is never replaced
    /// and an unknown source rating is never guessed.
    /// </summary>
    internal static class CertificationFallbackTests
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void Run(TestRunner runner)
        {
            MovieFallback(runner);
            MovieOfficial(runner);
            UnknownRating(runner);
            SeriesFallback(runner);
            ExtraMapping(runner);
            Countries(runner);
        }

        private static void MovieFallback(TestRunner runner)
        {
            runner.Section("R1. Movie without US/preferred rating falls back through the chain");

            var json = "{\"releases\":{\"countries\":[{\"iso_3166_1\":\"GB\",\"certification\":\"15\"}]}}";
            var result = CertificationFallback.Apply(Utf8NoBom.GetBytes(json),
                LocalMetadataFileKind.Movie, "US,GB", null);

            using var document = JsonDocument.Parse(result.Content);
            var countries = document.RootElement.GetProperty("releases").GetProperty("countries");
            var us = Find(countries, "US");
            var gb = Find(countries, "GB");

            runner.Check("applied", result.Applied);
            runner.Check("source is GB/15", result.SourceCountry == "GB" && result.SourceRating == "15");
            runner.Check("mapped to R", result.TargetRating == "R");
            runner.Check("US added", us.HasValue && us.Value.GetProperty("certification").GetString() == "R");
            runner.Check("GB not duplicated", gb.HasValue && gb.Value.GetProperty("certification").GetString() == "15");
        }

        private static void MovieOfficial(TestRunner runner)
        {
            runner.Section("R2. An existing US rating is left alone");

            var json = "{\"releases\":{\"countries\":[{\"iso_3166_1\":\"GB\",\"certification\":\"15\"}," +
                       "{\"iso_3166_1\":\"US\",\"certification\":\"PG-13\"}]}}";
            var content = Utf8NoBom.GetBytes(json);
            var result = CertificationFallback.Apply(content,
                LocalMetadataFileKind.Movie, "US,GB", null);

            runner.Check("not applied", !result.Applied);
            runner.Check("content unchanged", ReferenceEquals(content, result.Content));
        }

        private static void UnknownRating(TestRunner runner)
        {
            runner.Section("R3. Unknown ratings are not guessed");

            var json = "{\"releases\":{\"countries\":[{\"iso_3166_1\":\"GB\",\"certification\":\"XYZ\"}]}}";
            var result = CertificationFallback.Apply(Utf8NoBom.GetBytes(json),
                LocalMetadataFileKind.Movie, "US,GB", null);

            runner.Check("not applied", !result.Applied);
        }

        private static void SeriesFallback(TestRunner runner)
        {
            runner.Section("R4. Series content_ratings fallback");

            var json = "{\"content_ratings\":{\"results\":[{\"iso_3166_1\":\"JP\",\"rating\":\"PG12\"}]}}";
            var result = CertificationFallback.Apply(Utf8NoBom.GetBytes(json),
                LocalMetadataFileKind.Series, "US,JP", null);

            using var document = JsonDocument.Parse(result.Content);
            var results = document.RootElement.GetProperty("content_ratings").GetProperty("results");
            var us = Find(results, "US");

            runner.Check("applied", result.Applied);
            runner.Check("US added as PG-13", us.HasValue && us.Value.GetProperty("rating").GetString() == "PG-13");
        }

        private static void ExtraMapping(TestRunner runner)
        {
            runner.Section("R5. Extra mappings override the built-in table");

            var json = "{\"releases\":{\"countries\":[{\"iso_3166_1\":\"GB\",\"certification\":\"15\"}]}}";
            var result = CertificationFallback.Apply(Utf8NoBom.GetBytes(json),
                LocalMetadataFileKind.Movie, "US", "15=TV-MA");

            runner.Check("custom target", result.Applied && result.TargetRating == "TV-MA");

            runner.Check("FSK 16 maps to R",
                CertificationFallback.TryMap("FSK 16", null, out var mapped) && mapped == "R");
            runner.Check("FSK16 without space maps to R",
                CertificationFallback.TryMap("FSK16", null, out var compact) && compact == "R");
        }

        private static void Countries(TestRunner runner)
        {
            runner.Section("R6. Country chain parsing");

            var chain = CertificationFallback.ParseCountries(" us, gb ; jp ,, ");

            runner.Check("count", chain.Count == 3, string.Join(",", chain));
            runner.Check("upper case", chain[0] == "US" && chain[1] == "GB" && chain[2] == "JP");
            runner.Check("distinct", CertificationFallback.ParseCountries("US,us,US").Count == 1);
        }

        private static JsonElement? Find(JsonElement array, string country)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.TryGetProperty("iso_3166_1", out var value) && value.GetString() == country)
                    return item;
            }

            return null;
        }
    }
}
