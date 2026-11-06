using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// Handles Emby's favorite routes when cross-library favorite separation is enabled. The
    /// favorite is recorded per concrete item and only the aggregated value ("some item in the
    /// same movie group is favorite") is written to Emby's shared user data, so notifications and
    /// server side favorite queries keep working. The returned DTO always carries the per-item
    /// value, which is what the web client displays.
    /// </summary>
    public class VersionFavoriteService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserDataManager _userDataManager;

        public VersionFavoriteService(ILibraryManager libraryManager, IUserDataManager userDataManager)
        {
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
        }

        public object Post(MarkVersionFavoriteItem request)
        {
            return MarkFavorite(request.UserId, request.Id, true);
        }

        public object Any(UnmarkVersionFavoriteItem request)
        {
            return MarkFavorite(request.UserId, request.Id, false);
        }

        public object Get(GetVersionFavorites request)
        {
            var response = new VersionFavoriteResponse();
            var user = GetUserForRequest(null);

            if (user is null) return response;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var id in (request.Ids ?? string.Empty)
                         .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!seen.Add(id)) continue;

                var item = _libraryManager.GetItemById(id);
                if (item is null) continue;

                var favorite = Plugin.VersionFavoriteApi?.GetFavorite(user.InternalId, item, GetLibraryId(item));
                if (!favorite.HasValue) continue;

                response.Items.Add(new VersionFavoriteInfo
                {
                    ItemId = item.Id.ToString(),
                    IsFavorite = favorite.Value
                });
            }

            return response;
        }

        private object MarkFavorite(string userId, string itemId, bool isFavorite)
        {
            var user = GetUserForRequest(userId);
            var item = string.IsNullOrEmpty(itemId) ? null : _libraryManager.GetItemById(itemId);

            if (user is null || item is null) return new UserItemDataDto();

            var userData = _userDataManager.GetUserData(user, item);
            var separate = VersionFavoriteApi.IsEnabled && item is Movie &&
                           (!string.IsNullOrEmpty(VersionPlaybackApi.GetItemKey(item)) ||
                            !string.IsNullOrEmpty(VersionPlaybackApi.GetNameKey(item)));

            if (!separate)
            {
                // Behavior identical to Emby's own MarkFavorite when the feature is off or the
                // item is not a movie.
                userData.IsFavorite = isFavorite;
                userData.RatingLastModified = DateTimeOffset.UtcNow;
                _userDataManager.SaveUserData(user, item, userData, UserDataSaveReason.UpdateUserRating,
                    Request.CancellationToken);

                return _userDataManager.GetUserDataDto(item, user);
            }

            bool sharedFavorite;

            try
            {
                sharedFavorite = Plugin.VersionFavoriteApi.SetFavorite(user.InternalId, item, isFavorite,
                    GetLibraryId(item));
            }
            catch (Exception ex)
            {
                Logger.Debug("VersionFavorite - record failed: " + ex.Message);

                userData.IsFavorite = isFavorite;
                userData.RatingLastModified = DateTimeOffset.UtcNow;
                _userDataManager.SaveUserData(user, item, userData, UserDataSaveReason.UpdateUserRating,
                    Request.CancellationToken);

                return _userDataManager.GetUserDataDto(item, user);
            }

            // Emby's shared flag only tracks "any item of this movie is favorite". Writing it only
            // when it actually changes avoids duplicate favorite notifications for another
            // library's copy.
            if (userData.IsFavorite != sharedFavorite)
            {
                userData.IsFavorite = sharedFavorite;
                userData.RatingLastModified = DateTimeOffset.UtcNow;
                _userDataManager.SaveUserData(user, item, userData, UserDataSaveReason.UpdateUserRating,
                    Request.CancellationToken);
            }

            var dto = _userDataManager.GetUserDataDto(item, user);
            dto.IsFavorite = isFavorite;
            return dto;
        }

        private long GetLibraryId(BaseItem item)
        {
            try
            {
                return _libraryManager.GetCollectionFolders(item)?.FirstOrDefault()?.InternalId ?? 0;
            }
            catch (Exception ex)
            {
                Logger.Debug("VersionFavorite - library lookup failed: " + ex.Message);
                return 0;
            }
        }
    }
}
