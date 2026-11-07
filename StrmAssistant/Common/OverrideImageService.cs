using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Applies images found under override/&lt;tmdbid&gt;/images to items after they are added
    /// or refreshed. The provider manager owns the final save path, so the normal Emby image
    /// pipeline remains in charge of caching and metadata updates.
    /// </summary>
    internal static class OverrideImageService
    {
        private const string StoreFileName = "StrmAssistant.OverrideImages.json";
        private const string OverrideFolderName = "override";
        private const string ImagesFolderName = "images";
        private const string MoviesFolderName = "tmdb-movies2";
        private const string TvFolderName = "tmdb-tv";
        private const string CollectionsFolderName = "tmdb-collections";

        private static readonly Regex TmdbIdRegex = new Regex(@"^\d+$", RegexOptions.Compiled);
        private static readonly Regex ImageTypeRegex = new Regex(@"^(?<type>[a-z]+)\.[a-z0-9]+$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SeasonEpisodeImageRegex = new Regex(
            @"^(?:season-(?<season>\d+)|episode-(?<episodeSeason>\d+)-(?<episode>\d+))\.(?<extension>jpg|jpeg|png|webp)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly ConcurrentDictionary<long, byte> InFlight =
            new ConcurrentDictionary<long, byte>();
        private static readonly object StateSync = new object();

        private static ILibraryManager _libraryManager;
        private static IProviderManager _providerManager;
        private static IJsonSerializer _jsonSerializer;
        private static string _storePath;
        private static Dictionary<string, OverrideImageStateEntry> _state;

        public static void Initialize(ILibraryManager libraryManager, IProviderManager providerManager,
            IJsonSerializer jsonSerializer)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _jsonSerializer = jsonSerializer;
            _storePath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath, StoreFileName);
            _state = LoadState();
        }

        public static async Task ApplyAsync(BaseItem item)
        {
            if (item is null || _libraryManager is null || _providerManager is null || _jsonSerializer is null)
            {
                return;
            }

            if (!InFlight.TryAdd(item.InternalId, 0)) return;

            try
            {
                var options = Plugin.Instance?.GetPluginOptions();

                if (options is null || !options.EnableLocalMetadataSource ||
                    !options.LocalMetadataAutoRefreshImages)
                {
                    return;
                }

                var files = GetOverrideImages(item, options.LocalMetadataRoot);

                if (files.Count == 0) return;

                var libraryOptions = _libraryManager.GetLibraryOptions(item);
                var directoryService = new DirectoryService(Plugin.Instance.Logger, Plugin.Instance.FileSystem);

                foreach (var file in files)
                {
                    var info = new FileInfo(file.Path);

                    if (!info.Exists) continue;

                    var stamp = info.LastWriteTimeUtc.Ticks + ":" + info.Length;
                    var key = GetStateKey(item, file.ImageType);

                    lock (StateSync)
                    {
                        if (_state.TryGetValue(key, out var existing) && existing.Stamp == stamp)
                        {
                            continue;
                        }
                    }

                    try
                    {
                        using (var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                                   FileShare.Read))
                        {
                            await _providerManager.SaveImage(item, libraryOptions, stream,
                                GetMimeType(file.Path).AsMemory(), file.ImageType, file.ImageIndex, null,
                                directoryService, true, CancellationToken.None).ConfigureAwait(false);
                        }

                        RecordApplied(key, stamp, item.DateLastRefreshed.UtcTicks);
                        Plugin.Instance?.Logger?.Debug("Override image applied - {0} ({1})",
                            item.Name, file.Path);
                    }
                    catch (Exception e)
                    {
                        Plugin.Instance?.Logger?.Debug("Override image apply failed - " + file.Path + ": " +
                                                       e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Override image scan failed - " + item.Name + ": " + e.Message);
            }
            finally
            {
                InFlight.TryRemove(item.InternalId, out _);
            }
        }

        private static List<OverrideImageFile> GetOverrideImages(BaseItem item, string root)
        {
            var result = new List<OverrideImageFile>();

            if (string.IsNullOrWhiteSpace(root)) return result;

            string fullRoot;

            try
            {
                fullRoot = Path.GetFullPath(root.Trim().Trim('"'));
            }
            catch
            {
                return result;
            }

            var tmdbId = GetTmdbId(item);

            if (string.IsNullOrWhiteSpace(tmdbId) || !TmdbIdRegex.IsMatch(tmdbId)) return result;

            if (item is Movie)
            {
                var imageDirectories = new List<string>();
                AddDirectories(imageDirectories, fullRoot, MoviesFolderName, tmdbId);

                return ScanDirectories(imageDirectories, item);
            }

            if (item is Series)
            {
                var imageDirectories = new List<string>();
                AddDirectories(imageDirectories, fullRoot, TvFolderName, tmdbId);

                return ScanDirectories(imageDirectories, item);
            }

            if (item is BoxSet)
            {
                var imageDirectories = new List<string>();
                AddDirectories(imageDirectories, fullRoot, CollectionsFolderName, tmdbId);

                return ScanDirectories(imageDirectories, item);
            }

            if (item is Season season)
            {
                if (!season.IndexNumber.HasValue) return result;

                var localSeason = season.IndexNumber.Value;
                var localImages = GetSeasonEpisodeImages(fullRoot, tmdbId, localSeason, null);

                if (localImages.Count > 0) return localImages;

                if (LocalEpisodeGroup.TryGetTmdbNumbers(tmdbId, localSeason, null,
                        out var tmdbSeason, out _) && tmdbSeason != localSeason)
                {
                    var fallbackImages = GetSeasonEpisodeImages(fullRoot, tmdbId, tmdbSeason, null);

                    if (fallbackImages.Count > 0)
                    {
                        Plugin.Instance?.Logger?.Debug(
                            "Override image matched TMDB default season naming - {0} (library S{1} -> TMDB S{2})",
                            fallbackImages[0].Path, localSeason, tmdbSeason);
                    }

                    return fallbackImages;
                }

                return result;
            }

            if (item is Episode episode)
            {
                if (!episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue) return result;

                var localSeason = episode.ParentIndexNumber.Value;
                var localEpisode = episode.IndexNumber.Value;
                var localImages = GetSeasonEpisodeImages(fullRoot, tmdbId, localSeason, localEpisode);

                if (localImages.Count > 0) return localImages;

                if (LocalEpisodeGroup.TryGetTmdbNumbers(tmdbId, localSeason, localEpisode,
                        out var tmdbSeason, out var tmdbEpisode) &&
                    (tmdbSeason != localSeason || tmdbEpisode != localEpisode))
                {
                    var fallbackImages = GetSeasonEpisodeImages(fullRoot, tmdbId, tmdbSeason, tmdbEpisode);

                    if (fallbackImages.Count > 0)
                    {
                        Plugin.Instance?.Logger?.Debug(
                            "Override image matched TMDB default episode naming - " + fallbackImages[0].Path +
                            " (library S" + localSeason + "E" + localEpisode + " -> TMDB S" + tmdbSeason +
                            "E" + tmdbEpisode + ")");
                    }

                    return fallbackImages;
                }

                return result;
            }

            return result;
        }

        private static List<OverrideImageFile> GetSeasonEpisodeImages(string root, string tmdbId,
            int season, int? episode)
        {
            var files = new List<OverrideImageFile>();

            if (episode.HasValue)
            {
                AddFlatImages(files, root, tmdbId, season, episode.Value);
            }
            else
            {
                AddFlatImages(files, root, tmdbId, season, null);
            }

            var directories = new List<string>();

            if (episode.HasValue)
            {
                AddDirectories(directories, root, TvFolderName, tmdbId,
                    "season-" + season + "-episode-" + episode.Value);
                AddDirectories(directories, root, TvFolderName, tmdbId,
                    "episode-" + season + "-" + episode.Value);
            }
            else
            {
                AddDirectories(directories, root, TvFolderName, tmdbId, "season-" + season);
            }

            files.AddRange(ScanDirectories(directories, null, true));

            return files
                .GroupBy(file => file.ImageType)
                .Select(group => group.First())
                .ToList();
        }

        private static void AddFlatImages(List<OverrideImageFile> files, string root, string tmdbId,
            int season, int? episode)
        {
            foreach (var directory in new[]
                     {
                         Path.Combine(root, OverrideFolderName, TvFolderName, tmdbId, ImagesFolderName),
                         Path.Combine(root, OverrideFolderName, tmdbId, ImagesFolderName)
                     }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(directory)) continue;

                foreach (var path in Directory.GetFiles(directory))
                {
                    var match = SeasonEpisodeImageRegex.Match(Path.GetFileName(path));

                    if (!match.Success) continue;

                    if (episode.HasValue)
                    {
                        if (!match.Groups["episode"].Success ||
                            !int.TryParse(match.Groups["episodeSeason"].Value, out var imageSeason) ||
                            !int.TryParse(match.Groups["episode"].Value, out var imageEpisode) ||
                            imageSeason != season || imageEpisode != episode.Value)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        if (match.Groups["episode"].Success ||
                            !int.TryParse(match.Groups["season"].Value, out var imageSeason) ||
                            imageSeason != season)
                        {
                            continue;
                        }
                    }

                    files.Add(new OverrideImageFile
                    {
                        Path = path,
                        ImageType = ImageType.Primary,
                        ImageIndex = null
                    });
                }
            }
        }

        private static List<OverrideImageFile> ScanDirectories(List<string> imageDirectories, BaseItem item,
            bool seasonEpisodeOnly = false)
        {
            var result = new List<OverrideImageFile>();

            var allowedTypes = seasonEpisodeOnly || item is Episode || item is Season
                ? new[] { "poster" }
                : new[] { "poster", "fanart", "backdrop", "clearlogo", "logo", "landscape", "thumb",
                    "banner", "discart", "disc", "clearart", "art" };

            foreach (var directory in imageDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(directory)) continue;

                foreach (var path in Directory.GetFiles(directory))
                {
                    var match = ImageTypeRegex.Match(Path.GetFileName(path));

                    if (!match.Success) continue;

                    var typeName = match.Groups["type"].Value.ToLowerInvariant();

                    if (!allowedTypes.Contains(typeName)) continue;

                    var imageType = MapImageType(typeName, out var imageIndex);

                    if (!imageType.HasValue) continue;

                    result.Add(new OverrideImageFile
                    {
                        Path = path,
                        ImageType = imageType.Value,
                        ImageIndex = imageIndex
                    });
                }
            }

            return result
                .GroupBy(file => file.ImageType)
                .Select(group => group.First())
                .ToList();
        }

        private static void AddDirectories(List<string> directories, string root, string typeFolder,
            string tmdbId, string itemFolder = null)
        {
            var relative = typeFolder + "/" + tmdbId;

            if (!string.IsNullOrEmpty(itemFolder)) relative += "/" + itemFolder;

            directories.Add(Path.Combine(root, OverrideFolderName,
                relative.Replace('/', Path.DirectorySeparatorChar), ImagesFolderName));
            directories.Add(Path.Combine(root, OverrideFolderName, tmdbId,
                (itemFolder ?? string.Empty).Replace('/', Path.DirectorySeparatorChar), ImagesFolderName));
        }

        private static string GetTmdbId(BaseItem item)
        {
            if (item is Episode episode)
            {
                return episode.Series != null && episode.Series.ProviderIds.TryGetValue("Tmdb", out var episodeTmdb)
                    ? episodeTmdb
                    : null;
            }

            if (item is Season season)
            {
                return season.Series != null && season.Series.ProviderIds.TryGetValue("Tmdb", out var seasonTmdb)
                    ? seasonTmdb
                    : null;
            }

            return item.ProviderIds != null && item.ProviderIds.TryGetValue("Tmdb", out var tmdb) ? tmdb : null;
        }

        private static ImageType? MapImageType(string typeName, out int? imageIndex)
        {
            imageIndex = null;

            switch (typeName)
            {
                case "poster":
                    return ImageType.Primary;
                case "fanart":
                case "backdrop":
                    imageIndex = 0;
                    return ImageType.Backdrop;
                case "clearlogo":
                case "logo":
                    return ImageType.Logo;
                case "landscape":
                case "thumb":
                    imageIndex = 0;
                    return ImageType.Thumb;
                case "banner":
                    return ImageType.Banner;
                case "discart":
                case "disc":
                    return ImageType.Disc;
                case "clearart":
                case "art":
                    imageIndex = 0;
                    return ImageType.Art;
                default:
                    return null;
            }
        }

        private static string GetMimeType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png":
                    return "image/png";
                case ".webp":
                    return "image/webp";
                default:
                    return "image/jpeg";
            }
        }

        private static string GetStateKey(BaseItem item, ImageType imageType)
        {
            return item.InternalId.ToString() + ":" + imageType;
        }

        private static void RecordApplied(string key, string stamp, long refreshedAtTicks)
        {
            lock (StateSync)
            {
                _state[key] = new OverrideImageStateEntry
                {
                    Stamp = stamp,
                    RefreshedAtTicks = refreshedAtTicks
                };

                try
                {
                    _jsonSerializer.SerializeToFile(new OverrideImageStateDocument { Images = _state },
                        _storePath);
                }
                catch (Exception e)
                {
                    Plugin.Instance?.Logger?.Debug("Override image state save failed: " + e.Message);
                }
            }
        }

        private static Dictionary<string, OverrideImageStateEntry> LoadState()
        {
            try
            {
                if (!File.Exists(_storePath)) return new Dictionary<string, OverrideImageStateEntry>();

                var document = _jsonSerializer.DeserializeFromFile<OverrideImageStateDocument>(_storePath);
                return document?.Images ?? new Dictionary<string, OverrideImageStateEntry>();
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Override image state load failed: " + e.Message);
                return new Dictionary<string, OverrideImageStateEntry>();
            }
        }

        private sealed class OverrideImageFile
        {
            public string Path { get; set; }
            public ImageType ImageType { get; set; }
            public int? ImageIndex { get; set; }
        }

        private sealed class OverrideImageStateDocument
        {
            public Dictionary<string, OverrideImageStateEntry> Images { get; set; } =
                new Dictionary<string, OverrideImageStateEntry>();
        }

        private sealed class OverrideImageStateEntry
        {
            public string Stamp { get; set; }
            public long RefreshedAtTicks { get; set; }
        }
    }
}
