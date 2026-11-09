using System;
using System.Collections.Generic;

namespace StrmAssistant.Provider
{
    /// <summary>
    /// Minimal TMDB response models used by the MovieDb series provider and by the title
    /// beautification task. Only the fields the plugin actually consumes are declared.
    /// </summary>
    public class SeriesResponseInfo
    {
        public List<SeasonResponseInfo> seasons { get; set; }
    }

    public class SeasonResponseInfo
    {
        public int id { get; set; }

        public int season_number { get; set; }

        public List<EpisodeResponseInfo> episodes { get; set; }
    }

    public class EpisodeResponseInfo
    {
        public DateTimeOffset? air_date { get; set; }

        public int episode_number { get; set; }

        public string name { get; set; }

        public string overview { get; set; }

        public int id { get; set; }

        public int season_number { get; set; }
    }
}
