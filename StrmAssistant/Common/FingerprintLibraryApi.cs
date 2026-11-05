using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Exports and imports the audio fingerprints Emby stores for intro detection.
    /// Fingerprints are <c>title_{minutes}_{ticks}.fp</c> files inside each episode's internal
    /// metadata folder (raw little-endian uint32 chromaprint data). They are packed into a single
    /// JSON document so a library can be moved between servers without re-running detection.
    /// </summary>
    public class FingerprintLibraryApi
    {
        private const string TitleFingerprintPattern = "title_*.fp";

        private readonly ILibraryManager _libraryManager;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;

        public FingerprintLibraryApi(ILibraryManager libraryManager, IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _jsonSerializer = jsonSerializer;
            _logger = Plugin.Instance.Logger;
        }

        public string GetLibraryFilePath()
        {
            var configured = Plugin.Instance.GetPluginOptions().IntroSkipOptions.FingerprintLibraryFilePath;

            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

            var dataFolder = Plugin.Instance.DataFolderPath;

            if (string.IsNullOrEmpty(dataFolder))
            {
                dataFolder = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath,
                    "StrmAssistant");
            }

            return Path.Combine(dataFolder, "intro-fingerprints.json");
        }

        public int Export(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var filePath = GetLibraryFilePath();
            var episodes = FetchEpisodes();

            _logger?.Info("IntroFingerprintExport - Number of episodes: " + episodes.Count);

            var entries = new List<FingerprintEntry>();
            double total = episodes.Count;
            var index = 0;

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var file in GetFingerprintFiles(episode))
                {
                    try
                    {
                        entries.Add(new FingerprintEntry
                        {
                            Path = episode.Path,
                            SeriesName = episode.SeriesName,
                            SeasonNumber = episode.ParentIndexNumber,
                            EpisodeNumber = episode.IndexNumber,
                            FileName = System.IO.Path.GetFileName(file),
                            Data = Convert.ToBase64String(File.ReadAllBytes(file))
                        });
                    }
                    catch (Exception e)
                    {
                        _logger?.Debug("IntroFingerprintExport - Failed to read " + file + ": " + e.Message);
                    }
                }

                index++;
                progress?.Report(index / Math.Max(1, total));
            }

            var directory = System.IO.Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            _jsonSerializer.SerializeToFile(new FingerprintLibrary
            {
                Version = 1,
                ExportedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Fingerprints = entries
            }, filePath);

            _logger?.Info("IntroFingerprintExport - Exported " + entries.Count + " fingerprints to " + filePath);

            return entries.Count;
        }

        public Tuple<int, int> Import(CancellationToken cancellationToken)
        {
            var filePath = GetLibraryFilePath();

            if (!File.Exists(filePath))
            {
                _logger?.Info("IntroFingerprintImport - File not found: " + filePath);
                return new Tuple<int, int>(0, 0);
            }

            var library = _jsonSerializer.DeserializeFromFile<FingerprintLibrary>(filePath);

            if (library?.Fingerprints is null || library.Fingerprints.Count == 0)
            {
                _logger?.Info("IntroFingerprintImport - No fingerprints in file: " + filePath);
                return new Tuple<int, int>(0, 0);
            }

            _logger?.Info("IntroFingerprintImport - Number of fingerprints in file: " + library.Fingerprints.Count);

            var episodes = FetchEpisodes();
            var byPath = new Dictionary<string, Episode>(StringComparer.OrdinalIgnoreCase);
            var byFileName = new Dictionary<string, List<Episode>>(StringComparer.OrdinalIgnoreCase);
            var byIdentity = new Dictionary<string, List<Episode>>(StringComparer.OrdinalIgnoreCase);

            foreach (var episode in episodes)
            {
                if (!string.IsNullOrEmpty(episode.Path))
                {
                    byPath[episode.Path] = episode;

                    var fileName = System.IO.Path.GetFileName(episode.Path);

                    if (!byFileName.TryGetValue(fileName, out var fileNameMatches))
                    {
                        fileNameMatches = new List<Episode>();
                        byFileName[fileName] = fileNameMatches;
                    }

                    fileNameMatches.Add(episode);
                }

                var identity = BuildIdentity(episode);

                if (identity is null) continue;

                if (!byIdentity.TryGetValue(identity, out var identityMatches))
                {
                    identityMatches = new List<Episode>();
                    byIdentity[identity] = identityMatches;
                }

                identityMatches.Add(episode);
            }

            var imported = 0;
            var skipped = 0;
            var handledEpisodes = new HashSet<long>();

            foreach (var entry in library.Fingerprints)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var target = FindTarget(entry, byPath, byFileName, byIdentity);

                if (target is null)
                {
                    skipped++;
                    _logger?.Debug("IntroFingerprintImport - No matching episode: " + entry.Path);
                    continue;
                }

                if (!handledEpisodes.Add(target.InternalId))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var directory = target.GetInternalMetadataPath();

                    if (string.IsNullOrEmpty(directory)) throw new IOException("No internal metadata folder");

                    Directory.CreateDirectory(directory);

                    foreach (var existing in Directory.GetFiles(directory, TitleFingerprintPattern))
                    {
                        File.Delete(existing);
                    }

                    var minutes = GetFingerprintMinutes(target, entry);
                    var name = "title_" + minutes + "_" +
                               target.DateCreated.Ticks.ToString(CultureInfo.InvariantCulture) + ".fp";

                    File.WriteAllBytes(System.IO.Path.Combine(directory, name), Convert.FromBase64String(entry.Data));

                    imported++;
                    _logger?.Debug("IntroFingerprintImport - Imported: " + target.Path);
                }
                catch (Exception e)
                {
                    skipped++;
                    _logger?.Info("IntroFingerprintImport - Failed: " + target.Path + " - " + e.Message);
                }
            }

            _logger?.Info("IntroFingerprintImport - Imported " + imported + ", skipped " + skipped);

            return new Tuple<int, int>(imported, skipped);
        }

        private List<Episode> FetchEpisodes()
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    HasPath = true,
                    IncludeItemTypes = new[] { nameof(Episode) }
                })
                .OfType<Episode>()
                .ToList();
        }

        private static List<string> GetFingerprintFiles(BaseItem item)
        {
            try
            {
                var directory = item.GetInternalMetadataPath();

                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    return new List<string>();
                }

                return Directory.GetFiles(directory, TitleFingerprintPattern).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static Episode FindTarget(FingerprintEntry entry, Dictionary<string, Episode> byPath,
            Dictionary<string, List<Episode>> byFileName, Dictionary<string, List<Episode>> byIdentity)
        {
            if (!string.IsNullOrEmpty(entry.Path) && byPath.TryGetValue(entry.Path, out var exact))
            {
                return exact;
            }

            if (!string.IsNullOrEmpty(entry.Path))
            {
                var fileName = System.IO.Path.GetFileName(entry.Path);

                if (byFileName.TryGetValue(fileName, out var fileNameMatches) && fileNameMatches.Count == 1)
                {
                    return fileNameMatches[0];
                }
            }

            if (string.IsNullOrEmpty(entry.SeriesName) || entry.SeasonNumber is null || entry.EpisodeNumber is null)
            {
                return null;
            }

            var identity = BuildIdentity(entry.SeriesName, entry.SeasonNumber.Value, entry.EpisodeNumber.Value);

            return byIdentity.TryGetValue(identity, out var matches) && matches.Count == 1 ? matches[0] : null;
        }

        private static string BuildIdentity(Episode episode)
        {
            if (string.IsNullOrEmpty(episode.SeriesName) || episode.ParentIndexNumber is null ||
                episode.IndexNumber is null)
            {
                return null;
            }

            return BuildIdentity(episode.SeriesName, episode.ParentIndexNumber.Value, episode.IndexNumber.Value);
        }

        private static string BuildIdentity(string seriesName, int seasonNumber, int episodeNumber)
        {
            return seriesName.Trim() + "|" + seasonNumber + "|" + episodeNumber;
        }

        private int GetFingerprintMinutes(BaseItem item, FingerprintEntry entry)
        {
            try
            {
                var libraryOptions = _libraryManager.GetLibraryOptions(item);

                if (libraryOptions?.IntroDetectionFingerprintLength > 0)
                {
                    return libraryOptions.IntroDetectionFingerprintLength;
                }
            }
            catch
            {
                // Fall back to the length encoded in the exported file name.
            }

            var segments = System.IO.Path.GetFileNameWithoutExtension(entry.FileName ?? string.Empty).Split('_');

            if (segments.Length >= 3 && int.TryParse(segments[1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
            {
                return minutes;
            }

            return 10;
        }
    }

    public class FingerprintLibrary
    {
        public int Version { get; set; }
        public string ExportedAtUtc { get; set; }
        public List<FingerprintEntry> Fingerprints { get; set; } = new List<FingerprintEntry>();
    }

    public class FingerprintEntry
    {
        public string Path { get; set; }
        public string SeriesName { get; set; }
        public int? SeasonNumber { get; set; }
        public int? EpisodeNumber { get; set; }
        public string FileName { get; set; }
        public string Data { get; set; }
    }
}
