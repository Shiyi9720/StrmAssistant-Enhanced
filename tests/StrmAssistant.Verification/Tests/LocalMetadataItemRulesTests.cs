using StrmAssistant.Common;
using System.Text;

namespace StrmAssistant.Verification
{
    /// <summary>
    /// external_ids.json and episodeorder.json parsing. The season-less legacy shape must stay a
    /// specials-only order, otherwise it would reorder the main run.
    /// </summary>
    internal static class LocalMetadataItemRulesTests
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void Run(TestRunner runner)
        {
            ExternalIds(runner);
            EpisodeOrder(runner);
            Resolve(runner);
        }

        private static void ExternalIds(TestRunner runner)
        {
            runner.Section("L6. external_ids aliases and value types");

            var ids = LocalMetadataItemRules.ReadExternalIds(Utf8NoBom.GetBytes(
                "{\"tmdb\":123,\"imdb\":\"tt1\",\"bgm\":\"x\",\"tvrage\":\"v\",\"empty\":\"\"}"));

            runner.Check("tmdb number", ids != null && ids["Tmdb"] == "123");
            runner.Check("imdb", ids != null && ids["Imdb"] == "tt1");
            runner.Check("bgm -> Bangumi", ids != null && ids["Bangumi"] == "x");
            runner.Check("tvrage", ids != null && ids["TvRage"] == "v");
            runner.Check("empty dropped", ids != null && !ids.ContainsKey("empty"));
            runner.Check("invalid json", LocalMetadataItemRules.ReadExternalIds(
                Utf8NoBom.GetBytes("{oops")) == null);
        }

        private static void EpisodeOrder(TestRunner runner)
        {
            runner.Section("L7. episodeorder.json and legacy specials.json shapes");

            var entries = LocalMetadataItemRules.ReadEpisodeOrder(Utf8NoBom.GetBytes(
                "{\"episodes\":[{\"season\":1,\"episode\":2,\"air_order\":7}," +
                "{\"episode_number\":5,\"order\":3}]}"));

            runner.Check("two entries", entries != null && entries.Count == 2);
            runner.Check("season entry", entries[0].HasSeason && entries[0].Season == 1 &&
                                         entries[0].Episode == 2 && entries[0].AirOrder == 7);
            runner.Check("legacy season-less entry", !entries[1].HasSeason && entries[1].Episode == 5 &&
                                                     entries[1].AirOrder == 3);

            var specials = LocalMetadataItemRules.ReadEpisodeOrder(Utf8NoBom.GetBytes(
                "{\"specials\":[{\"episode_number\":1,\"sort_index_number\":9}]}"));

            runner.Check("specials array", specials != null && specials.Count == 1 &&
                                           specials[0].AirOrder == 9);

            var bad = LocalMetadataItemRules.ReadEpisodeOrder(Utf8NoBom.GetBytes("{\"other\":[]}"));
            runner.Check("unsupported shape", bad == null);
        }

        private static void Resolve(TestRunner runner)
        {
            runner.Section("L8. Air order resolves by season and only season 0 uses season-less entries");

            var entries = LocalMetadataItemRules.ReadEpisodeOrder(Utf8NoBom.GetBytes(
                "{\"episodes\":[{\"season\":1,\"episode\":2,\"air_order\":7}," +
                "{\"episode_number\":5,\"order\":3}]}"));

            runner.Check("season 1 ep 2", LocalMetadataItemRules.TryResolveAirOrder(entries, 1, 2) == 7);
            runner.Check("season 2 ep 2 is not the season 1 entry",
                LocalMetadataItemRules.TryResolveAirOrder(entries, 2, 2) == null);
            runner.Check("season 0 ep 5 uses legacy", LocalMetadataItemRules.TryResolveAirOrder(entries, 0, 5) == 3);
            runner.Check("season 1 ep 5 ignores legacy",
                LocalMetadataItemRules.TryResolveAirOrder(entries, 1, 5) == null);
        }
    }
}
