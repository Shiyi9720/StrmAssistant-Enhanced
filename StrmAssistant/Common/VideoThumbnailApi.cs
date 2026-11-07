using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using StrmAssistant.Properties;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class VideoThumbnailApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFfmpegManager _ffmpegManager;
        private readonly IFileSystem _fileSystem;
        private readonly IImageProcessor _imageProcessor;
        private readonly IItemRepository _itemRepository;

        private readonly object _thumbnailGenerator;
        private readonly MethodInfo _refreshThumbnailImages;

        private static readonly Version AppVer = Plugin.Instance.ApplicationHost.ApplicationVersion;
        private static readonly Version Ver4936 = new Version("4.9.0.36");

        public VideoThumbnailApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IImageExtractionManager imageExtractionManager, IItemRepository itemRepository,
            IMediaMountManager mediaMountManager, IServerApplicationPaths applicationPaths,
            ILibraryMonitor libraryMonitor, IFfmpegManager ffmpegManager, IImageProcessor imageProcessor,
            IServerApplicationHost serverApplicationHost)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _ffmpegManager = ffmpegManager;
            _fileSystem = fileSystem;
            _imageProcessor = imageProcessor;
            _itemRepository = itemRepository;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var thumbnailGenerator = embyProviders.GetType("Emby.Providers.MediaInfo.ThumbnailGenerator");
                var thumbnailGeneratorConstructor = thumbnailGenerator.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(IFileSystem), typeof(ILogger), typeof(IImageExtractionManager),
                        typeof(IItemRepository), typeof(IMediaMountManager), typeof(IServerApplicationPaths),
                        typeof(ILibraryMonitor), typeof(IFfmpegManager)
                    }, null);
                _thumbnailGenerator = thumbnailGeneratorConstructor?.Invoke(new object[]
                {
                    fileSystem, _logger,
                    ImageExtractionTimeout.Wrap(imageExtractionManager, ffmpegManager, serverApplicationHost),
                    itemRepository, mediaMountManager, applicationPaths, libraryMonitor,
                    FfmpegApi.WrapFfmpegManager(ffmpegManager)
                });
                _refreshThumbnailImages = thumbnailGenerator.GetMethod("RefreshThumbnailImages",
                    BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger?.Debug(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
            }

            if (_thumbnailGenerator is null || _refreshThumbnailImages is null)
            {
                _logger?.Warn($"{nameof(VideoThumbnailApi)} Init Failed");
            }
        }

        public async Task<bool> RefreshThumbnailImages(Video item, LibraryOptions libraryOptions,
            IDirectoryService directoryService, List<ChapterInfo> chapters, bool extractImages, bool saveChapters,
            CancellationToken cancellationToken, DistributionStats distributionStats = null)
        {
            if (extractImages && VideoThumbnailBlacklist.FromOptions().IsExcluded(item))
            {
                _logger.Info("VideoThumbnailBlacklist - Skipped extraction: " + item.Name + " - " + item.Path);
                return false;
            }

            var mediaSource = AppVer >= Ver4936
                ? item.GetMediaSources(false, false, libraryOptions).FirstOrDefault()
                : null;

            // Distributed extraction only replaces the chapter image path. Libraries that use
            // BIF thumbnail sets, or items that already carry a local thumbnail set, keep using
            // Emby's generator. Any distributed failure silently falls through to it as well.
            if (extractImages && saveChapters && libraryOptions != null &&
                libraryOptions.EnableChapterImageExtraction &&
                libraryOptions.ThumbnailImagesIntervalSeconds == -1 &&
                Video.GetLocalThumbnailSetInfos(item.Path, item.Id, false, directoryService).Length == 0 &&
                Plugin.DistributedExtractApi != null &&
                DistributedExtractApi.IsThumbnailOptionEnabled)
            {
                var distributedMediaPath = mediaSource?.Path;

                if (string.IsNullOrEmpty(distributedMediaPath))
                {
                    distributedMediaPath = item.Path;

                    if (item.IsShortcut)
                    {
                        try
                        {
                            distributedMediaPath = await Plugin.LibraryApi
                                .GetStrmMountPath(distributedMediaPath).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            _logger?.Debug(
                                "VideoThumbnailDistributed - Failed to resolve shortcut: " + item.Path + " - " +
                                e.Message);
                            distributedMediaPath = null;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(distributedMediaPath) &&
                    await Plugin.DistributedExtractApi
                        .TryExtractChapterImagesAsync(item, distributedMediaPath, chapters, cancellationToken,
                            distributionStats)
                        .ConfigureAwait(false))
                {
                    await PostProcessChapterImages(item, mediaSource, chapters, cancellationToken)
                        .ConfigureAwait(false);

                    return true;
                }
            }

            var parameters = AppVer >= Ver4936
                ? new object[]
                {
                    item, mediaSource, null, libraryOptions, directoryService, chapters, extractImages,
                    saveChapters, cancellationToken
                }
                : new object[]
                {
                    item, null, libraryOptions, directoryService, chapters, extractImages, saveChapters,
                    cancellationToken
                };

            var result = await ((Task<bool>)_refreshThumbnailImages.Invoke(_thumbnailGenerator, parameters))
                .ConfigureAwait(false);

            if (result)
            {
                await PostProcessChapterImages(item, mediaSource, chapters, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        private async Task PostProcessChapterImages(Video item, MediaSourceInfo mediaSource,
            List<ChapterInfo> chapters, CancellationToken cancellationToken)
        {
            var options = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions;
            var quality = options.ScreenshotQuality;
            var maxWidth = options.ScreenshotMaxWidth;
            var maxHeight = options.ScreenshotMaxHeight;
            var cropUltraWide = options.CropUltraWideScreenshots;
            var cropPortrait = options.CropPortraitScreenshots;

            await ReExtractDolbyVisionProfile5Async(item, mediaSource, chapters, cancellationToken)
                .ConfigureAwait(false);

            if (quality <= 0 && maxWidth <= 0 && maxHeight <= 0 && !cropUltraWide && !cropPortrait) return;

            if (_imageProcessor is null)
            {
                _logger?.Warn("ScreenshotPostProcess - Image processor is unavailable");
                return;
            }

            var chapterImages = item.GetImages(ImageType.Chapter).ToList();

            if (!chapterImages.Any()) return;

            _logger?.Info($"ScreenshotPostProcess - Processing {chapterImages.Count} image(s): {item.Name} - {item.Path}");

            var index = 0;

            foreach (var image in chapterImages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var imagePath = image.Path;

                if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                {
                    index++;
                    continue;
                }

                if (cropUltraWide || cropPortrait)
                {
                    CropImage(imagePath, image, cropUltraWide, cropPortrait);
                }

                if (quality <= 0 && maxWidth <= 0 && maxHeight <= 0)
                {
                    index++;
                    continue;
                }

                var tempPath = imagePath + ".tmp";

                try
                {
                    using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var processingOptions = new ImageProcessingOptions
                        {
                            ItemId = item.InternalId,
                            Item = item,
                            Image = image,
                            ImageIndex = index,
                            Quality = quality,
                            MaxWidth = maxWidth,
                            MaxHeight = maxHeight,
                            SupportedOutputFormats = new[] { ImageFormat.Jpg }
                        };

                        await _imageProcessor.ProcessImage(processingOptions, output, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    File.Copy(tempPath, imagePath, true);
                    image.Reset(_fileSystem.GetFileSystemInfo(imagePath));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    _logger?.Error($"ScreenshotPostProcess - Failed: {imagePath}");
                    _logger?.Error(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                index++;
            }
        }

        /// <summary>
        /// Emby extracts Dolby Vision Profile 5 chapter images with its own ffmpeg, which ignores
        /// the DOVI reshaping and produces a green cast. When a custom ffmpeg with Vulkan and
        /// libplacebo is configured, replace those images with frames extracted by that build.
        /// </summary>
        private async Task ReExtractDolbyVisionProfile5Async(Video item, MediaSourceInfo mediaSource,
            List<ChapterInfo> chapters, CancellationToken cancellationToken)
        {
            if (chapters is null || chapters.Count == 0) return;

            var videoStream = mediaSource?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video) ??
                              item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);

            if (!DvP5Screenshot.IsDolbyVisionProfile5(videoStream)) return;

            var distributedEnabled = Plugin.DistributedExtractApi != null &&
                                     DistributedExtractApi.IsDvP5DistributionEnabled;

            // Without distributed extraction the custom ffmpeg is required on this node, exactly
            // as before. With it enabled the worker may own the capability, so the local self
            // check is deferred until a remote attempt has failed.
            if (!distributedEnabled && !DvP5Screenshot.IsAvailable(_ffmpegManager)) return;

            var inputPath = mediaSource?.Path;

            if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath)) inputPath = item.Path;

            if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
            {
                _logger?.Warn("DvP5Screenshot - Media file not found: " + item.Path);
                return;
            }

            var targets = new List<(ChapterInfo Chapter, string ImagePath, TimeSpan Offset)>();

            foreach (var chapter in chapters)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var imagePath = chapter.ImagePath;

                if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) continue;

                // Emby extracts the first chapter at 15 seconds to avoid black frames.
                var offset = chapter.StartPositionTicks <= 0
                    ? TimeSpan.FromSeconds(15)
                    : TimeSpan.FromTicks(chapter.StartPositionTicks);

                targets.Add((chapter, imagePath, offset));
            }

            if (targets.Count == 0) return;

            if (distributedEnabled)
            {
                var points = targets.Select(t => new DistributedDvP5Point
                {
                    StartPositionTicks = t.Chapter.StartPositionTicks,
                    OffsetSeconds = t.Offset.TotalSeconds
                }).ToList();

                var frames = await Plugin.DistributedExtractApi
                    .TryExtractDvP5ChapterImagesAsync(inputPath, points, cancellationToken)
                    .ConfigureAwait(false);

                var applied = frames is null ? 0 : ApplyDvP5Frames(targets, frames);

                if (applied == targets.Count)
                {
                    _logger?.Info($"DvP5Screenshot - Replaced {targets.Count} chapter image(s) via worker: " +
                                  $"{item.Name} - {item.Path}");
                    SaveChapterImageChanges(item, chapters);
                    return;
                }

                // A partial write still needs its chapter records kept in sync; the local pass
                // below (when available) will overwrite every target again.
                if (applied > 0) SaveChapterImageChanges(item, chapters);
            }

            if (!DvP5Screenshot.IsAvailable(_ffmpegManager)) return;

            var processed = 0;

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var tempPath = await DvP5Screenshot
                    .ExtractSingleImageAsync(_ffmpegManager, inputPath, target.Offset, cancellationToken)
                    .ConfigureAwait(false);

                if (tempPath is null) continue;

                try
                {
                    File.Copy(tempPath, target.ImagePath, true);
                    target.Chapter.ImageDateModified = _fileSystem.GetLastWriteTimeUtc(target.ImagePath);
                    processed++;
                }
                catch (Exception e)
                {
                    _logger?.Error("DvP5Screenshot - Failed to replace chapter image: " + target.ImagePath);
                    _logger?.Error(e.Message);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }

            if (processed > 0)
            {
                _logger?.Info($"DvP5Screenshot - Replaced {processed} chapter image(s): {item.Name} - {item.Path}");
                SaveChapterImageChanges(item, chapters);
            }
        }

        private int ApplyDvP5Frames(
            List<(ChapterInfo Chapter, string ImagePath, TimeSpan Offset)> targets,
            Dictionary<long, byte[]> frames)
        {
            var applied = 0;

            foreach (var target in targets)
            {
                if (!frames.TryGetValue(target.Chapter.StartPositionTicks, out var bytes) ||
                    bytes is null || bytes.Length < 4)
                {
                    continue;
                }

                var tempPath = target.ImagePath + ".dvp5.tmp";

                try
                {
                    _fileSystem.WriteAllBytes(tempPath, bytes);
                    _fileSystem.MoveFile(tempPath, target.ImagePath, true);
                    target.Chapter.ImageDateModified = _fileSystem.GetLastWriteTimeUtc(target.ImagePath);
                    applied++;
                }
                catch (Exception e)
                {
                    _logger?.Error("DvP5Screenshot - Failed to write worker image: " + target.ImagePath);
                    _logger?.Error(e.Message);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }

            return applied;
        }

        private void SaveChapterImageChanges(Video item, List<ChapterInfo> chapters)
        {
            try
            {
                _itemRepository?.SaveChapters(item.InternalId, chapters);
            }
            catch (Exception e)
            {
                _logger?.Debug("DvP5Screenshot - Failed to save chapters: " + e.Message);
            }
        }

        /// <summary>
        /// Crops ultra wide screenshots to 16:9 and vertical screenshots to 2:3 so that the
        /// chapter images follow the poster frame used by the web UI.
        /// </summary>
        private void CropImage(string imagePath, ItemImageInfo image, bool cropUltraWide, bool cropPortrait)
        {
            var tempPath = imagePath + ".crop.jpg";

            try
            {
                var size = _imageProcessor.GetImageSize(imagePath);
                var crop = GetCropFilter(size, cropUltraWide, cropPortrait);

                if (crop is null) return;

                var encoderPath = FfmpegApi.GetEncoderPath(_ffmpegManager);

                if (string.IsNullOrEmpty(encoderPath)) return;

                var arguments = $"-hide_banner -loglevel error -nostdin -i \"{imagePath}\" -vf \"{crop}\" -q:v 2 -y \"{tempPath}\"";

                var success = FfmpegApi.RunAsync(_ffmpegManager, encoderPath, arguments, "ScreenshotCrop",
                        60000, CancellationToken.None)
                    .GetAwaiter().GetResult();

                if (!success || !File.Exists(tempPath))
                {
                    _logger?.Warn("ScreenshotCrop - Failed: " + imagePath);
                    return;
                }

                File.Copy(tempPath, imagePath, true);
                image.Reset(_fileSystem.GetFileSystemInfo(imagePath));

                _logger?.Info($"ScreenshotCrop - {crop}: {imagePath}");
            }
            catch (Exception e)
            {
                _logger?.Error("ScreenshotCrop - Failed: " + imagePath);
                _logger?.Error(e.Message);
                _logger?.Debug(e.StackTrace);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private static string GetCropFilter(ImageSize size, bool cropUltraWide, bool cropPortrait)
        {
            var width = (int)Math.Round(size.Width);
            var height = (int)Math.Round(size.Height);

            if (width <= 0 || height <= 0) return null;

            var aspectRatio = (double)width / height;

            if (cropUltraWide && aspectRatio > 16.0 / 9 + 0.01)
            {
                var newWidth = Math.Min(width, (int)Math.Round(height * 16.0 / 9)) & ~1;

                if (newWidth < width)
                {
                    return $"crop={newWidth}:{height}:{(width - newWidth) / 2}:0";
                }
            }

            if (cropPortrait && aspectRatio < 2.0 / 3 - 0.01)
            {
                var newHeight = Math.Min(height, (int)Math.Round(width * 3.0 / 2)) & ~1;

                if (newHeight < height)
                {
                    return $"crop={width}:{newHeight}:0:{(height - newHeight) / 2}";
                }
            }

            return null;
        }

        public List<Video> FetchExtractTaskItems()
        {
            var libraryIds = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions
                .LibraryScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            var librariesWithVideoThumbnail = _libraryManager.GetVirtualFolders()
                .Where(f => f.LibraryOptions.EnableChapterImageExtraction)
                .ToList();
            var librariesSelected = librariesWithVideoThumbnail.Where(f => libraryIds.Contains(f.Id)).ToList();

            _logger?.Info("VideoThumbnailExtract - LibraryScope: " + (!librariesWithVideoThumbnail.Any()
                ? "NONE"
                : string.Join(", ",
                    (libraryIds.Contains("-1")
                        ? new[] { Resources.Favorites }.Concat(librariesSelected.Select(l => l.Name))
                        : librariesSelected.Select(l => l.Name)).DefaultIfEmpty("ALL"))));

            var includeExtra = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.IncludeExtra;
            _logger?.Info("Include Extra: " + includeExtra);

            var librariesWithVideoThumbnailPaths = librariesWithVideoThumbnail.SelectMany(l => l.Locations)
                .Select(ls =>
                    ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                .ToArray();
            var librariesSelectedPaths = librariesSelected.SelectMany(l => l.Locations)
                .Select(ls =>
                    ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                .ToArray();

            var favoritesWithExtra = Array.Empty<BaseItem>();

            if (libraryIds.Contains("-1") && librariesWithVideoThumbnailPaths.Any())
            {
                var favorites = LibraryApi.AllUsers.Select(e => e.Key)
                    .SelectMany(user => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        User = user,
                        IsFavorite = true,
                        PathStartsWithAny = librariesWithVideoThumbnailPaths
                    })).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();

                var expanded = Plugin.LibraryApi.ExpandFavorites(favorites, false, false, false);

                favoritesWithExtra = expanded
                    .Concat(includeExtra
                        ? expanded.SelectMany(f => f.GetExtras(LibraryApi.IncludeExtraTypes))
                        : Enumerable.Empty<BaseItem>())
                    .Where(i => Plugin.LibraryApi.HasMediaInfo(i) && !i.HasImage(ImageType.Chapter))
                    .ToArray();
            }

            var items = Array.Empty<BaseItem>();
            var extras = Array.Empty<BaseItem>();

            if (!libraryIds.Any() && librariesWithVideoThumbnailPaths.Any() ||
                libraryIds.Any(id => id != "-1") && librariesSelectedPaths.Any())
            {
                var videoThumbnailQuery = new InternalItemsQuery
                {
                    MediaTypes = new[] { MediaType.Video },
                    HasAudioStream = true,
                    HasChapterImages = false,
                    PathStartsWithAny = !libraryIds.Any() ? librariesWithVideoThumbnailPaths : librariesSelectedPaths
                };

                items = _libraryManager.GetItemList(videoThumbnailQuery);

                if (includeExtra)
                {
                    videoThumbnailQuery.ExtraTypes = LibraryApi.IncludeExtraTypes;
                    extras = _libraryManager.GetItemList(videoThumbnailQuery);
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            var combined = favoritesWithExtra.Concat(items).Concat(extras).GroupBy(i => i.InternalId)
                .Select(g => g.First()).Where(i => isModSupported || !i.IsShortcut).OfType<Video>().ToList();

            var blacklist = VideoThumbnailBlacklist.FromOptions();
            var filtered = combined.Where(i => !blacklist.IsExcluded(i)).ToList();

            if (filtered.Count != combined.Count)
            {
                _logger.Info($"VideoThumbnailBlacklist - Number of items excluded: {combined.Count - filtered.Count}");
            }

            return filtered;
        }
    }
}
