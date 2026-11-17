using System;
using System.IO;
using System.Threading;
using StrmAssistant.Common;

namespace StrmAssistant.Verification
{
    internal static class OverrideImageMatcherTests
    {
        public static void Run(TestRunner runner)
        {
            FlatSeasonNames(runner);
            FlatEpisodeNames(runner);
            ImageTypeNames(runner);
            DirectoryListingCache(runner);
        }

        private static void FlatSeasonNames(TestRunner runner)
        {
            runner.Section("OverrideImageMatcher - flat season names");

            CheckSeason(runner, "season-1.jpg", 1, true);
            CheckSeason(runner, "season-01.jpg", 1, true);
            CheckSeason(runner, "season-001.PNG", 1, true);
            CheckSeason(runner, "SEASON-2.JPEG", 2, true);
            CheckSeason(runner, "Season-2.WebP", 2, true);
            CheckSeason(runner, "season-2.jpg", 1, false);
            CheckSeason(runner, "season-1.gif", 1, false);
            CheckSeason(runner, "season-1.avif", 1, false);
            CheckSeason(runner, "season-1.heic", 1, false);
            CheckSeason(runner, "season-1.jpg.bak", 1, false);
            CheckSeason(runner, "season-1", 1, false);
            CheckSeason(runner, "season-.jpg", 1, false);
            CheckSeason(runner, "season-1a.jpg", 1, false);
            CheckSeason(runner, "season-1-extra.jpg", 1, false);
            CheckSeason(runner, "season--1.jpg", 1, false);
            CheckSeason(runner, "season-99999999999.jpg", 1, false);
            CheckSeason(runner, "episode-1-2.jpg", 1, false);
        }

        private static void FlatEpisodeNames(TestRunner runner)
        {
            runner.Section("OverrideImageMatcher - flat episode names");

            CheckEpisode(runner, "episode-1-2.jpg", 1, 2, true);
            CheckEpisode(runner, "episode-01-002.png", 1, 2, true);
            CheckEpisode(runner, "EPISODE-1-2.WEBP", 1, 2, true);
            CheckEpisode(runner, "Episode-1-02.Jpeg", 1, 2, true);
            CheckEpisode(runner, "episode-1-3.jpg", 1, 2, false);
            CheckEpisode(runner, "episode-2-2.jpg", 1, 2, false);
            CheckEpisode(runner, "episode-1-2.gif", 1, 2, false);
            CheckEpisode(runner, "episode-1-2", 1, 2, false);
            CheckEpisode(runner, "episode-1-2-3.jpg", 1, 2, false);
            CheckEpisode(runner, "episode--1-2.jpg", 1, 2, false);
            CheckEpisode(runner, "episode-99999999999-2.jpg", 1, 2, false);
            CheckEpisode(runner, "season-1.jpg", 1, 2, false);
        }

        private static void ImageTypeNames(TestRunner runner)
        {
            runner.Section("OverrideImageMatcher - directory image names");

            CheckType(runner, "poster.jpg", "poster", true);
            CheckType(runner, "POSTER.JPG", "poster", true);
            CheckType(runner, "fanart.png", "fanart", true);
            CheckType(runner, "clearlogo.webp", "clearlogo", true);
            CheckType(runner, "poster.avif", "poster", true);
            CheckType(runner, "poster.123", "poster", true);
            CheckType(runner, "poster.", null, false);
            CheckType(runner, "poster", null, false);
            CheckType(runner, "my-poster.jpg", null, false);
            CheckType(runner, "poster.jpg.tmp", null, false);
            CheckType(runner, "海报.jpg", null, false);
            CheckType(runner, "backdrop2.jpg", null, false);
        }

        private static void DirectoryListingCache(TestRunner runner)
        {
            runner.Section("OverrideImageMatcher - directory listing cache");

            OverrideImageMatcher.ClearCache();

            var directory = Path.Combine(Path.GetTempPath(),
                "strmassistant-verification-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(directory);

                var before = OverrideImageMatcher.EnumerationCount;
                var empty = OverrideImageMatcher.GetFiles(directory);
                runner.Check("empty directory returns no files", empty.Count == 0);
                runner.EqualLong("empty directory enumerates once",
                    before + 1, OverrideImageMatcher.EnumerationCount);

                File.WriteAllText(Path.Combine(directory, "season-1.jpg"), "x");

                var afterAdd = OverrideImageMatcher.EnumerationCount;
                var first = OverrideImageMatcher.GetFiles(directory);
                runner.Check("first lookup sees the new file", first.Count == 1);
                runner.EqualLong("first lookup enumerates", afterAdd + 1,
                    OverrideImageMatcher.EnumerationCount);

                var second = OverrideImageMatcher.GetFiles(directory);
                runner.Check("second lookup returns the same file", second.Count == 1);
                runner.EqualLong("second lookup uses the cache", afterAdd + 1,
                    OverrideImageMatcher.EnumerationCount);

                Thread.Sleep(30);
                File.WriteAllText(Path.Combine(directory, "season-2.jpg"), "x");
                TouchDirectory(directory);

                var third = OverrideImageMatcher.GetFiles(directory);
                runner.Check("changed directory refreshes the listing", third.Count == 2);
                runner.EqualLong("changed directory enumerates again", afterAdd + 2,
                    OverrideImageMatcher.EnumerationCount);

                Thread.Sleep(30);
                File.Delete(Path.Combine(directory, "season-2.jpg"));
                TouchDirectory(directory);

                var fourth = OverrideImageMatcher.GetFiles(directory);
                runner.Check("deleted file disappears from the listing", fourth.Count == 1);

                var missingBefore = OverrideImageMatcher.EnumerationCount;
                var missing = OverrideImageMatcher.GetFiles(Path.Combine(directory, "missing"));
                runner.Check("missing directory returns no files", missing.Count == 0);
                runner.EqualLong("missing directory is not cached",
                    missingBefore, OverrideImageMatcher.EnumerationCount);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
        }

        private static void TouchDirectory(string directory)
        {
            // Make the write-time change explicit; some filesystems have coarse timestamp
            // resolution when several entries are created within the same tick.
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow);
        }

        private static void CheckSeason(TestRunner runner, string fileName, int season, bool expected)
        {
            var actual = OverrideImageMatcher.IsSeasonEpisodeImage(fileName, season, null);
            runner.Check("season match '" + fileName + "' S" + season, actual == expected,
                "expected " + expected + ", actual " + actual);
        }

        private static void CheckEpisode(TestRunner runner, string fileName, int season, int episode,
            bool expected)
        {
            var actual = OverrideImageMatcher.IsSeasonEpisodeImage(fileName, season, episode);
            runner.Check("episode match '" + fileName + "' S" + season + "E" + episode,
                actual == expected, "expected " + expected + ", actual " + actual);
        }

        private static void CheckType(TestRunner runner, string fileName, string expectedType,
            bool expectedMatch)
        {
            var actualMatch = OverrideImageMatcher.TryGetImageTypeName(fileName, out var actualType);
            runner.Check("image type '" + fileName + "'", actualMatch == expectedMatch &&
                                                        string.Equals(expectedType, actualType,
                                                            StringComparison.Ordinal),
                "expected " + (expectedMatch ? expectedType : "<no match>") + ", actual " +
                (actualMatch ? actualType : "<no match>"));
        }
    }
}
