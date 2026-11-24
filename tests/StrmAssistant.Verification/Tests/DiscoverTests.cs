using StrmAssistant.Common;
using System.Collections.Generic;
using System.Text.Json;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// 探索发现 channel pure logic: collection setting parsing, stable item ids, library matching,
    /// tagline/overview projection, trailer runtime and MoviePilot payloads.
    /// </summary>
    internal static class DiscoverTests
    {
        public static void Run(TestRunner runner)
        {
            CollectionSpecs(runner);
            ChannelIds(runner);
            LibraryMatching(runner);
            Projection(runner);
            TrailerRuntime(runner);
            SubscribePayloads(runner);
            ResponseParsing(runner);
            ImageProxy(runner);
        }

        private static void CollectionSpecs(TestRunner runner)
        {
            runner.Section("D1. Collection setting parses 标识|名称|数量");

            var specs = DiscoverCollectionSpecParser.Parse(
                "# comment\r\nECMNABFUI|戛纳获奖|20\n\nECMNABFUI|重复|5\nABC\nXYZ|只有名称\nBAD|X|999");

            runner.Check("4 unique specs", specs.Count == 4);
            runner.Check("name parsed", specs[0].Id == "ECMNABFUI" && specs[0].DisplayName == "戛纳获奖");
            runner.Check("count parsed", specs[0].Count == 20);
            runner.Check("duplicate skipped", specs[1].Id == "ABC" && specs[1].Count == 20);
            runner.Check("name only keeps default count", specs[2].DisplayName == "只有名称" && specs[2].Count == 20);
            runner.Check("count clamps to 100", specs[3].Count == 100);
            runner.Check("empty setting", DiscoverCollectionSpecParser.Parse("  ").Count == 0);
        }

        private static void ChannelIds(TestRunner runner)
        {
            runner.Section("D2. Channel item ids round-trip");

            var movie = DiscoverChannelIds.Entry("1234567", "movie");
            var tv = DiscoverChannelIds.Entry("7654321", "tv");

            runner.Check("entry prefixes differ",
                movie.StartsWith(DiscoverChannelIds.EntryPrefix) && tv.StartsWith(DiscoverChannelIds.EntryPrefix));
            runner.Check("movie parse",
                DiscoverChannelIds.TryParseEntry(movie, out var movieKind, out var movieId) &&
                movieKind == "movie" && movieId == "1234567");
            runner.Check("tv parse",
                DiscoverChannelIds.TryParseEntry(tv, out var tvKind, out var tvId) &&
                tvKind == "tv" && tvId == "7654321");
            runner.Check("collection parse",
                DiscoverChannelIds.TryParseCollection(DiscoverChannelIds.Collection("ECMNABFUI"),
                    out var collectionId) && collectionId == "ECMNABFUI");
            runner.Check("foreign id rejected", !DiscoverChannelIds.TryParseEntry("Channel 探索发现", out _, out _));
            runner.Check("malformed entry rejected",
                !DiscoverChannelIds.TryParseEntry(DiscoverChannelIds.EntryPrefix + "movie", out _, out _));
        }

        private static void LibraryMatching(TestRunner runner)
        {
            runner.Section("D3. Douban entry matches the local library exactly");

            var index = new DiscoverLibraryIndex(new List<DiscoverLibraryEntry>
            {
                new DiscoverLibraryEntry
                {
                    ItemId = 10, Name = "沙丘", OriginalTitle = "Dune", Year = 2021,
                    DoubanId = "3000001", Kind = "movie"
                },
                new DiscoverLibraryEntry
                {
                    ItemId = 20, Name = "沙丘2", OriginalTitle = "Dune: Part Two", Year = 2024,
                    DoubanId = "3000002", Kind = "movie"
                },
                new DiscoverLibraryEntry
                {
                    ItemId = 30, Name = "漫长的季节", Year = 2023, DoubanId = null, Kind = "tv"
                }
            });

            var byId = index.Match("3000001", "完全不同的标题", null, "movie");
            runner.Check("douban id authoritative", byId.InLibrary && byId.ItemId == 10);

            var byTitle = index.Match(null, "漫长的季节", 2023, "tv");
            runner.Check("exact title + year", byTitle.InLibrary && byTitle.ItemId == 30);

            var sameTitle = index.Match(null, "沙丘", 2021, "movie");
            runner.Check("same title picks the right year", sameTitle.InLibrary && sameTitle.ItemId == 10);

            var yearMismatch = index.Match(null, "沙丘", 2024, "movie");
            runner.Check("a two-year gap is not the same title", !yearMismatch.InLibrary);

            var kindMismatch = index.Match(null, "沙丘", null, "tv");
            runner.Check("kind mismatch is penalised", !kindMismatch.InLibrary);

            var missing = index.Match(null, "不存在的电影", 2020, "movie");
            runner.Check("missing stays missing", !missing.InLibrary &&
                                                  missing.StatusText == "缺失");
            runner.Check("normalisation ignores spaces and punctuation",
                index.Match(null, "漫长 的季-节", 2023, "tv").InLibrary);
        }

        private static void Projection(TestRunner runner)
        {
            runner.Section("D4. Tagline distinguishes 已在库/缺失 and overview keeps the subtitle");

            var inLibrary = new DiscoverMatch { InLibrary = true };
            runner.Check("tagline in library", DiscoverEntryProjection.BuildTagline(inLibrary) == "已在库");
            runner.Check("tagline missing", DiscoverEntryProjection.BuildTagline(DiscoverMatch.Missing) == "缺失");

            var overview = DiscoverEntryProjection.BuildOverview(null, "2026 / 罗马尼亚 / 剧情", inLibrary);
            runner.Check("overview carries card subtitle", overview.Contains("2026 / 罗马尼亚 / 剧情"));
            runner.Check("overview carries status", overview.Contains("状态：已在库"));
            runner.Check("year from subtitle", DiscoverText.YearFromSubtitle("2026 / 罗马尼亚") == 2026);
            runner.Check("bad year rejected", DiscoverText.YearFromSubtitle("abcd") == null);
        }

        private static void TrailerRuntime(TestRunner runner)
        {
            runner.Section("D5. Douban trailer runtime converts to ticks");

            runner.Check("mm:ss", DiscoverText.ParseRuntimeTicks("02:30") == 150L * System.TimeSpan.TicksPerSecond);
            runner.Check("h:mm:ss", DiscoverText.ParseRuntimeTicks("1:02:03") == 3723L * System.TimeSpan.TicksPerSecond);
            runner.Check("zero is unknown", DiscoverText.ParseRuntimeTicks("00:00") == null);
            runner.Check("garbage is unknown", DiscoverText.ParseRuntimeTicks("abc") == null);
        }

        private static void SubscribePayloads(TestRunner runner)
        {
            runner.Section("D6. MoviePilot subscription payloads");

            var webhook = DiscoverSubscribe.BuildWebhookPayload("3000001", "沙丘", "movie", 2021,
                "https://img/poster.jpg", 8.5);
            using (var document = JsonDocument.Parse(webhook))
            {
                var root = document.RootElement;
                runner.Check("event is library.subscribe",
                    root.GetProperty("event").GetString() == "library.subscribe");
                runner.Check("douban id", root.GetProperty("douban_id").GetString() == "3000001");
                runner.Check("media type", root.GetProperty("media_type").GetString() == "movie");
                runner.Check("year", root.GetProperty("year").GetInt32() == 2021);
                runner.Check("rating", root.GetProperty("rating").GetDouble() == 8.5);
            }

            var api = DiscoverSubscribe.BuildApiPayload("3000002", "漫长的季节", "tv", 2023, null, 9.4);
            using (var document = JsonDocument.Parse(api))
            {
                var root = document.RootElement;
                runner.Check("api media source", root.GetProperty("media_source").GetString() == "douban");
                runner.Check("api tv type", root.GetProperty("type").GetString() == "电视剧");
                runner.Check("api media id", root.GetProperty("media_id").GetString() == "3000002");
                runner.Check("api year", root.GetProperty("year").GetString() == "2023");
            }

            runner.Check("webhook url appends token",
                DiscoverSubscribe.BuildWebhookUrl("http://mp:3000/hook", "abc") ==
                "http://mp:3000/hook?token=abc");
            runner.Check("webhook url keeps existing query",
                DiscoverSubscribe.BuildWebhookUrl("http://mp:3000/hook?x=1", "abc") ==
                "http://mp:3000/hook?x=1&token=abc");
            runner.Check("api url",
                DiscoverSubscribe.BuildApiUrl("http://mp:3000/", "abc") ==
                "http://mp:3000/api/v1/subscribe/?apikey=abc");
        }

        private static void ImageProxy(TestRunner runner)
        {
            runner.Section("D8. Poster relay accepts only Douban hosts");

            runner.Check("doubanio image accepted",
                DiscoverImageProxy.TryNormalize(
                    "https://img9.doubanio.com/view/photo/m_ratio_poster/public/p1.jpg", out var ok) &&
                ok.Host == "img9.doubanio.com");
            runner.Check("douban.com accepted",
                DiscoverImageProxy.TryNormalize("https://m.douban.com/a.jpg", out _));
            runner.Check("foreign host rejected",
                !DiscoverImageProxy.TryNormalize("https://example.com/a.jpg", out _));
            runner.Check("lookalike host rejected",
                !DiscoverImageProxy.TryNormalize("https://doubanio.com.evil.test/a.jpg", out _));
            runner.Check("file scheme rejected",
                !DiscoverImageProxy.TryNormalize("file:///etc/passwd", out _));
            runner.Check("empty rejected", !DiscoverImageProxy.TryNormalize(null, out _));
        }

        private static void ResponseParsing(TestRunner runner)
        {
            runner.Section("D7. MoviePilot response envelope");

            var ok = DiscoverSubscribe.ParseResponse(200, "{\"success\":true,\"message\":\"ok\"}");
            runner.Check("success parsed", ok.Success && ok.Message == "ok");

            var rejected = DiscoverSubscribe.ParseResponse(200, "{\"success\":false,\"message\":\"exists\"}");
            runner.Check("success false wins", !rejected.Success && rejected.Message == "exists");

            var httpError = DiscoverSubscribe.ParseResponse(500, "oops");
            runner.Check("http status is the verdict", !httpError.Success);

            var text = DiscoverSubscribe.ParseResponse(204, string.Empty);
            runner.Check("empty body with 2xx", text.Success);
        }
    }
}
