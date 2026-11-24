using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// Regression coverage for provider-id prefix search (douban:/bgm:/tmdb:/imdb:/tvdb:).
    /// The index must carry both the bare id (an id copied without a prefix still works) and the
    /// prefixed form, and Bangumi must be reachable as both "bgm:" and "bangumi:".
    /// </summary>
    internal static class ProviderSearchKeysTests
    {
        public static void Run(TestRunner runner)
        {
            DoubanPrefix(runner);
            BangumiBothSpellings(runner);
            TmdbAndTvdb(runner);
            ImdbPrefix(runner);
            BareIdStillIndexed(runner);
            UnknownProviderIsBareOnly(runner);
            BlankValueYieldsNothing(runner);
        }

        private static void DoubanPrefix(TestRunner runner)
        {
            runner.Section("P1. douban:1291546");

            var keys = ProviderSearchKeys.KeysFor("doubanid", "1291546").ToList();

            runner.Check("bare id present", keys.Contains("1291546"));
            runner.Check("douban prefix present", keys.Contains("douban1291546"));
            runner.Check("no extra keys", keys.Count == 2);
        }

        private static void BangumiBothSpellings(TestRunner runner)
        {
            runner.Section("P2. bgm:123456 and bangumi:123456");

            var keys = ProviderSearchKeys.KeysFor("bgmid", "123456").ToList();

            runner.Check("bare id present", keys.Contains("123456"));
            runner.Check("bgm prefix present", keys.Contains("bgm123456"));
            runner.Check("bangumi prefix present", keys.Contains("bangumi123456"));
        }

        private static void TmdbAndTvdb(TestRunner runner)
        {
            runner.Section("P3. tmdb:550 / tvdb:81189");

            var tmdb = ProviderSearchKeys.KeysFor("tmdbid", "550").ToList();
            var tvdb = ProviderSearchKeys.KeysFor("tvdbid", "81189").ToList();

            runner.Check("tmdb bare", tmdb.Contains("550"));
            runner.Check("tmdb prefix", tmdb.Contains("tmdb550"));
            runner.Check("tvdb bare", tvdb.Contains("81189"));
            runner.Check("tvdb prefix", tvdb.Contains("tvdb81189"));
        }

        private static void ImdbPrefix(TestRunner runner)
        {
            runner.Section("P4. imdb:tt0111161");

            var keys = ProviderSearchKeys.KeysFor("imdbid", "tt0111161").ToList();

            runner.Check("imdb bare", keys.Contains("tt0111161"));
            runner.Check("imdb prefix", keys.Contains("imdbtt0111161"));
        }

        private static void BareIdStillIndexed(TestRunner runner)
        {
            runner.Section("P5. The bare id is always present so a copied id works");

            foreach (var provider in new[] { "doubanid", "bgmid", "imdbid", "tmdbid", "tvdbid" })
            {
                var keys = ProviderSearchKeys.KeysFor(provider, "42").ToList();
                runner.Check(provider + " bare id", keys.Contains("42"));
            }
        }

        private static void UnknownProviderIsBareOnly(TestRunner runner)
        {
            runner.Section("P6. An unrecognised provider yields only the bare id");

            var keys = ProviderSearchKeys.KeysFor("someotherid", "abc123").ToList();

            runner.Check("bare id only", keys.Count == 1 && keys[0] == "abc123");
        }

        private static void BlankValueYieldsNothing(TestRunner runner)
        {
            runner.Section("P7. Blank values index nothing");

            runner.Check("null value", !ProviderSearchKeys.KeysFor("doubanid", null).Any());
            runner.Check("empty value", !ProviderSearchKeys.KeysFor("doubanid", "").Any());
            runner.Check("whitespace value", !ProviderSearchKeys.KeysFor("doubanid", "   ").Any());
        }
    }
}
