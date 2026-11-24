using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Path arithmetic for the advanced local TMDB files. The layout matches the old plugin's
    /// existing one, extended with patches and per-item files:
    /// <code>
    /// &lt;root&gt;/override/tmdb-movies2/&lt;id&gt;/movie.json
    /// &lt;root&gt;/override/tmdb-movies2/&lt;id&gt;/patches/*.json
    /// &lt;root&gt;/override/tmdb-tv/&lt;id&gt;/external_ids.json
    /// &lt;root&gt;/override/tmdb-tv/&lt;id&gt;/episodeorder.json
    /// </code>
    /// A flat &lt;root&gt;/override/&lt;id&gt;/… layout is accepted as well. override/ wins over cache/.
    /// Missing or unreadable folders are simply not hits; this class never throws.
    /// </summary>
    internal static class LocalMetadataFiles
    {
        public const string PatchesFolderName = "patches";
        public const string ExternalIdsFileName = "external_ids.json";
        public const string EpisodeOrderFileName = "episodeorder.json";
        public const string LegacySpecialsFileName = "specials.json";

        public static string TypeFolder(LocalMetadataFileKind kind)
        {
            switch (kind)
            {
                case LocalMetadataFileKind.Movie: return "tmdb-movies2";
                case LocalMetadataFileKind.Series:
                case LocalMetadataFileKind.Season:
                case LocalMetadataFileKind.Episode: return "tmdb-tv";
                case LocalMetadataFileKind.Collection: return "tmdb-collections";
                default: return null;
            }
        }

        public static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;

            try
            {
                return Path.GetFullPath(root.Trim().Trim('"'));
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("LocalMetadataFiles - Invalid root: " + root +
                                               Environment.NewLine + e);
                return null;
            }
        }

        /// <summary>override typed, override flat, cache typed, cache flat.</summary>
        public static IReadOnlyList<string> LookupDirectories(string root, LocalMetadataFileKind kind,
            string tmdbId)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(tmdbId)) return result;

            var fullRoot = NormalizeRoot(root);
            if (fullRoot is null) return result;

            var typeFolder = TypeFolder(kind);
            if (typeFolder is null) return result;

            Add(result, Path.Combine(fullRoot, "override", typeFolder, tmdbId));
            Add(result, Path.Combine(fullRoot, "override", tmdbId));
            Add(result, Path.Combine(fullRoot, "cache", typeFolder, tmdbId));
            Add(result, Path.Combine(fullRoot, "cache", tmdbId));
            return result;
        }

        public static string FindFile(string root, LocalMetadataFileKind kind, string tmdbId,
            string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            foreach (var directory in LookupDirectories(root, kind, tmdbId))
            {
                try
                {
                    var candidate = Path.Combine(directory, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception)
                {
                    // a malformed path is simply not a hit
                }
            }

            return null;
        }

        /// <summary>
        /// Patch files for one id in file-name order. Only the first directory that actually has a
        /// patches/ folder is used: copying an id folder into override/ replaces the cached
        /// patches instead of merging the two sets.
        /// </summary>
        public static IReadOnlyList<string> FindPatchFiles(string root, LocalMetadataFileKind kind,
            string tmdbId)
        {
            foreach (var directory in LookupDirectories(root, kind, tmdbId))
            {
                try
                {
                    var patches = Path.Combine(directory, PatchesFolderName);
                    if (!Directory.Exists(patches)) continue;

                    var files = Directory.GetFiles(patches, "*.json")
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (files.Count > 0) return files;
                }
                catch (Exception)
                {
                    // unreadable patch folder -> treat as absent
                }
            }

            return Array.Empty<string>();
        }

        private static void Add(List<string> values, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (values.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
            values.Add(value);
        }
    }
}
