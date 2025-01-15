using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using StrmAssistant.Common;
using StrmAssistant.ScheduledTask;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Mod
{
    internal class RefreshContext
    {
        public long InternalId { get; set; }
        public MetadataRefreshOptions MetadataRefreshOptions { get; set; }
        public bool MediaInfoNeedsUpdate { get; set; }
    }

    public static class ExclusiveExtract
    {
        private static readonly PatchApproachTracker PatchApproachTracker =
            new PatchApproachTracker(nameof(ExclusiveExtract));

        private static Assembly _mediaEncodingAssembly;
        private static MethodInfo _canRefreshMetadata;
        private static MethodInfo _canRefreshImage;
        private static MethodInfo _afterMetadataRefresh;
        private static MethodInfo _runFfProcess;

        private static MethodInfo _addVirtualFolder;
        private static MethodInfo _removeVirtualFolder;
        private static MethodInfo _addMediaPath;
        private static MethodInfo _removeMediaPath;

        private static MethodInfo _saveChapters;
        private static MethodInfo _deleteChapters;

        private static readonly Dictionary<Type, PropertyInfo> RefreshLibraryPropertyCache =
            new Dictionary<Type, PropertyInfo>();

        private static readonly AsyncLocal<long> ExclusiveItem = new AsyncLocal<long>();
        private static readonly AsyncLocal<long> ProtectIntroItem = new AsyncLocal<long>();

        private static AsyncLocal<RefreshContext> CurrentRefreshContext { get; } = new AsyncLocal<RefreshContext>();

        public static void Initialize()
        {
            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var providerManager = embyProviders.GetType("Emby.Providers.Manager.ProviderManager");
                _canRefreshMetadata = providerManager.GetMethod("CanRefresh",
                    BindingFlags.Static | BindingFlags.NonPublic, null,
                    new Type[]
                    {
                        typeof(IMetadataProvider), typeof(BaseItem), typeof(LibraryOptions), typeof(bool),
                        typeof(bool), typeof(bool)
                    }, null);
                _canRefreshImage = providerManager.GetMethod("CanRefresh",
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new Type[]
                    {
                        typeof(IImageProvider), typeof(BaseItem), typeof(LibraryOptions),
                        typeof(ImageRefreshOptions), typeof(bool), typeof(bool)
                    }, null);
                _afterMetadataRefresh =
                    typeof(BaseItem).GetMethod("AfterMetadataRefresh", BindingFlags.Instance | BindingFlags.Public);

                _mediaEncodingAssembly = Assembly.Load("Emby.Server.MediaEncoding");
                var mediaProbeManager =
                    _mediaEncodingAssembly.GetType("Emby.Server.MediaEncoding.Probing.MediaProbeManager");
                _runFfProcess =
                    mediaProbeManager.GetMethod("RunFfProcess", BindingFlags.Instance | BindingFlags.NonPublic);

                var embyApi = Assembly.Load("Emby.Api");
                var libraryStructureService = embyApi.GetType("Emby.Api.Library.LibraryStructureService");
                _addVirtualFolder = libraryStructureService.GetMethod("Post",
                    new[] { embyApi.GetType("Emby.Api.Library.AddVirtualFolder") });
                _removeVirtualFolder = libraryStructureService.GetMethod("Any",
                    new[] { embyApi.GetType("Emby.Api.Library.RemoveVirtualFolder") });
                _addMediaPath = libraryStructureService.GetMethod("Post",
                    new[] { embyApi.GetType("Emby.Api.Library.AddMediaPath") });
                _removeMediaPath = libraryStructureService.GetMethod("Any",
                    new[] { embyApi.GetType("Emby.Api.Library.RemoveMediaPath") });

                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                var sqliteItemRepository =
                    embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
                _saveChapters = sqliteItemRepository.GetMethod("SaveChapters",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(long), typeof(bool), typeof(List<ChapterInfo>) }, null);
                _deleteChapters =
                    sqliteItemRepository.GetMethod("DeleteChapters", BindingFlags.Instance | BindingFlags.Public);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn("ExclusiveExtract - Patch Init Failed");
                Plugin.Instance.Logger.Debug(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
                PatchApproachTracker.FallbackPatchApproach = PatchApproach.None;
            }

            if (HarmonyMod == null) PatchApproachTracker.FallbackPatchApproach = PatchApproach.Reflection;

            if (PatchApproachTracker.FallbackPatchApproach != PatchApproach.None)
            {
                PatchFFProbeTimeout();

                if (Plugin.Instance.MediaInfoExtractStore.GetOptions().ExclusiveExtract)
                {
                    UpdateExclusiveControlFeatures(Plugin.Instance.MediaInfoExtractStore.GetOptions()
                        .ExclusiveControlFeatures);
                    Patch();
                }
            }
        }

        private static void PatchFFProbeTimeout()
        {
            if (PatchApproachTracker.FallbackPatchApproach == PatchApproach.Harmony)
            {
                try
                {
                    if (!IsPatched(_runFfProcess, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Patch(_runFfProcess,
                            prefix: new HarmonyMethod(typeof(ExclusiveExtract).GetMethod("RunFfProcessPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug(
                            "Patch RunFfProcess Success by Harmony");
                    }
                }
                catch (Exception he)
                {
                    Plugin.Instance.Logger.Debug("Patch RunFfProcess Failed by Harmony");
                    Plugin.Instance.Logger.Debug(he.Message);
                    Plugin.Instance.Logger.Debug(he.StackTrace);
                    PatchApproachTracker.FallbackPatchApproach = PatchApproach.Reflection;
                }
            }
        }

        private static void UnpatchFFProbeTimeout()
        {
            if (PatchApproachTracker.FallbackPatchApproach == PatchApproach.Harmony)
            {
                try
                {
                    if (IsPatched(_runFfProcess, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Unpatch(_runFfProcess, HarmonyPatchType.Prefix);
                        Plugin.Instance.Logger.Debug("Unpatch RunFfProcess Success by Harmony");
                    }
                }
                catch (Exception he)
                {
                    Plugin.Instance.Logger.Debug("Unpatch RunFfProcess Failed by Harmony");
                    Plugin.Instance.Logger.Debug(he.Message);
                    Plugin.Instance.Logger.Debug(he.StackTrace);
                }
            }
        }

        public static void Patch()
        {
            if (PatchApproachTracker.FallbackPatchApproach == PatchApproach.Harmony)
            {
                try
                {
                    if (!IsPatched(_canRefreshMetadata, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Patch(_canRefreshMetadata,
                            prefix: new HarmonyMethod(typeof(ExclusiveExtract).GetMethod("CanRefreshMetadataPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)),
                            postfix: new HarmonyMethod(typeof(ExclusiveExtract).GetMethod("CanRefreshMetadataPostfix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug(
                            "Patch CanRefreshMetadata Success by Harmony");
                    }
                    if (!IsPatched(_canRefreshImage, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Patch(_canRefreshImage,
                            prefix: new HarmonyMethod(typeof(ExclusiveExtract).GetMethod("CanRefreshImagePrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug(
                            "Patch CanRefreshImage Success by Harmony");
                    }
                    if (!IsPatched(_afterMetadataRefresh, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Patch(_afterMetadataRefresh,
                            prefix: new HarmonyMethod(typeof(ExclusiveExtract).GetMethod("AfterMetadataRefreshPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug("Patch AfterMetadataRefresh Success by Harmony");
                    }
                    if (!IsPatched(_addVirtualFolder, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Patch(_addVirtualFolder,
                            prefix: new HarmonyMethod(typeof(ExclusiveExtract).GetMethod("RefreshLibraryPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug(
                            "Patch AddVirtualFolder Success by Harmony");
                    }
                    if (!IsPatched(_removeVirtualFolder, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Patch(_removeVirtualFolder,
                            prefix: new HarmonyMethod(typeof(ExclusiveExtract).GetMethod("RefreshLibraryPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug(
                            "Patch RemoveVirtualFolder Success by Harmony");
                    }
                    if (!IsPatched(_addMediaPath, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Patch(_addMediaPath,
                            prefix: new HarmonyMethod(typeof(ExclusiveExtract).GetMethod("RefreshLibraryPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug(
                            "Patch AddMediaPath Success by Harmony");
                    }
                    if (!IsPatched(_removeMediaPath, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Patch(_removeMediaPath,
                            prefix: new HarmonyMethod(typeof(ExclusiveExtract).GetMethod("RefreshLibraryPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug(
                            "Patch RemoveMediaPath Success by Harmony");
                    }
                    if (!IsPatched(_saveChapters, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Patch(_saveChapters,
                            prefix: new HarmonyMethod(typeof(ExclusiveExtract).GetMethod("SaveChaptersPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug(
                            "Patch SaveChapters Success by Harmony");
                    }
                    if (!IsPatched(_deleteChapters, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Patch(_deleteChapters,
                            prefix: new HarmonyMethod(typeof(ExclusiveExtract).GetMethod("DeleteChaptersPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug(
                            "Patch DeleteChapters Success by Harmony");
                    }
                }
                catch (Exception he)
                {
                    Plugin.Instance.Logger.Debug("Patch ExclusiveExtract Failed by Harmony");
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
                    if (IsPatched(_canRefreshMetadata, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Unpatch(_canRefreshMetadata,
                            AccessTools.Method(typeof(ExclusiveExtract), "CanRefreshMetadataPrefix"));
                        HarmonyMod.Unpatch(_canRefreshMetadata,
                            AccessTools.Method(typeof(ExclusiveExtract), "CanRefreshMetadataPostfix"));
                        Plugin.Instance.Logger.Debug("Unpatch CanRefreshMetadata Success by Harmony");
                    }
                    if (IsPatched(_canRefreshImage, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Unpatch(_canRefreshImage,
                            AccessTools.Method(typeof(ExclusiveExtract), "CanRefreshImagePrefix"));
                        Plugin.Instance.Logger.Debug("Unpatch CanRefreshImage Success by Harmony");
                    }
                    if (IsPatched(_afterMetadataRefresh, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Unpatch(_afterMetadataRefresh,
                            AccessTools.Method(typeof(ExclusiveExtract), "AfterMetadataRefreshPrefix"));
                        Plugin.Instance.Logger.Debug("Unpatch AfterMetadataRefresh Success by Harmony");
                    }
                    if (IsPatched(_addVirtualFolder, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Unpatch(_addVirtualFolder,
                            AccessTools.Method(typeof(ExclusiveExtract), "RefreshLibraryPrefix"));
                        Plugin.Instance.Logger.Debug("Unpatch AddVirtualFolder Success by Harmony");
                    }
                    if (IsPatched(_removeVirtualFolder, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Unpatch(_removeVirtualFolder,
                            AccessTools.Method(typeof(ExclusiveExtract), "RefreshLibraryPrefix"));
                        Plugin.Instance.Logger.Debug("Unpatch RemoveVirtualFolder Success by Harmony");
                    }
                    if (IsPatched(_addMediaPath, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Unpatch(_addMediaPath,
                            AccessTools.Method(typeof(ExclusiveExtract), "RefreshLibraryPrefix"));
                        Plugin.Instance.Logger.Debug("Unpatch AddMediaPath Success by Harmony");
                    }
                    if (IsPatched(_removeMediaPath, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Unpatch(_removeMediaPath,
                            AccessTools.Method(typeof(ExclusiveExtract), "RefreshLibraryPrefix"));
                        Plugin.Instance.Logger.Debug("Unpatch RemoveMediaPath Success by Harmony");
                    }
                    if (IsPatched(_saveChapters, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Unpatch(_saveChapters,
                            AccessTools.Method(typeof(ExclusiveExtract), "SaveChaptersPrefix"));
                        Plugin.Instance.Logger.Debug("Unpatch SaveChapters Success by Harmony");
                    }
                    if (IsPatched(_deleteChapters, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Unpatch(_deleteChapters,
                            AccessTools.Method(typeof(ExclusiveExtract), "DeleteChaptersPrefix"));
                        Plugin.Instance.Logger.Debug("Unpatch DeleteChapters Success by Harmony");
                    }
                }
                catch (Exception he)
                {
                    Plugin.Instance.Logger.Debug("Unpatch ExclusiveExtract Failed by Harmony");
                    Plugin.Instance.Logger.Debug(he.Message);
                    Plugin.Instance.Logger.Debug(he.StackTrace);
                }
            }
        }

        public static void AllowExtractInstance(BaseItem item)
        {
            ExclusiveItem.Value = item.InternalId;
        }

        [HarmonyPrefix]
        private static void RunFfProcessPrefix(ref int timeoutMs)
        {
            if (ExtractMediaInfoTask.IsRunning || QueueManager.IsMediaInfoProcessTaskRunning)
            {
                timeoutMs = 60000 * Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.MaxConcurrentCount;
            }
        }

        [HarmonyPrefix]
        private static bool CanRefreshMetadataPrefix(IMetadataProvider provider, BaseItem item,
            LibraryOptions libraryOptions, bool includeDisabled, bool forceEnableInternetMetadata,
            bool ignoreMetadataLock, ref bool __result, out bool __state)
        {
            if ((item.Parent is null && item.ExtraType is null) || !(provider is IPreRefreshProvider) ||
                !(provider is ICustomMetadataProvider<Video>))
            {
                __state = false;
                return true;
            }
            
            __state = true;

            ChapterChangeTracker.BypassInstance(item);

            if (ExclusiveItem.Value != 0 && ExclusiveItem.Value == item.InternalId) return true;

            if (item.DateLastRefreshed == DateTimeOffset.MinValue)
            {
                __result = false;
                return false;
            }

            if (!IsExclusiveFeatureSelected(ExclusiveControl.IgnoreFileChange) && CurrentRefreshContext.Value != null &&
                Plugin.LibraryApi.HasFileChanged(item, CurrentRefreshContext.Value.MetadataRefreshOptions.DirectoryService))
            {
                if (IsExclusiveFeatureSelected(ExclusiveControl.ExtractOnFileChange) && item.IsShortcut &&
                    Plugin.LibraryApi.HasMediaInfo(item))
                {
                    CurrentRefreshContext.Value.MetadataRefreshOptions.EnableRemoteContentProbe = true;
                    EnableImageCapture.AllowImageCaptureInstance(item);
                }

                CurrentRefreshContext.Value.MediaInfoNeedsUpdate = true;
                return true;
            }

            if (CurrentRefreshContext.Value != null && CurrentRefreshContext.Value.InternalId == item.InternalId &&
                (CurrentRefreshContext.Value.MetadataRefreshOptions.MetadataRefreshMode <=
                    MetadataRefreshMode.Default &&
                    CurrentRefreshContext.Value.MetadataRefreshOptions.ImageRefreshMode <=
                    MetadataRefreshMode.Default || !IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) &&
                    CurrentRefreshContext.Value.MetadataRefreshOptions.SearchResult != null))
            {
                __result = false;
                return false;
            }

            if (!IsExclusiveFeatureSelected(ExclusiveControl.CatchAllBlock) && !item.IsShortcut &&
                CurrentRefreshContext.Value != null &&
                CurrentRefreshContext.Value.MetadataRefreshOptions.ReplaceAllImages)
            {
                CurrentRefreshContext.Value.MediaInfoNeedsUpdate = true;
                return true;
            }

            if (!IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) && Plugin.LibraryApi.HasMediaInfo(item))
            {
                __result = false;
                return false;
            }

            if (CurrentRefreshContext.Value != null && (IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) ||
                                                        !IsExclusiveFeatureSelected(ExclusiveControl.CatchAllBlock) &&
                                                        !item.IsShortcut))
            {
                CurrentRefreshContext.Value.MediaInfoNeedsUpdate = true;
                return true;
            }

            if (IsExclusiveFeatureSelected(ExclusiveControl.CatchAllBlock) &&
                !(CurrentRefreshContext.Value != null && CurrentRefreshContext.Value.InternalId == item.InternalId &&
                  CurrentRefreshContext.Value.MetadataRefreshOptions.MetadataRefreshMode ==
                  MetadataRefreshMode.FullRefresh &&
                  CurrentRefreshContext.Value.MetadataRefreshOptions.ImageRefreshMode == MetadataRefreshMode.Default &&
                  !CurrentRefreshContext.Value.MetadataRefreshOptions.ReplaceAllMetadata &&
                  !CurrentRefreshContext.Value.MetadataRefreshOptions.ReplaceAllImages))
            {
                return false;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void CanRefreshMetadataPostfix(IMetadataProvider provider, BaseItem item,
            LibraryOptions libraryOptions, bool includeDisabled, bool forceEnableInternetMetadata,
            bool ignoreMetadataLock, ref bool __result, bool __state)
        {
            if (__state && item.DateLastRefreshed != DateTimeOffset.MinValue)
            {
                if (__result && !IsExclusiveFeatureSelected(ExclusiveControl.NoIntroProtect) &&
                    item is Episode && Plugin.ChapterApi.HasIntro(item))
                {
                    ProtectIntroItem.Value = item.InternalId;
                }

                if (!__result && !IsExclusiveFeatureSelected(ExclusiveControl.IgnoreExtSubChange) && item is Video &&
                    CurrentRefreshContext.Value != null && Plugin.SubtitleApi.HasExternalSubtitleChanged(item,
                        CurrentRefreshContext.Value.MetadataRefreshOptions.DirectoryService))
                {
                    _ = Plugin.SubtitleApi.UpdateExternalSubtitles(item, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        [HarmonyPrefix]
        private static bool CanRefreshImagePrefix(IImageProvider provider, BaseItem item, LibraryOptions libraryOptions,
            ImageRefreshOptions refreshOptions, bool ignoreMetadataLock, bool ignoreLibraryOptions, ref bool __result)
        {
            if ((item.Parent is null && item.ExtraType is null) || !provider.Supports(item) ||
                !(item is Video || item is Audio))
                return true;

            if (refreshOptions is MetadataRefreshOptions options)
            {
                if (ExclusiveItem.Value != 0 && ExclusiveItem.Value == item.InternalId) return true;

                if (CurrentRefreshContext.Value == null)
                {
                    CurrentRefreshContext.Value = new RefreshContext
                    {
                        InternalId = item.InternalId,
                        MetadataRefreshOptions = options,
                        MediaInfoNeedsUpdate = false
                    };

                    if (IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow))
                    {
                        options.EnableRemoteContentProbe = true;
                        EnableImageCapture.AllowImageCaptureInstance(item);
                    }
                }

                if (item.DateLastRefreshed == DateTimeOffset.MinValue) return true;

                if (!item.IsShortcut &&
                    item.HasImage(ImageType.Primary) && provider is IDynamicImageProvider &&
                    provider.GetType().Name == "VideoImageProvider" &&
                    (IsExclusiveFeatureSelected(ExclusiveControl.CatchAllBlock) ||
                     !IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) &&
                     !refreshOptions.ReplaceAllImages))
                {
                    __result = false;
                    return false;
                }

                if (item.HasImage(ImageType.Primary) && provider is IRemoteImageProvider &&
                    (IsExclusiveFeatureSelected(ExclusiveControl.CatchAllBlock) ||
                     !IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) && item.IsShortcut &&
                     !refreshOptions.ReplaceAllImages))
                {
                    __result = false;
                    return false;
                }
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool AfterMetadataRefreshPrefix(BaseItem __instance)
        {
            if (Plugin.Instance.MediaInfoExtractStore.GetOptions().PersistMediaInfo &&
                !IsExclusiveFeatureSelected(ExclusiveControl.NoPersistIntegration) &&
                (__instance is Video || __instance is Audio) && Plugin.LibraryApi.IsLibraryInScope(__instance) &&
                CurrentRefreshContext.Value != null &&
                CurrentRefreshContext.Value.InternalId == __instance.InternalId && ExclusiveItem.Value == 0)
            {
                var directoryService = CurrentRefreshContext.Value.MetadataRefreshOptions.DirectoryService;

                if (CurrentRefreshContext.Value.MediaInfoNeedsUpdate)
                {
                    if (__instance.IsShortcut &&
                        !CurrentRefreshContext.Value.MetadataRefreshOptions.EnableRemoteContentProbe)
                    {
                        _ = Plugin.LibraryApi.DeleteMediaInfoJson(__instance, directoryService,
                            "Exclusive Delete on Change", CancellationToken.None);
                    }
                    else
                    {
                        _ = Plugin.LibraryApi.SerializeMediaInfo(__instance, directoryService, true,
                            "Exclusive Overwrite", CancellationToken.None);
                    }
                }
                else if (!Plugin.LibraryApi.HasMediaInfo(__instance))
                {
                    _ = Plugin.LibraryApi.DeserializeMediaInfo(__instance, directoryService, "Exclusive Restore",
                        CancellationToken.None);
                }
                else
                {
                    _ = Plugin.LibraryApi.SerializeMediaInfo(__instance, directoryService, false,
                        "Exclusive Non-existence", CancellationToken.None);
                }

                CurrentRefreshContext.Value = null;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool RefreshLibraryPrefix(object request)
        {
            var requestType = request.GetType();

            if (!RefreshLibraryPropertyCache.TryGetValue(requestType, out var refreshLibraryProperty))
            {
                refreshLibraryProperty = requestType.GetProperty("RefreshLibrary");
                RefreshLibraryPropertyCache[requestType] = refreshLibraryProperty;
            }

            if (refreshLibraryProperty != null && refreshLibraryProperty.CanWrite)
            {
                refreshLibraryProperty.SetValue(request, false);
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool SaveChaptersPrefix(long itemId, bool clearExtractionFailureResult,
            List<ChapterInfo> chapters)
        {
            if (ProtectIntroItem.Value != 0 && ProtectIntroItem.Value == itemId) return false;

            return true;
        }

        [HarmonyPrefix]
        private static bool DeleteChaptersPrefix(long itemId, MarkerType[] markerTypes)
        {
            if (ProtectIntroItem.Value != 0 && ProtectIntroItem.Value == itemId) return false;

            return true;
        }
    }
}
