using Emby.Notifications;
using HarmonyLib;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public static class EnhanceNotificationSystem
    {
        private static readonly PatchApproachTracker PatchApproachTracker =
            new PatchApproachTracker(nameof(EnhanceNotificationSystem));

        private static MethodInfo _convertToGroups;
        private static MethodInfo _sendNotification;

        private static readonly AsyncLocal<Dictionary<long, List<ItemChangeEventArgs>>> GroupDetails =
            new AsyncLocal<Dictionary<long, List<ItemChangeEventArgs>>>();

        public static void Initialize()
        {
            try
            {
                var notificationsAssembly = Assembly.Load("Emby.Notifications");
                var notificationManager = notificationsAssembly.GetType("Emby.Notifications.NotificationManager");
                _convertToGroups = notificationManager.GetMethod("ConvertToGroups",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                _sendNotification = notificationManager.GetMethod("SendNotification",
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new[] { typeof(INotifier), typeof(NotificationInfo[]), typeof(NotificationRequest), typeof(bool) },
                    null);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn("EnhanceNotificationSystem - Patch Init Failed");
                Plugin.Instance.Logger.Debug(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
                PatchApproachTracker.FallbackPatchApproach = PatchApproach.None;
            }

            if (HarmonyMod == null) PatchApproachTracker.FallbackPatchApproach = PatchApproach.Reflection;

            if (PatchApproachTracker.FallbackPatchApproach != PatchApproach.None &&
                Plugin.Instance.ExperienceEnhanceStore.GetOptions().EnhanceNotificationSystem)
            {
                Patch();
            }
        }

        public static void Patch()
        {
            if (PatchApproachTracker.FallbackPatchApproach == PatchApproach.Harmony)
            {
                try
                {
                    if (!IsPatched(_convertToGroups, typeof(EnhanceNotificationSystem)))
                    {
                        HarmonyMod.Patch(_convertToGroups,
                            postfix: new HarmonyMethod(typeof(EnhanceNotificationSystem).GetMethod("ConvertToGroupsPostfix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug(
                            "Patch ConvertToGroups Success by Harmony");
                    }
                    if (!IsPatched(_sendNotification, typeof(EnhanceNotificationSystem)))
                    {
                        HarmonyMod.Patch(_sendNotification,
                            prefix: new HarmonyMethod(typeof(EnhanceNotificationSystem).GetMethod("SendNotificationPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug(
                            "Patch SendNotification Success by Harmony");
                    }
                }
                catch (Exception he)
                {
                    Plugin.Instance.Logger.Debug("Patch EnhanceNotificationSystem Failed by Harmony");
                    Plugin.Instance.Logger.Debug(he.Message);
                    Plugin.Instance.Logger.Debug(he.StackTrace);
                    PatchApproachTracker.FallbackPatchApproach = PatchApproach.Reflection;
                }
            }
        }

        public static void Unpatch()
        {
            if (PatchApproachTracker.FallbackPatchApproach == PatchApproach.Harmony)
            {
                try
                {
                    if (IsPatched(_convertToGroups, typeof(EnhanceNotificationSystem)))
                    {
                        HarmonyMod.Unpatch(_convertToGroups,
                            AccessTools.Method(typeof(EnhanceNotificationSystem), "ConvertToGroupsPostfix"));
                        Plugin.Instance.Logger.Debug("Unpatch ConvertToGroups Success by Harmony");
                    }
                    if (IsPatched(_sendNotification, typeof(EnhanceNotificationSystem)))
                    {
                        HarmonyMod.Unpatch(_sendNotification,
                            AccessTools.Method(typeof(EnhanceNotificationSystem), "SendNotificationPrefix"));
                        Plugin.Instance.Logger.Debug("Unpatch SendNotification Success by Harmony");
                    }
                }
                catch (Exception he)
                {
                    Plugin.Instance.Logger.Debug("Unpatch EnhanceNotificationSystem Failed by Harmony");
                    Plugin.Instance.Logger.Debug(he.Message);
                    Plugin.Instance.Logger.Debug(he.StackTrace);
                }
            }
        }

        [HarmonyPostfix]
        private static void ConvertToGroupsPostfix(ItemChangeEventArgs[] list,
            ref Dictionary<long, List<ItemChangeEventArgs>> __result)
        {
            var filteredItems = list.Where(i => i.Item.SeriesId != 0L).ToArray();

            if (filteredItems.Length == 0) return;

            GroupDetails.Value = filteredItems.GroupBy(i => i.Item.SeriesId).ToDictionary(g => g.Key, g => g.ToList());
        }

        [HarmonyPrefix]
        private static bool SendNotificationPrefix(INotifier notifier, NotificationInfo[] notifications,
            NotificationRequest request, bool enableUserDataInDto)
        {
            if (notifications.FirstOrDefault()?.GroupItems is true
                && request.Item is Series series
                && GroupDetails.Value.Remove(series.InternalId, out var groupDetails))
            {
                var episodes = groupDetails
                    .Select(d => d.Item)
                    .OfType<Episode>()
                    .Where(e => e.ParentIndexNumber.HasValue)
                    .ToList();

                var groupedBySeason = episodes.GroupBy(e => e.ParentIndexNumber).OrderBy(g => g.Key);

                var descriptions = new List<string>();

                foreach (var seasonGroup in groupedBySeason)
                {
                    var seasonIndex = seasonGroup.Key;
                    var episodesBySeason = seasonGroup
                        .Where(e => e.IndexNumber.HasValue)
                        .OrderBy(e => e.IndexNumber.Value)
                        .Select(e => e.IndexNumber.Value)
                        .ToList();

                    if (!episodesBySeason.Any()) continue;

                    var episodeRanges = new List<string>();
                    var rangeStart = episodesBySeason[0];
                    var lastEpisodeInRange = rangeStart;

                    for (var i = 1; i < episodesBySeason.Count; i++)
                    {
                        var current = episodesBySeason[i];
                        if (current != lastEpisodeInRange + 1)
                        {
                            episodeRanges.Add(rangeStart == lastEpisodeInRange
                                ? $"E{rangeStart:D2}"
                                : $"E{rangeStart:D2}-E{lastEpisodeInRange:D2}");  
                            rangeStart = current;
                        }

                        lastEpisodeInRange = current;
                    }

                    episodeRanges.Add(rangeStart == lastEpisodeInRange
                        ? $"E{rangeStart:D2}"
                        : $"E{rangeStart:D2}-E{lastEpisodeInRange:D2}");  

                    descriptions.Add($"S{seasonIndex:D2} {string.Join(", ", episodeRanges)}");
                }

                var summary = string.Join(" / ", descriptions);

                var tmdbId = series.GetProviderId(MetadataProviders.Tmdb);

                request.Description = summary;

                if (!string.IsNullOrEmpty(tmdbId))
                {
                    request.Description += $"{Environment.NewLine}{Environment.NewLine}TmdbId: {tmdbId}";
                }
            }

            return true;
        }
    }
}
