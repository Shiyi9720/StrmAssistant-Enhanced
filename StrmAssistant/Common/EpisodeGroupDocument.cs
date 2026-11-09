using System.Collections.Generic;

namespace StrmAssistant.Common
{
    /// <summary>
    /// The episode group document consumed by <see cref="LocalEpisodeGroup"/>. Local
    /// episodegroup.json files and normalized TMDB episode group responses share this shape:
    /// <c>groups[].order</c> is the library season number and <c>episodes[].order</c> is the
    /// zero-based position inside that group.
    /// </summary>
    internal sealed class EpisodeGroupDocument
    {
        public string id { get; set; }
        public string name { get; set; }
        public string description { get; set; }
        public List<EpisodeGroupGroup> groups { get; set; }
    }

    internal sealed class EpisodeGroupGroup
    {
        public string name { get; set; }
        public string overview { get; set; }
        public string air_date { get; set; }
        public int order { get; set; }
        public List<EpisodeGroupEpisode> episodes { get; set; }
    }

    internal sealed class EpisodeGroupEpisode
    {
        public int episode_number { get; set; }
        public int season_number { get; set; }
        public int order { get; set; }
    }
}
