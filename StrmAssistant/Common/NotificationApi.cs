using Emby.Notifications;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class NotificationApi
    {
        private const int SeriesNotificationDelayMs = 40000;
        private const string NotificationSettingsKey = "usernotifications";

        private readonly ILogger _logger;
        private readonly INotificationManager _notificationManager;
        private readonly IUserManager _userManager;
        private readonly ISessionManager _sessionManager;
        private readonly ILibraryManager _libraryManager;

        private readonly object _seriesNotificationLock = new object();
        private readonly Dictionary<string, PendingSeriesChange> _pendingSeriesChanges =
            new Dictionary<string, PendingSeriesChange>();
        private Timer _seriesNotificationTimer;

        private sealed class PendingSeriesChange
        {
            public long SeriesId { get; set; }
            public bool Added { get; set; }
            public string SeriesName { get; set; }
            public string Path { get; set; }
            public Dictionary<int, SortedSet<int>> Seasons { get; } = new Dictionary<int, SortedSet<int>>();
        }

        public NotificationApi(INotificationManager notificationManager, IUserManager userManager,
            ISessionManager sessionManager, ILibraryManager libraryManager)
        {
            _logger = Plugin.Instance.Logger;
            _notificationManager = notificationManager;
            _userManager = userManager;
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
        }

        public void FavoritesUpdateSendNotification(BaseItem item)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            var users = Plugin.LibraryApi.GetUsersByFavorites(item);
            foreach (var user in users)
            {
                var request = new NotificationRequest
                {
                    Title = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                    EventId = "favorites.update",
                    User = user,
                    Item = item,
                    Description =
                        string.Format(
                            Resources.Notification_CatchupUpdate_EventDescription.Replace("\\n",
                                Environment.NewLine), item.Path, user)
                };
                _notificationManager.SendNotification(request);
            }
        }

        /// <summary>
        /// Emby groups added/removed episodes by series but drops the episode details from the
        /// notification. Queue them for the same 40 second window and send an additional
        /// notification whose description starts with the season and episode information.
        /// </summary>
        public void SeriesItemChangedSendNotification(BaseItem item, bool added)
        {
            var options = Plugin.Instance?.GetPluginOptions().NotificationOptions;
            var experienceOptions = Plugin.Instance?.GetPluginOptions().ExperienceEnhanceOptions;

            if (options is null || experienceOptions?.EnhanceNotification != true) return;

            if (!(item is Episode episode)) return;

            var seriesId = episode.SeriesId;

            if (seriesId == 0) return;

            lock (_seriesNotificationLock)
            {
                var key = seriesId + (added ? "|added" : "|removed");

                if (!_pendingSeriesChanges.TryGetValue(key, out var pending))
                {
                    pending = new PendingSeriesChange { SeriesId = seriesId, Added = added };
                    _pendingSeriesChanges[key] = pending;
                }

                var seasonNumber = episode.ParentIndexNumber ?? 0;

                if (!pending.Seasons.TryGetValue(seasonNumber, out var episodes))
                {
                    episodes = new SortedSet<int>();
                    pending.Seasons[seasonNumber] = episodes;
                }

                if (episode.IndexNumber.HasValue) episodes.Add(episode.IndexNumber.Value);

                pending.SeriesName = episode.FindSeriesName() ?? pending.SeriesName;
                pending.Path = episode.Path ?? pending.Path;

                if (_seriesNotificationTimer is null)
                {
                    _seriesNotificationTimer =
                        new Timer(FlushSeriesNotifications, null, SeriesNotificationDelayMs, Timeout.Infinite);
                }
                else
                {
                    _seriesNotificationTimer.Change(SeriesNotificationDelayMs, Timeout.Infinite);
                }
            }
        }

        private void FlushSeriesNotifications(object state)
        {
            List<PendingSeriesChange> pendingChanges;

            lock (_seriesNotificationLock)
            {
                pendingChanges = _pendingSeriesChanges.Values.ToList();
                _pendingSeriesChanges.Clear();
            }

            foreach (var pending in pendingChanges)
            {
                try
                {
                    SendSeriesItemNotification(pending);
                }
                catch (Exception e)
                {
                    _logger?.Error("SeriesNotification - Failed: " + pending.SeriesName);
                    _logger?.Error(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
            }
        }

        private void SendSeriesItemNotification(PendingSeriesChange pending)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            var seasons = pending.Seasons.OrderBy(kv => kv.Key).Select(kv =>
            {
                if (kv.Value.Count > 0)
                {
                    return string.Format(Resources.Notification_SeasonEpisode_Format, kv.Key,
                        string.Join(",", kv.Value));
                }

                return kv.Key > 0 ? string.Format(Resources.Notification_Season_Format, kv.Key) : string.Empty;
            }).Where(s => !string.IsNullOrEmpty(s)).ToList();

            var seasonInfo = seasons.Count > 0 ? string.Join(" / ", seasons) : pending.SeriesName;

            var description = string.Format(
                (pending.Added
                    ? Resources.Notification_SeriesItemAdded_Description
                    : Resources.Notification_SeriesItemRemoved_Description).Replace("\\n", Environment.NewLine),
                seasonInfo, pending.SeriesName, pending.Path);

            var request = new NotificationRequest
            {
                Title = Resources.PluginOptions_EditorTitle_Strm_Assistant + " - " +
                        (pending.Added
                            ? Resources.Notification_SeriesItemAdded_EventName
                            : Resources.Notification_SeriesItemRemoved_EventName),
                EventId = pending.Added ? "library.new" : "library.deleted",
                Item = _libraryManager.GetItemById(pending.SeriesId),
                Description = description
            };

            _notificationManager.SendNotification(request);
        }

        /// <summary>
        /// Emby copies the typed user settings of the source user when a user is duplicated,
        /// including the configured notification targets. Clear them for the newly created user.
        /// </summary>
        public void ClearUserNotificationSettings(User user)
        {
            try
            {
                var userManager = _userManager;
                var options = userManager.GetTypedUserSetting(user.InternalId, NotificationSettingsKey);

                if (options is null) return;

                var notificationsProperty = options.GetType().GetProperty("Notifications");

                if (notificationsProperty?.CanWrite != true) return;

                if (!(notificationsProperty.GetValue(options) is Array notifications) ||
                    notifications.Length == 0)
                {
                    return;
                }

                var elementType = notificationsProperty.PropertyType.GetElementType();
                notificationsProperty.SetValue(options, Array.CreateInstance(elementType, 0));
                userManager.SetTypedUserSetting(user.InternalId, NotificationSettingsKey, options);

                _logger?.Info("UserNotification - Cleared notification settings: " + user.Name);
            }
            catch (Exception e)
            {
                _logger?.Error("UserNotification - Failed to clear notification settings: " + user.Name);
                _logger?.Error(e.Message);
                _logger?.Debug(e.StackTrace);
            }
        }

        public void DeepDeleteSendNotification(BaseItem item, User user, HashSet<string> mountPaths)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            var mountPathList = string.Join(Environment.NewLine, mountPaths);

            var request = new NotificationRequest
            {
                Title = Resources.PluginOptions_EditorTitle_Strm_Assistant + " - " +
                        Resources.Notification_DeepDelete_EventName,
                EventId = "deep.delete",
                User = user,
                Item = item,
                Description =
                    string.Format(
                        Resources.Notification_DeepDelete_EventDescription.Replace("\\n", Environment.NewLine),
                        item.Name, item.Path, mountPathList)
            };

            _notificationManager.SendNotification(request);
        }

        public void CollectionCreatedSendNotification(BoxSet collection)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            SendCollectionNotification(collection,
                string.Format(Resources.Notification_CollectionCreated_Description.Replace("\\n",
                    Environment.NewLine), collection.Name, collection.Path));
        }

        public void CollectionDeletedSendNotification(BoxSet collection)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            SendCollectionNotification(collection,
                string.Format(Resources.Notification_CollectionDeleted_Description.Replace("\\n",
                    Environment.NewLine), collection.Name, collection.Path));
        }

        public void CollectionUpdatedSendNotification(BoxSet collection)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            SendCollectionNotification(collection,
                string.Format(Resources.Notification_CollectionUpdated_Description.Replace("\\n",
                    Environment.NewLine), collection.Name, collection.Path));
        }

        public void CollectionItemUpdateSendNotification(BaseItem collection, BaseItem item, bool added)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            SendCollectionNotification(collection,
                string.Format(
                    (added
                        ? Resources.Notification_CollectionItemAdded_Description
                        : Resources.Notification_CollectionItemRemoved_Description).Replace("\\n",
                        Environment.NewLine), item.Name, collection.Name));
        }

        private void SendCollectionNotification(BaseItem collection, string description)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            var request = new NotificationRequest
            {
                Title = Resources.PluginOptions_EditorTitle_Strm_Assistant + " - " +
                        Resources.Notification_CollectionUpdate_EventName,
                EventId = "collection.update",
                Item = collection,
                Description = description
            };

            _notificationManager.SendNotification(request);
        }

        public void MetadataUpdateSendNotification(BaseItem item)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            var request = new NotificationRequest
            {
                Title = Resources.PluginOptions_EditorTitle_Strm_Assistant + " - " +
                        Resources.Notification_MetadataUpdate_EventName,
                EventId = "metadata.update",
                Item = item,
                Description = string.Format(
                    Resources.Notification_MetadataUpdate_Description.Replace("\\n", Environment.NewLine),
                    item.Name, item.Path)
            };

            _notificationManager.SendNotification(request);
        }

        public void ImageUpdateSendNotification(BaseItem item)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            var request = new NotificationRequest
            {
                Title = Resources.PluginOptions_EditorTitle_Strm_Assistant + " - " +
                        Resources.Notification_ImageUpdate_EventName,
                EventId = "image.update",
                Item = item,
                Description = string.Format(
                    Resources.Notification_ImageUpdate_Description.Replace("\\n", Environment.NewLine),
                    item.Name, item.Path)
            };

            _notificationManager.SendNotification(request);
        }

        public async Task IntroUpdateSendNotification(Episode episode, SessionInfo session, string introStartTime,
            string introEndTime)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            if (CanDisplayMessage(session))
            {
                var message = new MessageCommand
                {
                    Header = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                    Text = string.Format(
                        Resources.Notification_IntroUpdate_Message, episode.FindSeriesName(), episode.FindSeasonName()),
                    TimeoutMs = 500
                };
                await _sessionManager.SendMessageCommand(session.Id, session.Id, message, CancellationToken.None);
            }

            var request = new NotificationRequest
            {
                Title =
                    Resources.PluginOptions_EditorTitle_Strm_Assistant,
                EventId = "introskip.update",
                User = _userManager.GetUserById(session.UserInternalId),
                Item = episode,
                Session = session,
                Description = string.Format(
                    Resources.Notification_IntroUpdate_Description.Replace("\\n", Environment.NewLine),
                    episode.FindSeriesName(), episode.FindSeasonName(), introStartTime, introEndTime,
                    session.UserName)
            };
            _notificationManager.SendNotification(request);
        }

        public async Task CreditsUpdateSendNotification(Episode episode, SessionInfo session, string creditsDuration)
        {
            Resources.Culture = Thread.CurrentThread.CurrentUICulture;

            if (CanDisplayMessage(session))
            {
                var message = new MessageCommand
                {
                    Header = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                    Text = string.Format(
                        Resources.Notification_CreditsUpdate_Message, episode.FindSeriesName(), episode.FindSeasonName()),
                    TimeoutMs = 500
                };
                await _sessionManager.SendMessageCommand(session.Id, session.Id, message, CancellationToken.None);
            }

            var request = new NotificationRequest
            {
                Title =
                    Resources.PluginOptions_EditorTitle_Strm_Assistant,
                EventId = "introskip.update",
                User = _userManager.GetUserById(session.UserInternalId),
                Item = episode,
                Session = session,
                Description = string.Format(
                    Resources.Notification_CreditsUpdate_Description.Replace("\\n", Environment.NewLine),
                    episode.FindSeriesName(), episode.FindSeasonName(), creditsDuration, session.UserName)

            };
            _notificationManager.SendNotification(request);
        }

        public async Task SendMessageToAdmins(string text, long? timeout)
        {
            var message = new MessageCommand
            {
                Header = Resources.PluginOptions_EditorTitle_Strm_Assistant,
                Text = text,
                TimeoutMs = timeout
            };

            var admins = LibraryApi.AllUsers.Where(kvp => kvp.Value).Select(kvp => kvp.Key);
            var sessions = _sessionManager.Sessions.Where(CanDisplayMessage)
                .Where(s => admins.Any(u => s.ContainsUser(u.InternalId)));

            foreach (var session in sessions)
            {
                await _sessionManager.SendMessageCommand(session.Id, session.Id, message, CancellationToken.None);
            }
        }

        private bool CanDisplayMessage(SessionInfo session)
        {
            return session.SupportedCommands.Contains("DisplayMessage");
        }
    }
}
