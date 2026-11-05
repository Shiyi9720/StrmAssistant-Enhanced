using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public class NotificationOptions : EditableOptionsBase
    {
        [DisplayNameL("NotificationOptions_EditorTitle_Notification", typeof(Resources))]
        public override string EditorTitle => Resources.NotificationOptions_EditorTitle_Notification;

        [DisplayNameL("NotificationOptions_EnableCollectionNotification_Collection_Changes", typeof(Resources))]
        [DescriptionL("NotificationOptions_EnableCollectionNotification_Send_a_notification_when_a_collection_is_created_deleted_or_its_entries_change__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool EnableCollectionNotification { get; set; } = false;

        [DisplayNameL("NotificationOptions_EnableMetadataNotification_Metadata_Updates", typeof(Resources))]
        [DescriptionL("NotificationOptions_EnableMetadataNotification_Send_a_notification_when_an_item_s_metadata_is_updated__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool EnableMetadataNotification { get; set; } = false;

        [DisplayNameL("NotificationOptions_EnableImageNotification_Image_Updates", typeof(Resources))]
        [DescriptionL("NotificationOptions_EnableImageNotification_Send_a_notification_when_an_item_s_image_is_updated__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool EnableImageNotification { get; set; } = false;
    }
}
