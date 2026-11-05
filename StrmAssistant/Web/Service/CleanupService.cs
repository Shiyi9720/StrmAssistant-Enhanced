using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.Web.Service
{
    public abstract class CleanupServiceBase : BaseApiService
    {
        protected readonly ILibraryManager _libraryManager;
        protected readonly IItemRepository _itemRepository;
        protected readonly IFileSystem _fileSystem;
        protected readonly IDirectoryService _directoryService;

        protected CleanupServiceBase(ILibraryManager libraryManager, IItemRepository itemRepository,
            IFileSystem fileSystem)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _fileSystem = fileSystem;
            _directoryService = new DirectoryService(Plugin.Instance.Logger, fileSystem);
        }

        protected int DeleteFiles(IEnumerable<string> paths)
        {
            var deleted = 0;

            foreach (var path in paths.Where(i => !string.IsNullOrEmpty(i))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!_fileSystem.FileExists(path)) continue;

                    _fileSystem.DeleteFile(path);
                    deleted++;
                }
                catch (Exception ex)
                {
                    Plugin.Instance.Logger.Error("Cleanup - Failed to delete " + path + " - " + ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            }

            return deleted;
        }
    }

    public class ClearChapterImagesService : CleanupServiceBase
    {
        public ClearChapterImagesService(ILibraryManager libraryManager, IItemRepository itemRepository,
            IFileSystem fileSystem) : base(libraryManager, itemRepository, fileSystem)
        {
        }

        /// <summary>
        /// Deletes the generated chapter thumbnails and clears their paths in the chapter table.
        /// The chapter entries themselves (names and timestamps) are kept.
        /// </summary>
        public void Post(ClearChapterImages request)
        {
            var item = _libraryManager.GetItemById(request.Id);

            if (item is null) return;

            try
            {
                var chapters = _itemRepository.GetChapters(item);
                var paths = new List<string>();

                foreach (var chapter in chapters)
                {
                    if (string.IsNullOrEmpty(chapter.ImagePath)) continue;

                    paths.Add(chapter.ImagePath);
                    chapter.ImagePath = null;
                    chapter.ImageDateModified = default;
                }

                var deleted = DeleteFiles(paths);

                _itemRepository.SaveChapters(item.InternalId, chapters);

                Plugin.Instance.Logger.Info(
                    $"ClearChapterImages - {item.Name} - Deleted {deleted} image(s)");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Error("ClearChapterImages - " + item.Name + " - " + ex.Message);
                Plugin.Instance.Logger.Debug(ex.StackTrace);
            }
        }
    }

    public class ClearBifService : CleanupServiceBase
    {
        public ClearBifService(ILibraryManager libraryManager, IItemRepository itemRepository,
            IFileSystem fileSystem) : base(libraryManager, itemRepository, fileSystem)
        {
        }

        /// <summary>
        /// Removes the BIF thumbnail sets generated for a video, both from the video folder and
        /// from the item metadata folder.
        /// </summary>
        public void Post(ClearBif request)
        {
            var item = _libraryManager.GetItemById(request.Id);

            if (!(item is Video video) || string.IsNullOrEmpty(video.Path)) return;

            try
            {
                var thumbnailSets = Video
                    .GetLocalThumbnailSetInfos(video.Path, video.Id, true, _directoryService)
                    .Concat(Video.GetMetadataFolderThumbnailSetInfos(video.Path, video.Id, _directoryService))
                    .ToArray();

                var paths = thumbnailSets.Select(i => i.Path).ToList();
                var deleted = DeleteFiles(paths);

                foreach (var folder in paths.Select(Path.GetDirectoryName)
                             .Where(i => !string.IsNullOrEmpty(i))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (folder.IndexOf("thumbnailcache", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            CommonUtility.IsDirectoryEmpty(folder))
                        {
                            _fileSystem.DeleteDirectory(folder, false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Instance.Logger.Debug("ClearBif - Failed to delete folder " + folder + " - " +
                                                     ex.Message);
                    }
                }

                Plugin.Instance.Logger.Info($"ClearBif - {video.Name} - Deleted {deleted} file(s)");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Error("ClearBif - " + video.Name + " - " + ex.Message);
                Plugin.Instance.Logger.Debug(ex.StackTrace);
            }
        }
    }

    public class ClearMediaInfoService : CleanupServiceBase
    {
        public ClearMediaInfoService(ILibraryManager libraryManager, IItemRepository itemRepository,
            IFileSystem fileSystem) : base(libraryManager, itemRepository, fileSystem)
        {
        }

        public void Post(ClearMediaInfo request)
        {
            var item = _libraryManager.GetItemById(request.Id);

            if (item is null || Plugin.MediaInfoApi is null) return;

            try
            {
                Plugin.MediaInfoApi.DeleteMediaInfoJson(item, _directoryService, "Clear Media Info");
                Plugin.Instance.Logger.Info($"ClearMediaInfo - {item.Name}");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Error("ClearMediaInfo - " + item.Name + " - " + ex.Message);
                Plugin.Instance.Logger.Debug(ex.StackTrace);
            }
        }
    }
}
