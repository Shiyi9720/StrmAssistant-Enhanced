using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// patches/*.json is a field-level projection of a TMDB response: objects merge, arrays and
    /// scalars replace, null removes, files apply in name order, and a bad patch never breaks the
    /// response.
    /// </summary>
    internal static class LocalMetadataPatcherTests
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void Run(TestRunner runner)
        {
            RecursiveMerge(runner);
            NullRemoves(runner);
            FileOrder(runner);
            BadPatchSkipped(runner);
            NoOpPatch(runner);
        }

        private static void RecursiveMerge(TestRunner runner)
        {
            runner.Section("L1. Objects merge recursively; arrays replace");

            var baseJson = "{\"title\":\"A\",\"nested\":{\"a\":1,\"b\":2},\"tags\":[\"x\"]}";
            var patch = "{\"nested\":{\"b\":3,\"c\":4},\"tags\":[\"y\"]}";

            var result = Apply(baseJson, new Dictionary<string, string> { ["p.json"] = patch });

            using var document = JsonDocument.Parse(result.Content);
            var root = document.RootElement;

            runner.Check("changed", result.Changed && result.AppliedCount == 1, result.Error);
            runner.Check("untouched scalar", root.GetProperty("title").GetString() == "A");
            runner.Check("nested kept", root.GetProperty("nested").GetProperty("a").GetInt32() == 1);
            runner.Check("nested replaced", root.GetProperty("nested").GetProperty("b").GetInt32() == 3);
            runner.Check("nested added", root.GetProperty("nested").GetProperty("c").GetInt32() == 4);
            runner.Check("array replaced", root.GetProperty("tags").GetArrayLength() == 1 &&
                                           root.GetProperty("tags")[0].GetString() == "y");
        }

        private static void NullRemoves(TestRunner runner)
        {
            runner.Section("L2. A null patch value removes the key");

            var result = Apply("{\"nested\":{\"a\":1,\"b\":2}}",
                new Dictionary<string, string> { ["p.json"] = "{\"nested\":{\"a\":null}}" });

            using var document = JsonDocument.Parse(result.Content);
            var nested = document.RootElement.GetProperty("nested");

            runner.Check("removed", !nested.TryGetProperty("a", out _));
            runner.Check("kept sibling", nested.GetProperty("b").GetInt32() == 2);
        }

        private static void FileOrder(TestRunner runner)
        {
            runner.Section("L3. Later file names win");

            var files = new Dictionary<string, string>
            {
                ["01-base.json"] = "{\"title\":\"first\"}",
                ["02-local.json"] = "{\"title\":\"second\"}"
            };

            var result = Apply("{\"title\":\"base\"}", files);
            using var document = JsonDocument.Parse(result.Content);

            runner.Check("02 wins", document.RootElement.GetProperty("title").GetString() == "second");
            runner.Check("both applied", result.AppliedCount == 2);
        }

        private static void BadPatchSkipped(TestRunner runner)
        {
            runner.Section("L4. A malformed patch is skipped and reported");

            var files = new Dictionary<string, string>
            {
                ["01-bad.json"] = "{not json",
                ["02-good.json"] = "{\"title\":\"ok\"}"
            };

            var result = Apply("{\"title\":\"base\"}", files);
            using var document = JsonDocument.Parse(result.Content);

            runner.Check("good patch applied", document.RootElement.GetProperty("title").GetString() == "ok");
            runner.Check("error reported", !string.IsNullOrEmpty(result.Error));
            runner.Check("only one applied", result.AppliedCount == 1);

            var arrayPatch = Apply("{\"title\":\"base\"}",
                new Dictionary<string, string> { ["p.json"] = "[\"x\"]" });

            runner.Check("array patch rejected", !arrayPatch.Changed &&
                                                  arrayPatch.Error.Contains("JSON object"));
        }

        private static void NoOpPatch(TestRunner runner)
        {
            runner.Section("L5. An empty object patch is a no-op");

            var content = Utf8NoBom.GetBytes("{\"title\":\"base\"}");
            var result = LocalMetadataPatcher.Apply(content,
                new[] { "p.json" }, _ => "{}");

            runner.Check("not changed", !result.Changed);
            runner.Check("same content reference", ReferenceEquals(content, result.Content));
        }

        private static LocalPatchResult Apply(string baseJson, Dictionary<string, string> patches)
        {
            var order = new List<string>(patches.Keys);
            order.Sort(StringComparer.OrdinalIgnoreCase);

            return LocalMetadataPatcher.Apply(Utf8NoBom.GetBytes(baseJson), order,
                path => patches[path]);
        }
    }
}
