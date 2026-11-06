using Emby.Notifications;
using MediaBrowser.Controller;
using StrmAssistant.Properties;
using System.Collections.Generic;

namespace StrmAssistant.Notification
{
    public class CustomNotifications : INotificationTypeFactory
    {
        private readonly IServerApplicationHost _appHost;

        public CustomNotifications(IServerApplicationHost appHost) => _appHost = appHost;

        public List<NotificationTypeInfo> GetNotificationTypes(string language)
        {
            return new List<NotificationTypeInfo>
            {
                new NotificationTypeInfo
                {
                    Id = "favorites.update",
                    Name = Resources.Notification_CatchupUpdate_EventName,
                    CategoryId = "strm.assistant",
                    CategoryName = Resources.PluginOptions_EditorTitle_Strm_Assistant
                },
                new NotificationTypeInfo
                {
                    Id = "introskip.update",
                    Name = Resources.Notification_IntroSkipUpdate_EventName,
                    CategoryId = "strm.assistant",
                    CategoryName = Resources.PluginOptions_EditorTitle_Strm_Assistant
                },
                new NotificationTypeInfo
                {
                    Id = "collection.update",
                    Name = Resources.Notification_CollectionUpdate_EventName,
                    CategoryId = "strm.assistant",
                    CategoryName = Resources.PluginOptions_EditorTitle_Strm_Assistant
                },
                new NotificationTypeInfo
                {
                    Id = "collection.items.added",
                    Name = Resources.Notification_CollectionItemsAdded_EventName,
                    CategoryId = "strm.assistant",
                    CategoryName = Resources.PluginOptions_EditorTitle_Strm_Assistant
                },
                new NotificationTypeInfo
                {
                    Id = "collection.items.removed",
                    Name = Resources.Notification_CollectionItemsRemoved_EventName,
                    CategoryId = "strm.assistant",
                    CategoryName = Resources.PluginOptions_EditorTitle_Strm_Assistant
                },
                new NotificationTypeInfo
                {
                    Id = "metadata.update",
                    Name = Resources.Notification_MetadataUpdate_EventName,
                    CategoryId = "strm.assistant",
                    CategoryName = Resources.PluginOptions_EditorTitle_Strm_Assistant
                },
                new NotificationTypeInfo
                {
                    Id = "image.update",
                    Name = Resources.Notification_ImageUpdate_EventName,
                    CategoryId = "strm.assistant",
                    CategoryName = Resources.PluginOptions_EditorTitle_Strm_Assistant
                }
            };
        }
    }
}
