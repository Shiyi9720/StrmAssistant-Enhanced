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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Mod
{
    internal class RefreshContext
    {
        public long InternalId { get; set; }
        public MetadataRefreshOptions MetadataRefreshOptions { get; set; }
        public bool IsNewItem { get; set; }
        public bool IsFileChanged { get; set; }
        public bool IsExternalSubtitleChanged { get; set; }
        public bool IsPersistInScope { get; set; }
        public bool MediaInfoUpdated { get; set; }
    }

    public static class ExclusiveExtract
    {
        private static readonly PatchApproachTracker PatchApproachTracker =
            new PatchApproachTracker(nameof(ExclusiveExtract));

        private static MethodInfo _canRefreshMetadata;
        private static MethodInfo _canRefreshImage;
        private static MethodInfo _afterMetadataRefresh;

        private static MethodInfo _runFfProcess;
        private static PropertyInfo _standardOutput;
        private static PropertyInfo _standardError;

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

                var mediaEncodingAssembly = Assembly.Load("Emby.Server.MediaEncoding");
                var mediaProbeManager =
                    mediaEncodingAssembly.GetType("Emby.Server.MediaEncoding.Probing.MediaProbeManager");
                _runFfProcess =
                    mediaProbeManager.GetMethod("RunFfProcess", BindingFlags.Instance | BindingFlags.NonPublic);
                var processRunAssembly = Assembly.Load("Emby.ProcessRun");
                var processResult = processRunAssembly.GetType("Emby.ProcessRun.Common.ProcessResult");
                _standardOutput = processResult.GetProperty("StandardOutput");
                _standardError = processResult.GetProperty("StandardError");
                
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
                PatchFFProbeProcess();

                if (Plugin.Instance.MediaInfoExtractStore.GetOptions().ExclusiveExtract)
                {
                    UpdateExclusiveControlFeatures(Plugin.Instance.MediaInfoExtractStore.GetOptions()
                        .ExclusiveControlFeatures);
                    Patch();
                }
            }
        }

        private static void PatchFFProbeProcess()
        {
            if (PatchApproachTracker.FallbackPatchApproach == PatchApproach.Harmony)
            {
                try
                {
                    if (!IsPatched(_runFfProcess, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Patch(_runFfProcess,
                            prefix: new HarmonyMethod(typeof(ExclusiveExtract).GetMethod("RunFfProcessPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)),
                            postfix: new HarmonyMethod(typeof(ExclusiveExtract).GetMethod("RunFfProcessPostfix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug("Patch RunFfProcess Success by Harmony");
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

        private static void UnpatchFFProbeProcess()
        {
            if (PatchApproachTracker.FallbackPatchApproach == PatchApproach.Harmony)
            {
                try
                {
                    if (IsPatched(_runFfProcess, typeof(ExclusiveExtract)))
                    {
                        HarmonyMod.Unpatch(_runFfProcess,
                            AccessTools.Method(typeof(ExclusiveExtract), "RunFfProcessPrefix"));
                        HarmonyMod.Unpatch(_runFfProcess,
                            AccessTools.Method(typeof(ExclusiveExtract), "RunFfProcessPostfix"));
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
            if (!IsExclusiveFeatureSelected(ExclusiveControl.NoIntroProtect) &&
                item.DateLastRefreshed != DateTimeOffset.MinValue && item is Episode &&
                Plugin.ChapterApi.HasIntro(item))
            {
                ProtectIntroItem.Value = item.InternalId;
            }

            ExclusiveItem.Value = item.InternalId;
        }

        [HarmonyPrefix]
        private static void RunFfProcessPrefix(ref int timeoutMs)
        {
            if (ExclusiveItem.Value != 0)
            {
                timeoutMs = 60000 * Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.MaxConcurrentCount;
            }
        }

        [HarmonyPostfix]
        private static void RunFfProcessPostfix(ref object __result)
        {
            if (__result is Task task)
            {
                object result = null;

                try
                {
                    result = task.GetType().GetProperty("Result")?.GetValue(task);
                }
                
                catch
                {
                    // ignored
                }

                if (result != null)
                {
                    var standardOutput = _standardOutput.GetValue(result) as string;
                    var standardError = _standardError.GetValue(result) as string;

                    if (standardOutput != null && standardError != null)
                    {
                        var partialOutput = standardOutput.Length > 20
                            ? standardOutput.Substring(0, 20)
                            : standardOutput;

                        if (Regex.Replace(partialOutput, @"\s+", "") == "{}")
                        {
                            var lines = standardError.Split(new[] { '\r', '\n' },
                                StringSplitOptions.RemoveEmptyEntries);

                            if (lines.Length > 0)
                            {
                                var errorMessage = lines[lines.Length - 1].Trim();

                                Plugin.Instance.Logger.Error("MediaInfoExtract - FfProbe Error: " + errorMessage);
                            }
                        }
                    }
                }
            }
        }

        [HarmonyPrefix]
        private static bool CanRefreshImagePrefix(IImageProvider provider, BaseItem item, LibraryOptions libraryOptions,
            ImageRefreshOptions refreshOptions, bool ignoreMetadataLock, bool ignoreLibraryOptions, ref bool __result)
        {
            if ((item.Parent is null && item.ExtraType is null) || !provider.Supports(item) ||
                !(item is Video || item is Audio))
            {
                return true;
            }

            if (ExclusiveItem.Value != 0 && ExclusiveItem.Value == item.InternalId) return true;
            
            if (refreshOptions is MetadataRefreshOptions options)
            {
                if (CurrentRefreshContext.Value is null)
                {
                    CurrentRefreshContext.Value = new RefreshContext
                    {
                        InternalId = item.InternalId,
                        MetadataRefreshOptions = options,
                        IsNewItem = item.DateLastRefreshed == DateTimeOffset.MinValue
                    };

                    if (!CurrentRefreshContext.Value.IsNewItem)
                    {
                        if (!IsExclusiveFeatureSelected(ExclusiveControl.IgnoreFileChange) &&
                            Plugin.LibraryApi.HasFileChanged(item, options.DirectoryService))
                        {
                            CurrentRefreshContext.Value.IsFileChanged = true;
                        }

                        if (!IsExclusiveFeatureSelected(ExclusiveControl.IgnoreExtSubChange) &&
                            item is Video && Plugin.SubtitleApi.HasExternalSubtitleChanged(item, options.DirectoryService))
                        {
                            CurrentRefreshContext.Value.IsExternalSubtitleChanged = true;
                        }

                        if (item.IsShortcut && (CurrentRefreshContext.Value.IsFileChanged &&
                                                IsExclusiveFeatureSelected(ExclusiveControl.ExtractOnFileChange) &&
                                                Plugin.LibraryApi.HasMediaInfo(item) ||
                                                IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow)))
                        {
                            options.EnableRemoteContentProbe = true;
                            EnableImageCapture.AllowImageCaptureInstance(item);
                        }
                    }
                }
                
                if (CurrentRefreshContext.Value.IsNewItem)
                {
                    return true;
                }

                if (item.HasImage(ImageType.Primary) && (provider is IDynamicImageProvider &&
                        provider.GetType().Name == "VideoImageProvider" || provider is IRemoteImageProvider) &&
                    (IsExclusiveFeatureSelected(ExclusiveControl.CatchAllBlock) ||
                     !IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) &&
                     !options.ReplaceAllImages))
                {
                    __result = false;
                    return false;
                }
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool CanRefreshMetadataPrefix(IMetadataProvider provider, BaseItem item,
            LibraryOptions libraryOptions, bool includeDisabled, bool forceEnableInternetMetadata,
            bool ignoreMetadataLock, ref bool __result, out bool __state)
        {
            __state = false;

            if ((item.Parent is null && item.ExtraType is null) || !(provider is IPreRefreshProvider) ||
                !(provider is ICustomMetadataProvider<Video>))
            {
                return true;
            }

            if (ExclusiveItem.Value != 0 && ExclusiveItem.Value == item.InternalId)
            {
                return true;
            }
            
            if (CurrentRefreshContext.Value != null && CurrentRefreshContext.Value.InternalId == item.InternalId)
            {
                __state = true;

                if (CurrentRefreshContext.Value.IsNewItem)
                {
                    __result = false;
                    return false;
                }

                var refreshOptions = CurrentRefreshContext.Value.MetadataRefreshOptions;

                if (CurrentRefreshContext.Value.IsFileChanged)
                {
                    return true;
                }

                if (refreshOptions.MetadataRefreshMode <= MetadataRefreshMode.Default &&
                    refreshOptions.ImageRefreshMode <= MetadataRefreshMode.Default ||
                    !IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) && refreshOptions.SearchResult != null)
                {
                    __result = false;
                    return false;
                }

                if (!IsExclusiveFeatureSelected(ExclusiveControl.CatchAllBlock) && !item.IsShortcut &&
                    refreshOptions.ReplaceAllImages)
                {
                    return true;
                }

                if (!IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) && Plugin.LibraryApi.HasMediaInfo(item))
                {
                    __result = false;
                    return false;
                }

                if (IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) ||
                    !IsExclusiveFeatureSelected(ExclusiveControl.CatchAllBlock) && !item.IsShortcut)
                {
                    return true;
                }

                if (IsExclusiveFeatureSelected(ExclusiveControl.CatchAllBlock) &&
                    !(refreshOptions.MetadataRefreshMode == MetadataRefreshMode.FullRefresh &&
                      refreshOptions.ImageRefreshMode == MetadataRefreshMode.Default &&
                      !refreshOptions.ReplaceAllMetadata && !refreshOptions.ReplaceAllImages))
                {
                    return false;
                }

                return true;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void CanRefreshMetadataPostfix(IMetadataProvider provider, BaseItem item,
            LibraryOptions libraryOptions, bool includeDisabled, bool forceEnableInternetMetadata,
            bool ignoreMetadataLock, ref bool __result, bool __state)
        {
            if (!__state) return;

            var isPersistInScope = !IsExclusiveFeatureSelected(ExclusiveControl.NoPersistIntegration) &&
                                   Plugin.Instance.MediaInfoExtractStore.GetOptions().PersistMediaInfo &&
                                   (item is Video || item is Audio) && Plugin.LibraryApi.IsLibraryInScope(item);
            CurrentRefreshContext.Value.IsPersistInScope = isPersistInScope;

            if (!__result)
            {
                var refreshOptions = CurrentRefreshContext.Value.MetadataRefreshOptions;
                refreshOptions.ForceSave = true;

                if (CurrentRefreshContext.Value.IsExternalSubtitleChanged)
                {
                    _ = Plugin.SubtitleApi.UpdateExternalSubtitles(item, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                if (!IsExclusiveFeatureSelected(ExclusiveControl.NoIntroProtect) &&
                    !CurrentRefreshContext.Value.IsFileChanged && item is Episode &&
                    Plugin.ChapterApi.HasIntro(item))
                {
                    ProtectIntroItem.Value = item.InternalId;
                }

                if (isPersistInScope)
                {
                    ChapterChangeTracker.BypassInstance(item);
                    CurrentRefreshContext.Value.MediaInfoUpdated = true;
                }
            }
        }

        [HarmonyPrefix]
        private static bool AfterMetadataRefreshPrefix(BaseItem __instance)
        {
            if (CurrentRefreshContext.Value != null &&
                CurrentRefreshContext.Value.InternalId == __instance.InternalId &&
                CurrentRefreshContext.Value.IsPersistInScope)
            {
                var refreshOptions = CurrentRefreshContext.Value.MetadataRefreshOptions;
                var directoryService = refreshOptions.DirectoryService;

                if (CurrentRefreshContext.Value.MediaInfoUpdated)
                {
                    if (__instance.IsShortcut && !refreshOptions.EnableRemoteContentProbe)
                    {
                        if (CurrentRefreshContext.Value.IsFileChanged)
                        {
                            _ = Plugin.LibraryApi.DeleteMediaInfoJson(__instance, directoryService,
                                "Exclusive Delete on Change", CancellationToken.None);
                        }
                        else
                        {
                            _ = Plugin.LibraryApi.DeserializeMediaInfo(__instance, directoryService,
                                "Exclusive Restore", CancellationToken.None);
                        }
                    }
                    else
                    {
                        _ = Plugin.LibraryApi.SerializeMediaInfo(__instance, directoryService, true,
                            "Exclusive Overwrite", CancellationToken.None);
                    }
                }
                else if (!CurrentRefreshContext.Value.IsNewItem)
                {
                    if (!Plugin.LibraryApi.HasMediaInfo(__instance))
                    {
                        _ = Plugin.LibraryApi.DeserializeMediaInfo(__instance, directoryService, "Exclusive Restore",
                            CancellationToken.None);
                    }
                    else
                    {
                        _ = Plugin.LibraryApi.SerializeMediaInfo(__instance, directoryService, false,
                            "Exclusive Non-existent", CancellationToken.None);
                    }
                }
            }

            CurrentRefreshContext.Value = null;

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
