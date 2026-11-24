using StrmAssistant.Common;
using System.Text.Json;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// The passive cache is served back to Emby verbatim, so the annotated body must stay valid
    /// JSON. Regression: the old implementation round-tripped through Emby's untyped serializer
    /// and wrote nested values as the non-JSON "{id: 18, name: Drama}", which made Emby's TMDB
    /// provider throw on a null genre name and drop the response.
    /// </summary>
    internal static class LocalMetadataCaptureTests
    {
        public static void Run(TestRunner runner)
        {
            PreservesStructure(runner);
            IgnoresNonObject(runner);
            IgnoresInvalidJson(runner);
        }

        private static void PreservesStructure(TestRunner runner)
        {
            runner.Section("C1. Capture annotation keeps the TMDB payload valid");

            const string body = "{\"id\":900001,\"adult\":false,\"runtime\":100," +
                                "\"vote_average\":7.5,\"genres\":[{\"id\":18,\"name\":\"Drama\"}]," +
                                "\"credits\":{\"cast\":[{\"name\":\"Tom Hanks\",\"order\":0}]}," +
                                "\"belongs_to_collection\":null}";

            var meta = new { source = "online", request = "Movie:900001", url = "http://x/3/movie/900001" };
            var annotated = LocalMetadataCapture.Annotate(body, meta);

            runner.Check("annotation returns new content", !ReferenceEquals(body, annotated));

            using (var document = JsonDocument.Parse(annotated))
            {
                var root = document.RootElement;

                runner.Check("root stays an object", root.ValueKind == JsonValueKind.Object);
                runner.Check("number stays a number",
                    root.GetProperty("runtime").ValueKind == JsonValueKind.Number);
                runner.Check("double stays a number",
                    root.GetProperty("vote_average").GetDouble() == 7.5);
                runner.Check("bool stays a bool",
                    root.GetProperty("adult").ValueKind == JsonValueKind.False);
                runner.Check("null stays null",
                    root.GetProperty("belongs_to_collection").ValueKind == JsonValueKind.Null);

                var genres = root.GetProperty("genres");
                runner.Check("genres stays an array", genres.ValueKind == JsonValueKind.Array);
                runner.Check("genre name is readable",
                    genres[0].GetProperty("name").GetString() == "Drama");
                runner.Check("cast stays nested",
                    root.GetProperty("credits").GetProperty("cast")[0]
                        .GetProperty("name").GetString() == "Tom Hanks");

                var metaElement = root.GetProperty("_meta");
                runner.Check("meta source kept",
                    metaElement.GetProperty("source").GetString() == "online");
                runner.Check("meta request kept",
                    metaElement.GetProperty("request").GetString() == "Movie:900001");
            }
        }

        private static void IgnoresNonObject(TestRunner runner)
        {
            runner.Section("C2. Non-object bodies are returned untouched");

            const string array = "[1,2,3]";
            runner.Check("array body unchanged",
                ReferenceEquals(array, LocalMetadataCapture.Annotate(array, new { source = "x" })));
        }

        private static void IgnoresInvalidJson(TestRunner runner)
        {
            runner.Section("C3. Invalid JSON is never rewritten");

            const string broken = "{not json";
            runner.Check("invalid body unchanged",
                ReferenceEquals(broken, LocalMetadataCapture.Annotate(broken, new { source = "x" })));
        }
    }
}
