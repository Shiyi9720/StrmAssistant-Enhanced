using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System;

namespace StrmAssistant.Web.Service
{
    [Authenticated]
    public class LibraryService : BaseApiService
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;

        public LibraryService(ILibraryManager libraryManager, IItemRepository itemRepository, IFileSystem fileSystem)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _fileSystem = fileSystem;
        }

        public void Any(DeleteVersion request)
        {
            var item = _libraryManager.GetItemById(request.Id);

            if (!(item is Video video) || !(video is Movie || video is Episode) || !video.IsFileProtocol ||
                video.GetAlternateVersionIds().Count == 0)
            {
                return;
            }
            
            var user = GetUserForRequest(null);
            var collectionFolders = _libraryManager.GetCollectionFolders(item);

            if (user is null)
            {
                if (!item.CanDelete())
                {
                    return;
                }
            }
            else if (!item.CanDelete(user, collectionFolders))
            {
                return;
            }

            var deletePaths = LibraryApi.GetDeletePaths(item);

            foreach (var path in deletePaths)
            {
                try
                {
                    if (!path.IsDirectory)
                    {
                        _logger.Info("DeleteVersion - Attempting to delete file: " + path.FullName);
                        _fileSystem.DeleteFile(path.FullName, true);
                    }
                }
                catch (Exception e)
                {
                    _logger.Error("DeleteVersion - Delete file failed: " + path.FullName);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            _itemRepository.DeleteItems(new[] { item });

            try
            {
                _fileSystem.DeleteDirectory(item.GetInternalMetadataPath(), true, true);
            }
            catch
            {
                // ignored
            }
        }
    }
}
