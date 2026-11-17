using System;
using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Regression coverage for the corrupt-scope distinction. An empty value keeps the
    /// documented "all libraries" meaning; a non-empty value where nothing parses must be
    /// reported as malformed so the resolver can fail safe instead of widening to all libraries.
    /// </summary>
    internal static class LibraryScopeParserTests
    {
        public static void Run(TestRunner runner)
        {
            UnsetIsEmpty(runner);
            ValidIdsAreWellFormed(runner);
            CorruptValuesAreMalformed(runner);
            MixedValuesKeepTheParsableIds(runner);
            FavoritesIdParses(runner);
        }

        private static void UnsetIsEmpty(TestRunner runner)
        {
            runner.Section("scope parser - unset values mean all libraries");

            Check(runner, "null scope", null, LibraryScopeKind.Empty, 0);
            Check(runner, "empty scope", "", LibraryScopeKind.Empty, 0);
            Check(runner, "whitespace scope", "   ", LibraryScopeKind.Empty, 0);
            Check(runner, "only separators", ",,,", LibraryScopeKind.Empty, 0);
        }

        private static void ValidIdsAreWellFormed(TestRunner runner)
        {
            runner.Section("scope parser - valid id lists");

            Check(runner, "single id", "163843", LibraryScopeKind.WellFormed, 1);
            Check(runner, "two ids", "163843,163844", LibraryScopeKind.WellFormed, 2);
            Check(runner, "spaces around ids", "163843, 163844", LibraryScopeKind.WellFormed, 2);
            Check(runner, "empty segments ignored", "163843,,163844,", LibraryScopeKind.WellFormed, 2);
        }

        private static void CorruptValuesAreMalformed(TestRunner runner)
        {
            runner.Section("scope parser - corrupt values are malformed, not empty");

            Check(runner, "letters only", "abc", LibraryScopeKind.Malformed, 0);
            Check(runner, "multiple garbage segments", "abc,def", LibraryScopeKind.Malformed, 0);
            Check(runner, "path-like garbage", "/media/tv", LibraryScopeKind.Malformed, 0);
            Check(runner, "truncated number", "16384x", LibraryScopeKind.Malformed, 0);
        }

        private static void MixedValuesKeepTheParsableIds(TestRunner runner)
        {
            runner.Section("scope parser - a single parsable id keeps the value usable");

            var kind = LibraryScopeParser.Classify("abc,163843", out var ids);

            runner.Check("mixed value is well formed", kind == LibraryScopeKind.WellFormed,
                "kind=" + kind);
            runner.EqualLong("mixed value keeps one id", 1, ids.Length);
            runner.EqualLong("mixed value keeps the numeric id", 163843, ids.Length == 1 ? ids[0] : -1);
        }

        private static void FavoritesIdParses(TestRunner runner)
        {
            runner.Section("scope parser - the favorites pseudo id is a valid segment");

            var kind = LibraryScopeParser.Classify("-1", out var ids);

            runner.Check("favorites id is well formed", kind == LibraryScopeKind.WellFormed,
                "kind=" + kind);
            runner.EqualLong("favorites id kept", -1, ids.Length == 1 ? ids[0] : 0);
        }

        private static void Check(TestRunner runner, string name, string scope,
            LibraryScopeKind expectedKind, int expectedCount)
        {
            var kind = LibraryScopeParser.Classify(scope, out var ids);

            runner.Check(name + " -> " + expectedKind, kind == expectedKind, "got " + kind);
            runner.EqualLong(name + " -> id count", expectedCount, ids.Length);
        }
    }
}