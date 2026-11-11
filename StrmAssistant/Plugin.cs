using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.IntroSkip;
using StrmAssistant.Mod;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using StrmAssistant.ScheduledTask;
using StrmAssistant.Web.Helper;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using static StrmAssistant.Options.GeneralOptions;
using static StrmAssistant.Options.IntroSkipOptions;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.MetadataEnhanceOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant
{
    public class Plugin: BasePluginSimpleUI<PluginOptions>, IHasThumbImage
    {
        public static Plugin Instance { get; private set; }
        public static LibraryApi LibraryApi { get; private set; }
        public static MediaInfoApi MediaInfoApi { get; private set; }
        public static ChapterApi ChapterApi { get; private set; }
        public static FingerprintApi FingerprintApi { get; private set; }
        public static FingerprintLibraryApi FingerprintLibraryApi { get; private set; }
        public static NotificationApi NotificationApi { get; private set; }
        public static SubtitleApi SubtitleApi { get; private set; }
        public static MovieDbApi MovieDbApi { get; private set; }
        public static PlaySessionMonitor PlaySessionMonitor { get; private set; }
        public static MetadataApi MetadataApi { get; private set; }
        public static VideoThumbnailApi VideoThumbnailApi { get; private set; }
        public static VersionPlaybackApi VersionPlaybackApi { get; private set; }
        public static VersionFavoriteApi VersionFavoriteApi { get; private set; }
        public static BoxSetHomeVisibilityStore BoxSetHomeVisibilityStore { get; private set; }
        public static DoubanApi DoubanApi { get; private set; }
        public static DoubanCacheApi DoubanCacheApi { get; private set; }
        public static IntroDbApi IntroDbApi { get; private set; }
        public static DiscMediaInfoApi DiscMediaInfoApi { get; private set; }
        public static DistributedExtractApi DistributedExtractApi { get; private set; }
        public static SearchIndexService SearchIndexService { get; private set; }

        private readonly Guid _id = new Guid("63c322b7-a371-41a3-b11f-04f8418b37d8");

        public readonly ILogger Logger;
        public readonly IApplicationHost ApplicationHost;
        public readonly IApplicationPaths ApplicationPaths;

        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ITaskManager _taskManager;
        private readonly IFfmpegManager _ffmpegManager;
        private readonly ISessionManager _sessionManager;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IHttpClient _httpClient;
        private readonly ILocalizationManager _localizationManager;

        internal IFfmpegManager FfmpegManager => _ffmpegManager;
        internal IFileSystem FileSystem => _fileSystem;
        internal IJsonSerializer JsonSerializer => _jsonSerializer;
        internal IHttpClient HttpClient => _httpClient;
        internal ILocalizationManager LocalizationManager => _localizationManager;

        private bool _currentSuppressOnOptionsSaved;
        private bool _skipOptionsSanitizeOnce;
        private int _currentMaxConcurrentCount;
        private int _currentTier2ConcurrentCount;
        private int _currentIntroDetectionMaxConcurrentCount;
        private bool _currentPersistMediaInfo;
        private bool _currentMediaInfoRestoreMode;
        private bool _currentExclusiveExtract;
        private bool _currentCatchupMode;
        private bool _currentAutoExtractOnItemAdded;
        private bool _currentEnableFavoriteCatchupPriority;
        private bool _currentEnableIntroSkip;
        private bool _currentUnlockIntroSkip;
        private bool _currentAutoMarkPreviousEpisodesPlayed;
        private bool _currentBeautifyMissingMetadata;
        private bool _currentEnhanceMissingEpisodes;
        private bool _currentNoBoxsetsAutoCreation;
        private bool _currentMergeMultiVersion;
        private bool _currentDeepDelete;
        private bool _currentNewLibraryMovieDb;
        private bool _currentPreferTraditionalChinese;
        private bool _currentEnableAltMovieDbUrl;
        private bool _currentEnableLocalMovieDb;
        private bool _currentEnableMovieDbCache;
        private bool _currentEnableLocalMetadataSource;
        private bool _currentDistinguishChinesePosters;
        private bool _currentPreferOriginalPoster;
        private string _currentLocalDiscoveryAddress;
        private bool _currentCrossLibraryFavoriteSeparation;
        private ChineseMetadataOrderOption _currentChineseMetadataOrder;
        private bool _currentConvertSimplifiedToTraditional;

        private static readonly HashSet<string> ExcludedCollectionTypes = new HashSet<string>
        {
            CollectionType.Books.ToString(),
            CollectionType.Photos.ToString(),
            CollectionType.Games.ToString(),
            CollectionType.LiveTv.ToString(),
            CollectionType.Playlists.ToString(),
            CollectionType.BoxSets.ToString()
        };

        internal static string _satelliteRoot;

        /// <summary>
        /// 依赖程序集放这个后缀的子目录（如 StrmAssistantLite.deps），不要放插件目录顶层。
        /// Emby 启动时会把 plugins 顶层的每个 *.dll 按字节加载并 GetTypes（见
        /// ApplicationHost.DiscoverTypes/LoadAssembly）；按字节加载的程序集不会按名字绑定它的
        /// 依赖，扫描 0Harmony/MonoMod/Cecil 时就会抛 "Could not load MonoMod.Utils / Mono.Cecil"
        /// 和 ReflectionTypeLoadException。子目录不在扫描范围内，改为由本回调在插件初始化时按需加载。
        /// </summary>
        private const string DependencyFolderSuffix = ".deps";

        static Plugin()
        {
            // 卫星程序集（zh/zh-hant）跟随插件放在 <plugins>/<culture>/ 子目录。
            // Emby 用 Assembly.Load(bytes) 加载插件主程序集（无位置），卫星查找只能靠
            // AppDomain.AssemblyResolve 兜底（Resource.Embedder 的做法）。
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    Plugin.Instance?.Logger.Debug("[AssemblyResolve] {0} ui={1}", args.Name,
                        CultureInfo.CurrentUICulture?.Name);
                    var an = new AssemblyName(args.Name);

                    // 已经加载过（例如 Emby 自己按插件目录加载了 0Harmony）就绝不重复加载：
                    // 同一程序集两份会带来类型标识冲突（Harmony/MonoMod 的泛型约束异常就是它）
                    foreach (var loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (string.Equals(loadedAssembly.GetName().Name, an.Name,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return loadedAssembly;
                        }
                    }

                    if (string.IsNullOrEmpty(_satelliteRoot))
                    {
                        return null;
                    }

                    string file;
                    if (an.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                    {
                        var culture = an.CultureName;
                        if (string.IsNullOrEmpty(culture))
                        {
                            return null;
                        }

                        file = Path.Combine(_satelliteRoot, culture, an.Name + ".dll");
                    }
                    else
                    {
                        // 插件主程序集是按字节加载的，默认探测不会看插件目录；依赖（如
                        // 0Harmony / MonoMod.* / Mono.Cecil / ToolGood.Words.Pinyin）优先从
                        // <plugins>/<AssemblyName>.deps/ 加载，找不到再回退旧的顶层布局。
                        var depsRoot = Path.Combine(_satelliteRoot,
                            typeof(Plugin).Assembly.GetName().Name + DependencyFolderSuffix);
                        file = Path.Combine(depsRoot, an.Name + ".dll");

                        if (!File.Exists(file))
                        {
                            file = Path.Combine(_satelliteRoot, an.Name + ".dll");
                        }
                    }

                    var loaded = File.Exists(file) ? Assembly.Load(File.ReadAllBytes(file)) : null;
                    Plugin.Instance?.Logger.Debug("[LoadDependency] {0} -> {1} loaded={2}", an.FullName, file, loaded != null);
                    return loaded;
                }
                catch
                {
                    return null;
                }
            };
        }

        public Plugin(IApplicationHost applicationHost, IApplicationPaths applicationPaths, ILogManager logManager,
            IFileSystem fileSystem, ILibraryManager libraryManager, ISessionManager sessionManager,
            IItemRepository itemRepository, INotificationManager notificationManager, ILibraryMonitor libraryMonitor,
            IMediaSourceManager mediaSourceManager, IMediaMountManager mediaMountManager,
            IProviderManager providerManager, IMediaProbeManager mediaProbeManager,
            ILocalizationManager localizationManager, IUserManager userManager, IUserDataManager userDataManager,
            IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder, IJsonSerializer jsonSerializer,
            IHttpClient httpClient, IServerApplicationHost serverApplicationHost,
            IServerConfigurationManager configurationManager, ITaskManager taskManager,
            IImageExtractionManager imageExtractionManager, IServerApplicationPaths serverApplicationPaths,
            IImageProcessor imageProcessor) : base(applicationHost)
        {
            Instance = this;
            // 尽早记录插件目录：后续任何服务（如需要 0Harmony 的 UnifiedRatingService）都可能触发依赖解析
            _satelliteRoot = applicationPaths.PluginsPath;
            Logger = logManager.GetLogger(Name);
            Logger.Info("Plugin is getting loaded.");
            ApplicationHost = applicationHost;
            ApplicationPaths = applicationPaths;

            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _taskManager = taskManager;
            _ffmpegManager = ffmpegManager;
            _sessionManager = sessionManager;
            _jsonSerializer = jsonSerializer;
            _httpClient = httpClient;
            _localizationManager = localizationManager;

            _currentMaxConcurrentCount = GetOptions().GeneralOptions.MaxConcurrentCount;
            _currentIntroDetectionMaxConcurrentCount = GetOptions().GeneralOptions.IntroDetectionMaxConcurrentCount;
            _currentPersistMediaInfo = GetOptions().MediaInfoExtractOptions.PersistMediaInfoMode !=
                                       PersistMediaInfoOption.None.ToString();
            _currentMediaInfoRestoreMode = GetOptions().MediaInfoExtractOptions.PersistMediaInfoMode ==
                                           PersistMediaInfoOption.Restore.ToString();
            _currentExclusiveExtract = GetOptions().MediaInfoExtractOptions.ExclusiveExtract;
            _currentCatchupMode = GetOptions().GeneralOptions.CatchupMode;
            _currentAutoExtractOnItemAdded = GetOptions().MediaInfoExtractOptions.AutoExtractOnItemAdded;
            _currentEnableFavoriteCatchupPriority = GetOptions().GeneralOptions.EnableFavoriteCatchupPriority;
            _currentEnableIntroSkip = GetOptions().IntroSkipOptions.EnableIntroSkip;
            _currentUnlockIntroSkip = GetOptions().IntroSkipOptions.UnlockIntroSkip;
            _currentAutoMarkPreviousEpisodesPlayed =
                GetOptions().ExperienceEnhanceOptions.AutoMarkPreviousEpisodesPlayed;
            _currentBeautifyMissingMetadata = GetOptions().ExperienceEnhanceOptions.BeautifyMissingMetadata;
            _currentEnhanceMissingEpisodes = GetOptions().ExperienceEnhanceOptions.EnhanceMissingEpisodes;
            _currentNoBoxsetsAutoCreation = GetOptions().ExperienceEnhanceOptions.NoBoxsetsAutoCreation;
            _currentMergeMultiVersion = GetOptions().ExperienceEnhanceOptions.MergeMultiVersion;
            _currentDeepDelete = GetOptions().GeneralOptions.DeepDelete;
            _currentNewLibraryMovieDb = GetOptions().MetadataEnhanceOptions.NewLibraryMovieDb;
            _currentPreferTraditionalChinese = GetOptions().MetadataEnhanceOptions.PreferTraditionalChinese;
            _currentEnableAltMovieDbUrl = GetOptions().MetadataEnhanceOptions.EnableAltMovieDbUrl;
            _currentEnableLocalMovieDb = GetOptions().MetadataEnhanceOptions.EnableLocalMovieDb;
            _currentEnableMovieDbCache = GetOptions().MetadataEnhanceOptions.EnableMovieDbCache;
            _currentEnableLocalMetadataSource = GetOptions().EnableLocalMetadataSource;
            _currentDistinguishChinesePosters = GetOptions().MetadataEnhanceOptions.DistinguishChinesePosters;
            _currentPreferOriginalPoster = GetOptions().MetadataEnhanceOptions.PreferOriginalPoster;
            _currentLocalDiscoveryAddress = GetOptions().NetworkOptions.LocalDiscoveryAddress;
            _currentCrossLibraryFavoriteSeparation = GetOptions().ExperienceEnhanceOptions.VersionFavoriteOptions
                .EnableCrossLibraryFavoriteSeparation;
            _currentChineseMetadataOrder = GetOptions().MetadataEnhanceOptions.ChineseMetadataOrder;
            _currentConvertSimplifiedToTraditional =
                GetOptions().MetadataEnhanceOptions.ConvertSimplifiedToTraditional;
            InitializeOptionCache();

            if (GetOptions().AboutOptions.DebugMode)
            {
                DebugMode = true;
                GetOptions().AboutOptions.DebugMode = false;
                SavePluginOptionsSuppress();
            }
            else if (Debugger.IsAttached)
            {
                DebugMode = true;
            }

            LibraryApi = new LibraryApi(libraryManager, providerManager, fileSystem, mediaMountManager, userManager);
            MediaInfoApi = new MediaInfoApi(libraryManager, fileSystem, providerManager, mediaSourceManager,
                itemRepository, jsonSerializer, libraryMonitor);
            DiscMediaInfoApi = new DiscMediaInfoApi(mediaProbeManager, libraryManager, itemRepository);
            DistributedExtractApi = new DistributedExtractApi(httpClient, jsonSerializer, mediaProbeManager,
                libraryManager, itemRepository, imageExtractionManager, FfmpegApi.WrapFfmpegManager(ffmpegManager),
                mediaEncoder, applicationPaths, fileSystem, serverApplicationHost);
            ChapterApi = new ChapterApi(libraryManager, itemRepository);
            FingerprintApi = new FingerprintApi(libraryManager, fileSystem, applicationPaths,
                FfmpegApi.WrapFfmpegManager(ffmpegManager), mediaEncoder, mediaMountManager, jsonSerializer,
                itemRepository, serverApplicationHost);
            PlaySessionMonitor = new PlaySessionMonitor(libraryManager, userManager, userDataManager,
                sessionManager);
            NotificationApi = new NotificationApi(notificationManager, userManager, sessionManager, libraryManager);
            SubtitleApi = new SubtitleApi(libraryManager, fileSystem, mediaProbeManager, localizationManager,
                itemRepository);
            LocalEpisodeGroup.Initialize(libraryManager, jsonSerializer);
            RemoteEpisodeGroup.Initialize(libraryManager, jsonSerializer, _httpClient, Logger);
            LocalMetadataSource.Initialize(Logger, _jsonSerializer, ProxyApi.Wrap(_httpClient));
            LocalMetadataTagService.Initialize(libraryManager, jsonSerializer);
            OverrideImageService.Initialize(libraryManager, providerManager, jsonSerializer);
            UnifiedRatingService.Initialize(jsonSerializer);
            UnifiedRatingService.ApplyOptions(GetOptions());
            CustomSpecialsOrderService.Initialize(libraryManager, jsonSerializer);
            MovieDbApi = new MovieDbApi(libraryManager);
            MetadataApi = new MetadataApi(libraryManager, fileSystem, configurationManager, localizationManager,
                jsonSerializer, httpClient);
            VideoThumbnailApi = new VideoThumbnailApi(libraryManager, fileSystem, imageExtractionManager, itemRepository,
                mediaMountManager, serverApplicationPaths, libraryMonitor, ffmpegManager, imageProcessor,
                serverApplicationHost);
            VersionPlaybackApi = new VersionPlaybackApi(sessionManager, jsonSerializer);
            VersionPlaybackApi.Initialize();
            VersionFavoriteApi = new VersionFavoriteApi(jsonSerializer);
            BoxSetHomeVisibilityStore = new BoxSetHomeVisibilityStore(jsonSerializer);
            DoubanApi = new DoubanApi(providerManager, fileSystem, libraryManager, jsonSerializer, httpClient);
            DoubanCacheApi = new DoubanCacheApi(jsonSerializer, httpClient);
            IntroDbApi = new IntroDbApi(libraryManager, itemRepository, jsonSerializer, httpClient);
            FingerprintLibraryApi = new FingerprintLibraryApi(libraryManager, jsonSerializer);
            SearchIndexService = new SearchIndexService(libraryManager, jsonSerializer, httpClient);
            ShortcutMenuHelper.Initialize(configurationManager);

            if (_currentCatchupMode) QueueManager.Initialize();
            if (_currentEnableIntroSkip || _currentAutoMarkPreviousEpisodesPlayed) PlaySessionMonitor.Initialize();
            if (_currentPreferTraditionalChinese || _currentEnableAltMovieDbUrl || _currentEnableLocalMovieDb ||
                _currentEnableMovieDbCache || _currentEnableLocalMetadataSource ||
                _currentDistinguishChinesePosters || _currentPreferOriginalPoster ||
                _currentChineseMetadataOrder != ChineseMetadataOrderOption.LibraryDefault ||
                _currentConvertSimplifiedToTraditional)
                MovieDbApi.Initialize();

            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemRemoved += OnItemRemoved;
            _libraryManager.ItemUpdated += OnItemUpdated;
            _providerManager.RefreshCompleted += OnRefreshCompleted;
            _userManager.UserCreated += OnUserCreated;
            _userManager.UserDeleted += OnUserDeleted;
            Properties.Resources.ResetCachedResources();
            RefreshPageInfoDisplayName();
            System.Threading.Tasks.Task.Run(async () =>
            {
                foreach (var delay in new[] { 5000, 20000 })
                {
                    await System.Threading.Tasks.Task.Delay(delay).ConfigureAwait(false);
                    RefreshPageInfoDisplayName();
                }
            });
            _userManager.UserConfigurationUpdated += OnUserConfigurationUpdated;
            _userDataManager.UserDataSaved += OnUserDataSaved;
            CollectionFolder.LibraryOptionsUpdated += OnLibraryOptionsUpdated;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;

            // Harmony patches must be applied once every plugin service they may call is constructed.
            // Initialize() also decides whether the mod is usable at all (Harmony self-test and the
            // mandatory ffprobe hooks); IsModSupported reflects that result from here on.
            PatchManager.Initialize();

            // Optional patches are only constructed when the option is on, so a default
            // installation performs no extra reflection lookups and installs no detours.
            if (_currentBeautifyMissingMetadata) new BeautifyMissingMetadata();
            if (_currentEnhanceMissingEpisodes) new EnhanceMissingEpisodes();
            if (_currentNoBoxsetsAutoCreation) new NoBoxsetsAutoCreation();

            // Loads the persisted index or builds it in the background after startup has settled.
            SearchIndexService.Initialize();
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            if (e.Item is Video && _currentExclusiveExtract &&
                IsExclusiveFeatureSelected(ExclusiveControl.ExtractAlternative))
            {
                var targetItem = MediaInfoApi.GetItemByMediaSourceId(e.Item, e.MediaSourceId);

                if (targetItem != null)
                {
                    var refreshOptions = MediaInfoApi.GetMediaInfoRefreshOptions();

                    MediaInfoApi.QueueRefreshAlternateVersions(targetItem, refreshOptions, false);
                }
            }
        }

        private void OnRefreshCompleted(object sender, GenericEventArgs<RefreshProgressInfo> e)
        {
            if (e.Argument.Item is Video)
            {
                MediaInfoApi.RemoveBlacklistedSubtitleStreams(e.Argument.Item, "Refresh Completed");
            }

            if (_libraryManager.IsScanRunning) return;

            if (IntroDbApi != null && e.Argument.Item is Movie movie &&
                GetOptions().IntroSkipOptions.IntroDbOptions.EnableMovieIntroDb)
            {
                _ = IntroDbApi.ApplyToMovieAsync(movie, System.Threading.CancellationToken.None);
            }

            if (GetOptions().MetadataEnhanceOptions.EnableDoubanSupplement &&
                (e.Argument.Item is Movie || e.Argument.Item is Series))
            {
                _ = DoubanApi.SupplementAsync(e.Argument.Item, System.Threading.CancellationToken.None);
            }

            if (_currentMergeMultiVersion && e.Argument.Item.IsTopParent)
            {
                var library = e.Argument.CollectionFolders.OfType<CollectionFolder>().FirstOrDefault();

                if (library != null && (library.CollectionType == CollectionType.Movies.ToString() ||
                                        library.CollectionType is null))
                {
                    MergeMultiVersionTask.CurrentScanLibrary.Value = library;

                    var mergeMoviesTask = _taskManager.ScheduledTasks.FirstOrDefault(t =>
                        t.ScheduledTask is MergeMultiVersionTask);

                    if (mergeMoviesTask != null)
                    {
                        _ = _taskManager.Execute(mergeMoviesTask, new TaskOptions());
                    }
                }
            }
        }

        private void OnUserCreated(object sender, GenericEventArgs<User> e)
        {
            LibraryApi.FetchUsers();
            System.Threading.Tasks.Task.Run(() => NotificationApi.ClearUserNotificationSettings(e.Argument));
        }

        private void OnUserDeleted(object sender, GenericEventArgs<User> e)
        {
            LibraryApi.FetchUsers();
        }
        
        private void OnUserConfigurationUpdated(object sender, GenericEventArgs<User> e)
        {
            if (e.Argument.Policy.IsAdministrator) LibraryApi.FetchAdminOrderedViews();
        }

        private void OnLibraryOptionsUpdated(object sender, GenericEventArgs<Tuple<CollectionFolder, LibraryOptions>> e)
        {
            var library = e.Argument.Item1;

            if (!LibraryApi.ExcludedCollectionTypes.Contains(library.CollectionType))
            {
                LibraryApi.UpdateLibraryPathsInScope();

                if (library.CollectionType == CollectionType.TvShows.ToString() || library.CollectionType is null)
                {
                    PlaySessionMonitor.UpdateLibraryPathsInScope();
                    FingerprintApi.UpdateLibraryPathsInScope();

                    if (_currentUnlockIntroSkip)
                        FingerprintApi.UpdateLibraryIntroDetectionFingerprintLength();

                    if (_currentMergeMultiVersion)
                        LibraryApi.EnsureLibraryEnabledAutomaticSeriesGrouping();
                }
            }
        }

        private async void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            SearchIndexService.NotifyItemChanged(e.Item);
            _ = OverrideImageService.ApplyAsync(e.Item);
            LocalMetadataTagService.Apply(e.Item);
            CustomSpecialsOrderService.Apply(e.Item);

            try
            {
                if (GetOptions().NotificationOptions.EnableCollectionNotification)
                {
                    if (e.Item is BoxSet boxSet)
                    {
                        NotificationApi.CollectionCreatedSendNotification(boxSet);
                    }
                    else if (e.Parent is BoxSet collection)
                    {
                        NotificationApi.CollectionItemUpdateSendNotification(collection, e.Item, true);
                    }
                }

                var deserializeResult = false;

                if (_currentNewLibraryMovieDb && e.Item is CollectionFolder newLibrary)
                {
                    LibraryApi.ApplyNewLibraryMovieDbPreference(newLibrary);
                }

                if (_currentPersistMediaInfo && (e.Item is Video || e.Item is Audio))
                {
                    deserializeResult = LibraryApi.HasMediaInfo(e.Item);

                    var directoryService = new DirectoryService(Logger, _fileSystem);

                    if (!deserializeResult)
                    {
                        deserializeResult = await MediaInfoApi.DeserializeMediaInfo(e.Item, directoryService,
                            "OnItemAdded Restore", true).ConfigureAwait(false);
                    }
                    else
                    {
                        _ = MediaInfoApi.SerializeMediaInfo(e.Item.InternalId, directoryService, true,
                            "OnItemAdded Overwrite").ConfigureAwait(false);
                    }
                }

                if (_currentCatchupMode && (e.Item is Video || e.Item is Audio))
                {
                    if (_currentUnlockIntroSkip && IsCatchupTaskSelected(CatchupTask.Fingerprint) &&
                        e.Item is Episode && FingerprintApi.IsLibraryInScope(e.Item) &&
                        (!deserializeResult || FingerprintApi.IsExtractNeeded(e.Item)))
                    {
                        QueueManager.FingerprintItemQueue.Enqueue(e.Item);
                    }
                    else
                    {
                        if (IsCatchupTaskSelected(CatchupTask.MediaInfo) && !deserializeResult)
                        {
                            QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
                        }

                        if (IsCatchupTaskSelected(CatchupTask.IntroSkip) &&
                            e.Item is Episode && PlaySessionMonitor.IsLibraryInScope(e.Item))
                        {
                            if (!deserializeResult)
                            {
                                QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
                            }
                            else if (e.Item is Episode episode && ChapterApi.SeasonHasIntroCredits(episode))
                            {
                                QueueManager.IntroSkipItemQueue.Enqueue(episode);
                            }
                        }
                    }

                    var catchupEpisodeRefresh = e.Item is Episode ep &&
                                                LibraryApi.IsPremiereDateInScope(ep,
                                                    DateTimeOffset.UtcNow.AddDays(-90), false);
                    // Movies join the same catch-up when movie refresh is enabled; the queue
                    // filters them with the episode rules against the date they were added.
                    var catchupMovieRefresh = e.Item is Movie &&
                                              GetOptions().MediaInfoExtractOptions.MovieRefreshOptions
                                                  .EnableMovieRefresh;

                    if (IsCatchupTaskSelected(CatchupTask.EpisodeRefresh) &&
                        (catchupEpisodeRefresh || catchupMovieRefresh))
                    {
                        QueueManager.EpisodeRefreshItemQueue.Enqueue(e.Item);
                    }
                }

                if (e.Item is Movie || e.Item is Series || e.Item is Episode)
                {
                    NotificationApi.FavoritesUpdateSendNotification(e.Item);
                }

                if (e.Item is Episode)
                {
                    NotificationApi.SeriesItemChangedSendNotification(e.Item, true);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex.Message);
                Logger.Debug(ex.StackTrace);
            }

            try
            {
                await AutoExtractOnItemAddedAsync(e.Item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never let the ItemAdded handler throw: library scans would inherit the failure.
                Logger.Warn("AutoExtract - ItemAdded handling failed: " + ex.Message);
                Logger.Debug(ex.StackTrace);
            }
        }

        /// <summary>
        /// Queues a newly added item for media info extraction and, when enabled, primary image
        /// capture. Runs independently of Catch-up Mode; the new option is the switch for this
        /// path. Everything is best effort: failures are logged as warnings and never propagate
        /// into Emby's library scan.
        /// </summary>
        private async System.Threading.Tasks.Task AutoExtractOnItemAddedAsync(BaseItem addedItem)
        {
            try
            {
                if (addedItem is null) return;
                if (!GetOptions().MediaInfoExtractOptions.AutoExtractOnItemAdded) return;
                if (!(addedItem is Movie || addedItem is Episode || addedItem is Video)) return;

                var itemId = addedItem.InternalId;

                // Let the scan finish persisting the item and populating its streams/images.
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

                // Re-read the options after the delay so a toggle made meanwhile is honoured.
                var options = GetOptions().MediaInfoExtractOptions;
                if (!options.AutoExtractOnItemAdded) return;

                // Re-fetch the item so we see the state written by the scan, not the event payload.
                var item = _libraryManager.GetItemById(itemId);
                if (item is null) return;

                #pragma warning disable CS0618 // IsFolder is obsolete but the type check alone is not enough for callers passing a folder-typed instance.
                if (!(item is Movie || item is Episode || item is Video) || item.IsFolder) return;
#pragma warning restore CS0618

                if (!LibraryApi.IsLibraryInScope(item)) return;

                var hasMediaInfo = LibraryApi.HasMediaInfo(item);
                var needsImage = options.EnableImageCapture && !item.HasImage(ImageType.Primary);

                if (hasMediaInfo && !needsImage) return;

                // The container/extension exclusion only applies to image capture. A missing
                // media info is still worth extracting for containers that cannot be captured.
                if (hasMediaInfo && LibraryApi.IsImageCaptureExcluded(item)) return;

                if (MediaInfoBlacklist.FromOptions().IsExcluded(item)) return;

                if (QueueManager.MediaInfoExtractItemQueue.Any(i => i.InternalId == item.InternalId) ||
                    QueueManager.FavoriteMediaInfoExtractItemQueue.Any(i => i.InternalId == item.InternalId))
                {
                    return;
                }

                QueueManager.MediaInfoExtractItemQueue.Enqueue(item);
                QueueManager.EnsureMediaInfoProcessTaskStarted();

                Logger.Info("AutoExtract - Queued: " + item.Name + " - " + item.Path);
            }
            catch (Exception ex)
            {
                Logger.Warn("AutoExtract - ItemAdded handling failed: " + addedItem?.Name + " - " +
                            addedItem?.Path + " - " + ex.Message);
                Logger.Debug(ex.StackTrace);
            }
        }

        private void OnItemRemoved(object sender, ItemChangeEventArgs e)
        {
            SearchIndexService.NotifyItemRemoved(e.Item.InternalId);

            try
            {
                if (GetOptions().NotificationOptions.EnableCollectionNotification)
                {
                    if (e.Item is BoxSet boxSet)
                    {
                        NotificationApi.CollectionDeletedSendNotification(boxSet);
                    }
                    else if (e.Parent is BoxSet collection)
                    {
                        NotificationApi.CollectionItemUpdateSendNotification(collection, e.Item, false);
                    }
                }

                if (e.Item is Episode)
                {
                    NotificationApi.SeriesItemChangedSendNotification(e.Item, false);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex.Message);
                Logger.Debug(ex.StackTrace);
            }

            if (!_currentMediaInfoRestoreMode && e.Item is Video)
            {
                var directoryService = new DirectoryService(Logger, _fileSystem);
                MediaInfoApi.DeleteMediaInfoJson(e.Item, directoryService, "Item Removed Event");
            }
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            SearchIndexService.NotifyItemChanged(e.Item);
            _ = OverrideImageService.ApplyAsync(e.Item);
            LocalMetadataTagService.Apply(e.Item);
            CustomSpecialsOrderService.Apply(e.Item);

            try
            {
                var options = GetOptions().NotificationOptions;
                var updateReason = e.UpdateReason;

                var metadataUpdated =
                    (updateReason & (ItemUpdateType.MetadataImport | ItemUpdateType.MetadataDownload |
                                     ItemUpdateType.MetadataEdit)) != 0;

                if (e.Item is BoxSet boxSet)
                {
                    if (options.EnableCollectionNotification && metadataUpdated)
                    {
                        NotificationApi.CollectionUpdatedSendNotification(boxSet);
                    }

                    return;
                }

                if (!options.EnableMetadataNotification && !options.EnableImageNotification) return;

                if (!(e.Item is Video || e.Item is Audio || e.Item is Series || e.Item is Season ||
                      e.Item is MusicAlbum)) return;

                if (options.EnableMetadataNotification && metadataUpdated)
                {
                    NotificationApi.MetadataUpdateSendNotification(e.Item);
                }

                if (options.EnableImageNotification && (updateReason & ItemUpdateType.ImageUpdate) != 0)
                {
                    NotificationApi.ImageUpdateSendNotification(e.Item);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex.Message);
                Logger.Debug(ex.StackTrace);
            }
        }

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            if (e.UserData.IsFavorite)
            {
                if (_currentUnlockIntroSkip && _currentCatchupMode && IsCatchupTaskSelected(CatchupTask.Fingerprint) &&
                    FingerprintApi.IsLibraryInScope(e.Item) && (e.Item is Episode || e.Item is Series))
                {
                    if (_currentEnableFavoriteCatchupPriority)
                    {
                        QueueManager.FavoriteFingerprintItemQueue.Enqueue(e.Item);
                    }
                    else
                    {
                        QueueManager.FingerprintItemQueue.Enqueue(e.Item);
                    }
                }
                else if (_currentCatchupMode && IsCatchupTaskSelected(CatchupTask.MediaInfo))
                {
                    if (_currentEnableFavoriteCatchupPriority)
                    {
                        QueueManager.FavoriteMediaInfoExtractItemQueue.Enqueue(e.Item);
                    }
                    else
                    {
                        QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
                    }
                }
            }
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public override string Description =>
            Properties.Resources.ResourceManager.GetString("Plugin_Description", DefaultUICulture) ??
            "Extract MediaInfo and Enable IntroSkip";

        public override Guid Id => _id;

        public sealed override string Name =>
            Properties.Resources.ResourceManager.GetString("Plugin_Name", DefaultUICulture) ?? "Strm Assistant";


        public string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString();

        // HTTP 头只能是 ASCII；Name 已本地化为中文，不能直接拼进 User-Agent
        public string UserAgent => $"StrmAssistantLite/{CurrentVersion}";

        public CultureInfo DefaultUICulture => new CultureInfo("zh-CN");

        public bool DebugMode;

        /// <summary>
        /// True only while the Harmony mod is actually operational. When Harmony cannot install
        /// its hooks (or the mandatory ffprobe patches fail) every exclusive feature must be
        /// presented as unavailable instead of pretending to run.
        /// </summary>
        public bool IsModSupported => PatchManager.IsModOperational;

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Properties.thumb.png");
        }

        public PluginOptions GetPluginOptions()
        {
            return GetOptions();
        }

        public void SavePluginOptionsSuppress()
        {
            _currentSuppressOnOptionsSaved = true;
            SaveOptions(GetOptions());
        }

        /// <summary>
        /// Persists the per-library intro detection fingerprint overrides edited on the plugin's
        /// own settings page. The scope sanitizing in <see cref="OnOptionsSaving"/> relies on
        /// transient library/user lists that only exist after the generic options page has been
        /// shown, so this targeted save skips it; the regular save pipeline (including
        /// <see cref="FingerprintApi.UpdateLibraryIntroDetectionFingerprintLength"/>) still runs,
        /// which makes the new values effective without a restart.
        /// </summary>
        public void SaveLibraryIntroDetectionFingerprintOptions(
            IEnumerable<LibraryIntroDetectionFingerprintOption> libraryOptions)
        {
            var options = GetPluginOptions();
            var collection = new LibraryIntroDetectionFingerprintOptionCollection();

            foreach (var option in libraryOptions)
            {
                collection.Add(new LibraryIntroDetectionFingerprintOption
                {
                    LibraryId = option.LibraryId,
                    LibraryName = option.LibraryName,
                    Override = option.Override,
                    IntroDetectionFingerprintMinutes =
                        Math.Max(2, Math.Min(20, option.IntroDetectionFingerprintMinutes))
                });
            }

            options.IntroSkipOptions.LibraryIntroDetectionFingerprintOptions = collection;

            _skipOptionsSanitizeOnce = true;
            // The verbose option logging in OnOptionsSaved resolves library/user names from lists
            // that are only populated when the generic settings page is shown, so suppress it for
            // this targeted save as well.
            _currentSuppressOnOptionsSaved = true;

            try
            {
                SaveOptions(options);
            }
            finally
            {
                _skipOptionsSanitizeOnce = false;
                _currentSuppressOnOptionsSaved = false;
            }
        }

        protected override bool OnOptionsSaving(PluginOptions options)
        {
            if (_skipOptionsSanitizeOnce)
            {
                return base.OnOptionsSaving(options);
            }

            if (string.IsNullOrEmpty(options.GeneralOptions.CatchupTaskScope))
            {
                options.GeneralOptions.CatchupTaskScope = CatchupTask.MediaInfo.ToString();
            }
            else if (!options.IntroSkipOptions.UnlockIntroSkip)
            {
                var taskScope = options.GeneralOptions.CatchupTaskScope;
                var selectedTasks = taskScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(f => f != CatchupTask.Fingerprint.ToString())
                    .ToList();
                options.GeneralOptions.CatchupTaskScope = string.Join(",", selectedTasks);
            }

            var mergeSeriesPreference = options.ExperienceEnhanceOptions.MergeSeriesPreference;

            if (!Enum.IsDefined(typeof(ExperienceEnhanceOptions.MergeSeriesScopeOption), mergeSeriesPreference))
            {
                options.ExperienceEnhanceOptions.MergeSeriesPreference =
                    ExperienceEnhanceOptions.MergeSeriesScopeOption.LibraryScope;
            }

            options.MediaInfoExtractOptions.LibraryScope = string.Join(",",
                options.MediaInfoExtractOptions.LibraryScope
                    ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(v => options.MediaInfoExtractOptions.LibraryList.Any(option => option.Value == v)) ??
                Enumerable.Empty<string>());

            var exclusiveControlFeatures = options.MediaInfoExtractOptions.ExclusiveControlFeatures;
            if (!string.IsNullOrEmpty(exclusiveControlFeatures))
            {
                var selected = exclusiveControlFeatures
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();

                if (selected.Contains(ExclusiveControl.CatchAllBlock.ToString()))
                {
                    selected.RemoveAll(f => f == ExclusiveControl.CatchAllAllow.ToString() ||
                                            f == ExclusiveControl.ExtractOnFileChange.ToString());
                }

                if (selected.Contains(ExclusiveControl.IgnoreFileChange.ToString()))
                {
                    selected.RemoveAll(f => f == ExclusiveControl.ExtractOnFileChange.ToString());
                }

                options.MediaInfoExtractOptions.ExclusiveControlFeatures = string.Join(",", selected);
            }

            options.IntroSkipOptions.LibraryScope = string.Join(",",
                options.IntroSkipOptions.LibraryScope
                    ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(v => options.IntroSkipOptions.LibraryList.Any(option => option.Value == v)) ??
                Enumerable.Empty<string>());

            var pinyinSortOptions = options.ExperienceEnhanceOptions.PinyinSortOptions;
            var pinyinSortLibraryList = pinyinSortOptions?.LibraryList;

            if (pinyinSortLibraryList != null)
            {
                pinyinSortOptions.LibraryScope = string.Join(",",
                    pinyinSortOptions.LibraryScope
                        ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(v => pinyinSortLibraryList.Any(option => option.Value == v)) ??
                    Enumerable.Empty<string>());
            }

            var episodeGroupOptions = options.MetadataEnhanceOptions;
            var episodeGroupLibraryList = episodeGroupOptions?.LibraryList;

            if (episodeGroupLibraryList != null)
            {
                episodeGroupOptions.EpisodeGroupLibraryScope = string.Join(",",
                    episodeGroupOptions.EpisodeGroupLibraryScope
                        ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(v => episodeGroupLibraryList.Any(option => option.Value == v)) ??
                    Enumerable.Empty<string>());
            }

            var beautifyLibraryList = options.ExperienceEnhanceOptions.BeautifyMissingMetadataLibraryList;

            if (beautifyLibraryList != null)
            {
                options.ExperienceEnhanceOptions.BeautifyMissingMetadataLibraryScope = string.Join(",",
                    options.ExperienceEnhanceOptions.BeautifyMissingMetadataLibraryScope
                        ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(v => beautifyLibraryList.Any(option => option.Value == v)) ??
                    Enumerable.Empty<string>());
            }

            options.IntroSkipOptions.UserScope = string.Join(",",
                options.IntroSkipOptions.UserScope
                    ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(v => options.IntroSkipOptions.UserList.Any(option => option.Value == v)) ??
                Enumerable.Empty<string>());

            options.IntroSkipOptions.MarkerEnabledLibraryScope =
                options.IntroSkipOptions.MarkerEnabledLibraryScope
                    ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Contains("-1") == true
                    ? "-1"
                    : string.Join(",",
                        options.IntroSkipOptions.MarkerEnabledLibraryScope
                            ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(v => options.IntroSkipOptions.MarkerEnabledLibraryList.Any(option =>
                                option.Value == v)) ?? Enumerable.Empty<string>());

            return base.OnOptionsSaving(options);
        }

        protected override void OnOptionsSaved(PluginOptions options)
        {
            var suppress = _currentSuppressOnOptionsSaved;

            var catchupMode = options.GeneralOptions.CatchupMode;
            var autoExtractOnItemAdded = options.MediaInfoExtractOptions.AutoExtractOnItemAdded;
            var catchupModeChanged = _currentCatchupMode != catchupMode;
            var autoExtractChanged = _currentAutoExtractOnItemAdded != autoExtractOnItemAdded;

            _currentCatchupMode = catchupMode;
            _currentAutoExtractOnItemAdded = autoExtractOnItemAdded;

            if (catchupMode)
            {
                if (catchupModeChanged) QueueManager.Initialize();
            }
            else if (autoExtractOnItemAdded)
            {
                // Auto-extract owns the media info worker while Catch-up Mode is off.
                QueueManager.EnsureMediaInfoProcessTaskStarted();
            }
            else if (catchupModeChanged || autoExtractChanged)
            {
                QueueManager.Dispose();
            }

            UpdateCatchupScope();
            _currentEnableFavoriteCatchupPriority = options.GeneralOptions.EnableFavoriteCatchupPriority;

            if (!suppress)
            {
                Logger.Info("CatchupMode is set to {0}", options.GeneralOptions.CatchupMode);
                Logger.Info("EnableFavoriteCatchupPriority is set to {0}",
                    options.GeneralOptions.EnableFavoriteCatchupPriority);
                var catchupTaskScope = GetSelectedCatchupTaskDescription();
                Logger.Info("CatchupTaskScope is set to {0}", string.IsNullOrEmpty(catchupTaskScope) ? "EMPTY" : catchupTaskScope);
            }

            if (!suppress)
            {
                Logger.Info("EnableProxyServer is set to {0}", options.NetworkOptions.EnableProxyServer);
                if (options.NetworkOptions.EnableProxyServer)
                {
                    Logger.Info("ProxyServer is set to {0}", options.NetworkOptions.ProxyServer);
                    Logger.Info("ProxyMode is set to {0}", options.NetworkOptions.ProxyMode);
                    if (options.NetworkOptions.ProxyMode == ProxyModeOption.Whitelist)
                    {
                        Logger.Info("ProxyWhitelist is set to {0}", string.IsNullOrWhiteSpace(options.NetworkOptions.ProxyWhitelist)
                            ? "EMPTY"
                            : options.NetworkOptions.ProxyWhitelist);
                    }
                }

                Logger.Info("LocalDiscoveryAddress is set to {0}",
                    string.IsNullOrWhiteSpace(options.NetworkOptions.LocalDiscoveryAddress)
                        ? "EMPTY"
                        : options.NetworkOptions.LocalDiscoveryAddress);
                if (!string.Equals(_currentLocalDiscoveryAddress, options.NetworkOptions.LocalDiscoveryAddress,
                        StringComparison.Ordinal))
                {
                    _currentLocalDiscoveryAddress = options.NetworkOptions.LocalDiscoveryAddress;
                    Logger.Info("LocalDiscoveryAddress changed. Restart Emby to apply it.");
                }

                var crossLibraryFavoriteSeparation = options.ExperienceEnhanceOptions.VersionFavoriteOptions
                    .EnableCrossLibraryFavoriteSeparation;
                Logger.Info("EnableCrossLibraryFavoriteSeparation is set to {0}", crossLibraryFavoriteSeparation);
                if (_currentCrossLibraryFavoriteSeparation != crossLibraryFavoriteSeparation)
                {
                    _currentCrossLibraryFavoriteSeparation = crossLibraryFavoriteSeparation;
                    Logger.Info("EnableCrossLibraryFavoriteSeparation changed. Restart Emby to apply it.");
                }
            }

            if (!suppress)
            {
                Logger.Info("IncludeExtra is set to {0}", options.MediaInfoExtractOptions.IncludeExtra);
                Logger.Info("CustomFfprobeArguments is set to {0}",
                    string.IsNullOrWhiteSpace(options.MediaInfoExtractOptions.CustomFfprobeArguments)
                        ? "EMPTY"
                        : options.MediaInfoExtractOptions.CustomFfprobeArguments);
                Logger.Info("CustomFfmpegArguments is set to {0}",
                    string.IsNullOrWhiteSpace(options.MediaInfoExtractOptions.CustomFfmpegArguments)
                        ? "EMPTY"
                        : options.MediaInfoExtractOptions.CustomFfmpegArguments);
                Logger.Info("SubtitleBlacklistLanguages is set to {0}",
                    string.IsNullOrWhiteSpace(options.MediaInfoExtractOptions.SubtitleBlacklistLanguages)
                        ? "EMPTY"
                        : options.MediaInfoExtractOptions.SubtitleBlacklistLanguages);
                Logger.Info("SubtitleBlacklistTitles is set to {0}",
                    string.IsNullOrWhiteSpace(options.MediaInfoExtractOptions.SubtitleBlacklistTitles)
                        ? "EMPTY"
                        : options.MediaInfoExtractOptions.SubtitleBlacklistTitles);
                Logger.Info("SubtitleBlacklistCodecs is set to {0}",
                    string.IsNullOrWhiteSpace(options.MediaInfoExtractOptions.SubtitleBlacklistCodecs)
                        ? "EMPTY"
                        : options.MediaInfoExtractOptions.SubtitleBlacklistCodecs);
                Logger.Info("CustomFfmpegPath is set to {0}",
                    string.IsNullOrWhiteSpace(options.MediaInfoExtractOptions.CustomFfmpegPath)
                        ? "EMPTY"
                        : options.MediaInfoExtractOptions.CustomFfmpegPath);
                if (!string.IsNullOrWhiteSpace(options.MediaInfoExtractOptions.CustomFfmpegPath))
                {
                    System.Threading.Tasks.Task.Run(() =>
                        FfmpegApi.CheckCustomFfmpeg(_ffmpegManager, true));
                }
                Logger.Info("CropUltraWideScreenshots is set to {0}",
                    options.MediaInfoExtractOptions.CropUltraWideScreenshots);
                Logger.Info("CropPortraitScreenshots is set to {0}",
                    options.MediaInfoExtractOptions.CropPortraitScreenshots);
                Logger.Info("DetectHdrVivid is set to {0}", options.MediaInfoExtractOptions.DetectHdrVivid);
                Logger.Info("EnableImageCapture is set to {0}",
                    options.MediaInfoExtractOptions.EnableImageCapture);
                Logger.Info("AutoExtractOnItemAdded is set to {0}",
                    options.MediaInfoExtractOptions.AutoExtractOnItemAdded);
                Logger.Info("EnableDvP7LayerBadge is set to {0}",
                    options.MediaInfoExtractOptions.EnableDvP7LayerBadge);
                Logger.Info("ScreenshotQuality is set to {0}", options.MediaInfoExtractOptions.ScreenshotQuality);
                Logger.Info("ScreenshotMaxWidth is set to {0}", options.MediaInfoExtractOptions.ScreenshotMaxWidth);
                Logger.Info("ScreenshotMaxHeight is set to {0}", options.MediaInfoExtractOptions.ScreenshotMaxHeight);
                Logger.Info("MaxConcurrentCount is set to {0}", options.GeneralOptions.MaxConcurrentCount);
                Logger.Info("IntroDetectionMaxConcurrentCount is set to {0}",
                    options.GeneralOptions.IntroDetectionMaxConcurrentCount);
                Logger.Info("CooldownDurationSeconds is set to {0}", options.GeneralOptions.CooldownDurationSeconds);
                Logger.Info("Tier2 MaxConcurrentCount is set to {0}", options.GeneralOptions.Tier2MaxConcurrentCount);

                var libraryScope = string.Join(", ",
                    options.MediaInfoExtractOptions.LibraryScope
                        ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v =>
                            options.MediaInfoExtractOptions.LibraryList.FirstOrDefault(option => option.Value == v)
                                ?.Name) ?? Enumerable.Empty<string>());
                Logger.Info("MediaInfoExtract - LibraryScope is set to {0}",
                    string.IsNullOrEmpty(libraryScope) ? "ALL" : libraryScope);
            }

            if (!suppress)
            {
                Logger.Info("EnableCollectionNotification is set to {0}",
                    options.NotificationOptions.EnableCollectionNotification);
                Logger.Info("EnableMetadataNotification is set to {0}",
                    options.NotificationOptions.EnableMetadataNotification);
                Logger.Info("EnableImageNotification is set to {0}",
                    options.NotificationOptions.EnableImageNotification);
            }
            if (_currentMaxConcurrentCount != options.GeneralOptions.MaxConcurrentCount)
            {
                _currentMaxConcurrentCount = options.GeneralOptions.MaxConcurrentCount;

                QueueManager.UpdateMasterSemaphore(_currentMaxConcurrentCount);
            }
            if (_currentIntroDetectionMaxConcurrentCount != options.GeneralOptions.IntroDetectionMaxConcurrentCount)
            {
                _currentIntroDetectionMaxConcurrentCount = options.GeneralOptions.IntroDetectionMaxConcurrentCount;

                QueueManager.UpdateFingerprintSemaphore(_currentIntroDetectionMaxConcurrentCount);

                FingerprintApi.PatchTimeout(_currentIntroDetectionMaxConcurrentCount);
            }
            if (_currentTier2ConcurrentCount != options.GeneralOptions.Tier2MaxConcurrentCount)
            {
                _currentTier2ConcurrentCount = options.GeneralOptions.Tier2MaxConcurrentCount;

                QueueManager.UpdateTier2Semaphore(_currentTier2ConcurrentCount);
            }
            LibraryApi.UpdateLibraryPathsInScope();

            if (!suppress)
            {
                Logger.Info("PersistMediaInfoMode is set to {0}", options.MediaInfoExtractOptions.PersistMediaInfoMode);
                Logger.Info("MediaInfoJsonRootFolder is set to {0}",
                    !string.IsNullOrEmpty(options.MediaInfoExtractOptions.MediaInfoJsonRootFolder)
                        ? options.MediaInfoExtractOptions.MediaInfoJsonRootFolder
                        : "EMPTY");
            }

            _currentPersistMediaInfo = options.MediaInfoExtractOptions.PersistMediaInfoMode !=
                                       PersistMediaInfoOption.None.ToString();
            _currentMediaInfoRestoreMode = options.MediaInfoExtractOptions.PersistMediaInfoMode ==
                                           PersistMediaInfoOption.Restore.ToString();

            if (!suppress)
            {
                Logger.Info("EnableIntroSkip is set to {0}", options.IntroSkipOptions.EnableIntroSkip);
                Logger.Info("MaxIntroDurationSeconds is set to {0}", options.IntroSkipOptions.MaxIntroDurationSeconds);
                Logger.Info("MaxCreditsDurationSeconds is set to {0}", options.IntroSkipOptions.MaxCreditsDurationSeconds);
                Logger.Info("MinOpeningPlotDurationSeconds is set to {0}", options.IntroSkipOptions.MinOpeningPlotDurationSeconds);
                Logger.Info("IntroMarkerMinDurationSeconds is set to {0}",
                    options.IntroSkipOptions.IntroMarkerMinDurationSeconds);
                Logger.Info("IntroMarkerMaxDurationSeconds is set to {0}",
                    options.IntroSkipOptions.IntroMarkerMaxDurationSeconds);
                Logger.Info("IntroMarkerPaddingSeconds is set to {0}",
                    options.IntroSkipOptions.IntroMarkerPaddingSeconds);
            }
            var enableIntroSkip = options.IntroSkipOptions.EnableIntroSkip;
            var autoMarkPreviousEpisodesPlayed = options.ExperienceEnhanceOptions.AutoMarkPreviousEpisodesPlayed;

            if (_currentEnableIntroSkip != enableIntroSkip ||
                _currentAutoMarkPreviousEpisodesPlayed != autoMarkPreviousEpisodesPlayed)
            {
                _currentEnableIntroSkip = enableIntroSkip;
                _currentAutoMarkPreviousEpisodesPlayed = autoMarkPreviousEpisodesPlayed;

                if (enableIntroSkip || autoMarkPreviousEpisodesPlayed)
                {
                    PlaySessionMonitor.Initialize();
                }
                else
                {
                    PlaySessionMonitor.Dispose();
                }
            }

            UpdateIntroSkipPreferences();

            if (!suppress)
            {
                var intoSkipLibraryScope = string.Join(", ",
                    options.IntroSkipOptions.LibraryScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => options.IntroSkipOptions.LibraryList
                            .FirstOrDefault(option => option.Value == v)
                            ?.Name) ?? Enumerable.Empty<string>());
                Logger.Info("IntroSkip - LibraryScope is set to {0}",
                        string.IsNullOrEmpty(intoSkipLibraryScope) ? "ALL" : intoSkipLibraryScope);

                var introSkipUserScope = string.Join(", ",
                    options.IntroSkipOptions.UserScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => options.IntroSkipOptions.UserList
                            .FirstOrDefault(option => option.Value == v)
                            ?.Name) ?? Enumerable.Empty<string>());
                Logger.Info("IntroSkip - UserScope is set to {0}",
                        string.IsNullOrEmpty(introSkipUserScope) ? "ALL" : introSkipUserScope);

                Logger.Info("IntroSkip - ClientScope is set to {0}", options.IntroSkipOptions.ClientScope);
                var introSkipPreferences = GetSelectedIntroSkipPreferenceDescription();

                Logger.Info("IntroSkipPreferences is set to {0}",
                    string.IsNullOrEmpty(introSkipPreferences) ? "EMPTY" : introSkipPreferences);
            }
            PlaySessionMonitor.UpdateLibraryPathsInScope();
            PlaySessionMonitor.UpdateUsersInScope();
            PlaySessionMonitor.UpdateClientInScope();
            
            if (!suppress)
            {
                Logger.Info("UnlockIntroSkip is set to {0}", options.IntroSkipOptions.UnlockIntroSkip);
                var markerEnabledLibraryScope = string.Join(", ",
                    options.IntroSkipOptions.MarkerEnabledLibraryScope
                        ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(v =>
                            options.IntroSkipOptions.MarkerEnabledLibraryList
                                .FirstOrDefault(option => option.Value == v)?.Name) ?? Enumerable.Empty<string>());
                Logger.Info("MarkerEnabledLibraryScope is set to {0}",
                    string.IsNullOrEmpty(markerEnabledLibraryScope)
                        ? options.IntroSkipOptions.MarkerEnabledLibraryList.Any(o => o.Value != "-1") ? "ALL" : "EMPTY"
                        : markerEnabledLibraryScope);
                Logger.Info("IntroDetectionFingerprintMinutes is set to {0}",
                    options.IntroSkipOptions.IntroDetectionFingerprintMinutes);
                var fingerprintOverrides = options.IntroSkipOptions.LibraryIntroDetectionFingerprintOptions?
                    .Overrides().ToList();
                Logger.Info("LibraryIntroDetectionFingerprintOverrides is set to {0}",
                    fingerprintOverrides?.Count > 0
                        ? string.Join(", ", fingerprintOverrides.Select(
                            o => (string.IsNullOrEmpty(o.LibraryName) ? o.LibraryId : o.LibraryName) + "=" +
                                 o.IntroDetectionFingerprintMinutes))
                        : "NONE");
            }
            _currentUnlockIntroSkip = options.IntroSkipOptions.UnlockIntroSkip;
            FingerprintApi.UpdateLibraryPathsInScope();
            FingerprintApi.UpdateLibraryIntroDetectionFingerprintLength();

            if (!suppress)
            {
                Logger.Info("MergeMultiVersion is set to {0}", options.ExperienceEnhanceOptions.MergeMultiVersion);
                Logger.Info("MergeMoviesPreference is set to {0}",
                    EnumExtensions.GetDescription(options.ExperienceEnhanceOptions.MergeMoviesPreference));
                Logger.Info("MergeSeriesPreference is set to {0}",
                    EnumExtensions.GetDescription(options.ExperienceEnhanceOptions.MergeSeriesPreference));
                Logger.Info("MaxMultiVersionMergeCount is set to {0}",
                    options.GeneralOptions.MaxMultiVersionMergeCount);
                Logger.Info("AutoMarkPreviousEpisodesPlayed is set to {0}",
                    options.ExperienceEnhanceOptions.AutoMarkPreviousEpisodesPlayed);
                Logger.Info("EnhanceNotification is set to {0}",
                    options.ExperienceEnhanceOptions.EnhanceNotification);
                Logger.Info("NaturalSort is set to {0}", options.ExperienceEnhanceOptions.NaturalSort);
                Logger.Info("EnforceLibraryOrder is set to {0}",
                    options.ExperienceEnhanceOptions.EnforceLibraryOrder);
                Logger.Info("LogNewestFirst is set to {0}",
                    options.ExperienceEnhanceOptions.LogNewestFirst);
                Logger.Info("MultiSelectActions is set to {0}",
                    options.ExperienceEnhanceOptions.MultiSelectActions);
                Logger.Info("HidePersonNoImage is set to {0}",
                    options.ExperienceEnhanceOptions.HidePersonNoImage);
                Logger.Info("HideSearchSuggestions is set to {0}",
                    options.SearchOptions.HideSearchSuggestions);
                Logger.Info("SuppressPluginUpdates is set to {0}",
                    options.ExperienceEnhanceOptions.SuppressPluginUpdates);
                Logger.Info("DeepDelete is set to {0}", options.GeneralOptions.DeepDelete);
                Logger.Info("BeautifyMissingMetadata is set to {0}",
                    options.ExperienceEnhanceOptions.BeautifyMissingMetadata);
                Logger.Info("BeautifyMissingMetadataLibraryScope is set to {0}",
                    string.IsNullOrEmpty(options.ExperienceEnhanceOptions.BeautifyMissingMetadataLibraryScope)
                        ? "ALL"
                        : options.ExperienceEnhanceOptions.BeautifyMissingMetadataLibraryScope);
                Logger.Info("EnhanceMissingEpisodes is set to {0}",
                    options.ExperienceEnhanceOptions.EnhanceMissingEpisodes);
                Logger.Info("NoBoxsetsAutoCreation is set to {0}",
                    options.ExperienceEnhanceOptions.NoBoxsetsAutoCreation);
            }
            if (_currentMergeMultiVersion != options.ExperienceEnhanceOptions.MergeMultiVersion)
            {
                _currentMergeMultiVersion = options.ExperienceEnhanceOptions.MergeMultiVersion;

                if (_currentMergeMultiVersion)
                {
                    PatchManager.UnlockMultiVersionLimit?.Patch();
                }
                else
                {
                    PatchManager.UnlockMultiVersionLimit?.Unpatch();
                }
            }

            if (!suppress)
            {
                Logger.Info("NewLibraryMovieDb is set to {0}", options.MetadataEnhanceOptions.NewLibraryMovieDb);
                Logger.Info("ChineseMetadataOrder is set to {0}",
                    options.MetadataEnhanceOptions.ChineseMetadataOrder);
                Logger.Info("ConvertSimplifiedToTraditional is set to {0}",
                    options.MetadataEnhanceOptions.ConvertSimplifiedToTraditional);
                Logger.Info("PreferTraditionalChinese is set to {0}",
                    options.MetadataEnhanceOptions.PreferTraditionalChinese);
                Logger.Info("DistinguishChinesePosters is set to {0}",
                    options.MetadataEnhanceOptions.DistinguishChinesePosters);
                Logger.Info("PreferOriginalPoster is set to {0}",
                    options.MetadataEnhanceOptions.PreferOriginalPoster);
            }
            _currentNewLibraryMovieDb = options.MetadataEnhanceOptions.NewLibraryMovieDb;
            if (!suppress)
            {
                Logger.Info("EnableDoubanSupplement is set to {0}",
                    options.MetadataEnhanceOptions.EnableDoubanSupplement);
                Logger.Info("EnableDoubanRecommendations is set to {0}",
                    options.MetadataEnhanceOptions.EnableDoubanRecommendations);
                Logger.Info("DoubanRequestIntervalSeconds is set to {0}",
                    options.MetadataEnhanceOptions.DoubanRequestIntervalSeconds);
                Logger.Info("EnableDoubanCast is set to {0}", options.MetadataEnhanceOptions.EnableDoubanCast);
                Logger.Info("EnableDoubanSeason is set to {0}", options.MetadataEnhanceOptions.EnableDoubanSeason);
                Logger.Info("EnableLocalMovieDb is set to {0}", options.MetadataEnhanceOptions.EnableLocalMovieDb);
                Logger.Info("LocalMovieDbUrl is set to {0}",
                    string.IsNullOrWhiteSpace(options.MetadataEnhanceOptions.LocalMovieDbUrl)
                        ? "EMPTY"
                        : options.MetadataEnhanceOptions.LocalMovieDbUrl);
                Logger.Info("EnableAltMovieDbUrl is set to {0}", options.MetadataEnhanceOptions.EnableAltMovieDbUrl);
                Logger.Info("AltMovieDbApiUrl is set to {0}",
                    string.IsNullOrWhiteSpace(options.MetadataEnhanceOptions.AltMovieDbApiUrl)
                        ? "EMPTY"
                        : options.MetadataEnhanceOptions.AltMovieDbApiUrl);
                Logger.Info("AltMovieDbImageUrl is set to {0}",
                    string.IsNullOrWhiteSpace(options.MetadataEnhanceOptions.AltMovieDbImageUrl)
                        ? "EMPTY"
                        : options.MetadataEnhanceOptions.AltMovieDbImageUrl);
                Logger.Info("EnableMovieDbCache is set to {0}",
                    options.MetadataEnhanceOptions.EnableMovieDbCache);
                Logger.Info("MovieDbCacheTtlDays is set to {0}",
                    options.MetadataEnhanceOptions.MovieDbCacheTtlDays);
                Logger.Info("MovieDbCacheMaxSizeMb is set to {0}",
                    options.MetadataEnhanceOptions.MovieDbCacheMaxSizeMb);
                Logger.Info("MovieDbEpisodeGroup is set to {0}",
                    options.MetadataEnhanceOptions.MovieDbEpisodeGroup);
                Logger.Info("EpisodeGroupLibraryScope is set to {0}",
                    string.IsNullOrWhiteSpace(options.MetadataEnhanceOptions.EpisodeGroupLibraryScope)
                        ? "ALL"
                        : options.MetadataEnhanceOptions.EpisodeGroupLibraryScope);
                Logger.Info("EnableLocalMetadataSource is set to {0}",
                    options.EnableLocalMetadataSource);
                Logger.Info("LocalMetadataRoot is set to {0}",
                    string.IsNullOrWhiteSpace(options.LocalMetadataRoot) ? "EMPTY" : options.LocalMetadataRoot);
                Logger.Info("LocalMetadataMode is set to {0}", options.LocalMetadataMode);
                Logger.Info("LocalMetadataAutoRefreshImages is set to {0}",
                    options.LocalMetadataAutoRefreshImages);
                Logger.Info("EnableLocalEpisodeGroup is set to {0}", options.EnableLocalEpisodeGroup);
                Logger.Info("UnifiedRatingMode is set to {0}", options.UnifiedRatingMode);
                Logger.Info("UnifiedRatingTarget is set to {0}",
                    string.IsNullOrWhiteSpace(options.UnifiedRatingTarget) ? "EMPTY" : options.UnifiedRatingTarget);
                Logger.Info("EnableCustomSpecialsOrder is set to {0}", options.EnableCustomSpecialsOrder);
            }
            if (_currentPreferTraditionalChinese != options.MetadataEnhanceOptions.PreferTraditionalChinese ||
                _currentEnableAltMovieDbUrl != options.MetadataEnhanceOptions.EnableAltMovieDbUrl ||
                _currentEnableLocalMovieDb != options.MetadataEnhanceOptions.EnableLocalMovieDb ||
                _currentEnableMovieDbCache != options.MetadataEnhanceOptions.EnableMovieDbCache ||
                _currentEnableLocalMetadataSource != options.EnableLocalMetadataSource ||
                _currentDistinguishChinesePosters != options.MetadataEnhanceOptions.DistinguishChinesePosters ||
                _currentPreferOriginalPoster != options.MetadataEnhanceOptions.PreferOriginalPoster ||
                _currentChineseMetadataOrder != options.MetadataEnhanceOptions.ChineseMetadataOrder ||
                _currentConvertSimplifiedToTraditional !=
                options.MetadataEnhanceOptions.ConvertSimplifiedToTraditional)
            {
                _currentPreferTraditionalChinese = options.MetadataEnhanceOptions.PreferTraditionalChinese;
                _currentEnableAltMovieDbUrl = options.MetadataEnhanceOptions.EnableAltMovieDbUrl;
                _currentEnableLocalMovieDb = options.MetadataEnhanceOptions.EnableLocalMovieDb;
                _currentEnableMovieDbCache = options.MetadataEnhanceOptions.EnableMovieDbCache;
                _currentEnableLocalMetadataSource = options.EnableLocalMetadataSource;
                _currentDistinguishChinesePosters = options.MetadataEnhanceOptions.DistinguishChinesePosters;
                _currentPreferOriginalPoster = options.MetadataEnhanceOptions.PreferOriginalPoster;
                _currentChineseMetadataOrder = options.MetadataEnhanceOptions.ChineseMetadataOrder;
                _currentConvertSimplifiedToTraditional =
                    options.MetadataEnhanceOptions.ConvertSimplifiedToTraditional;

                MovieDbApi.Initialize();
            }

            LocalMetadataSource.ApplyOptions(options);
            UnifiedRatingService.ApplyOptions(options);

            if (!suppress)
            {
                Logger.Info("ChineseSearch is set to {0}", options.SearchOptions.EnableChineseSearch);
                Logger.Info("PinyinSearch is set to {0}", options.SearchOptions.PinyinSearch);
                Logger.Info("ConversionSearch is set to {0}", options.SearchOptions.ConversionSearch);
                Logger.Info("AliasSearch is set to {0}", options.SearchOptions.AliasSearch);
                Logger.Info("SmartRanking is set to {0}", options.SearchOptions.SmartRanking);
                Logger.Info("TmdbAliasSearch is set to {0}", options.SearchOptions.TmdbAliasSearch);
            }

            if (!IsModSupported && options.MediaInfoExtractOptions.ExclusiveExtract)
            {
                // The mod is not running; never let the saved options claim that it is.
                options.MediaInfoExtractOptions.ExclusiveExtract = false;
                Logger.Warn("ExclusiveExtract is unavailable ({0}); the option was turned off",
                    PatchManager.ModFailureReason);
            }

            UpdateExclusiveControlFeatures(options.MediaInfoExtractOptions);

            if (!suppress)
            {
                Logger.Info("ExclusiveExtract is set to {0}", options.MediaInfoExtractOptions.ExclusiveExtract);
                var controlFeatures = GetSelectedExclusiveFeatureDescription();
                Logger.Info("ExclusiveExtract - ControlFeatures is set to {0}",
                    string.IsNullOrEmpty(controlFeatures) ? "EMPTY" : controlFeatures);
            }

            if (_currentExclusiveExtract != options.MediaInfoExtractOptions.ExclusiveExtract)
            {
                _currentExclusiveExtract = options.MediaInfoExtractOptions.ExclusiveExtract;

                if (_currentExclusiveExtract)
                {
                    PatchManager.ExclusiveExtract?.Patch();
                }
                else
                {
                    PatchManager.ExclusiveExtract?.Unpatch();
                }
            }

            if (_currentBeautifyMissingMetadata != options.ExperienceEnhanceOptions.BeautifyMissingMetadata)
            {
                _currentBeautifyMissingMetadata = options.ExperienceEnhanceOptions.BeautifyMissingMetadata;

                if (_currentBeautifyMissingMetadata)
                {
                    if (BeautifyMissingMetadata.Instance is null) _ = new BeautifyMissingMetadata();
                    else BeautifyMissingMetadata.Instance.Patch();
                }
                else
                {
                    BeautifyMissingMetadata.Instance?.Unpatch();
                }
            }

            if (_currentEnhanceMissingEpisodes != options.ExperienceEnhanceOptions.EnhanceMissingEpisodes)
            {
                _currentEnhanceMissingEpisodes = options.ExperienceEnhanceOptions.EnhanceMissingEpisodes;

                if (_currentEnhanceMissingEpisodes)
                {
                    if (EnhanceMissingEpisodes.Instance is null) _ = new EnhanceMissingEpisodes();
                    else EnhanceMissingEpisodes.Instance.Patch();
                }
                else
                {
                    EnhanceMissingEpisodes.Instance?.Unpatch();
                }
            }

            if (_currentNoBoxsetsAutoCreation != options.ExperienceEnhanceOptions.NoBoxsetsAutoCreation)
            {
                _currentNoBoxsetsAutoCreation = options.ExperienceEnhanceOptions.NoBoxsetsAutoCreation;

                if (_currentNoBoxsetsAutoCreation)
                {
                    if (NoBoxsetsAutoCreation.Instance is null) _ = new NoBoxsetsAutoCreation();
                    else NoBoxsetsAutoCreation.Instance.Patch();
                }
                else
                {
                    NoBoxsetsAutoCreation.Instance?.Unpatch();
                }
            }

            if (!IsModSupported && options.GeneralOptions.DeepDelete)
            {
                // Deep Delete is a Harmony-only feature; do not leave it enabled when the
                // hook could not be installed.
                options.GeneralOptions.DeepDelete = false;
                Logger.Warn("DeepDelete is unavailable ({0}); the option was turned off",
                    PatchManager.ModFailureReason);
            }

            if (_currentDeepDelete != options.GeneralOptions.DeepDelete)
            {
                _currentDeepDelete = options.GeneralOptions.DeepDelete;

                if (_currentDeepDelete)
                {
                    PatchManager.DeepDelete?.Patch();
                }
                else
                {
                    PatchManager.DeepDelete?.Unpatch();
                }
            }

            SearchIndexService.OnOptionsSaved();

            if (suppress) _currentSuppressOnOptionsSaved = false;

            base.OnOptionsSaved(options);
        }

        protected override PluginOptions OnBeforeShowUI(PluginOptions options)
        {
            options.Disclaimer.Clear();
            options.Disclaimer.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.DisclaimerButtonText,
                    Icon = IconNames.privacy_tip,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/Shiyi9720/StrmAssistant-Enhanced#%E5%A3%B0%E6%98%8E"
                });

            if (options.ShowConflictPluginLoadedStatus)
            {
                options.ConflictPluginLoadedStatus.Caption = Resources
                    .PluginOptions_IncompatibleMessage_Please_uninstall_the_conflict_plugin_Strm_Extract;
                options.ConflictPluginLoadedStatus.StatusText = string.Empty;
                options.ConflictPluginLoadedStatus.Status = ItemStatus.Warning;
            }
            else
            {
                options.ConflictPluginLoadedStatus.Caption = string.Empty;
                options.ConflictPluginLoadedStatus.StatusText = string.Empty;
                options.ConflictPluginLoadedStatus.Status = ItemStatus.None;
            }

            var extractOptions = options.MediaInfoExtractOptions;

            if (!IsModSupported)
            {
                // The toggle itself is disabled by EnabledCondition; turning it off here makes
                // the state unambiguous and the status item says why it is not available.
                extractOptions.ExclusiveExtract = false;
                extractOptions.ExclusiveExtractUnavailableStatus.Caption = string.Format(
                    Resources.ModOptions_ExclusiveExtractUnavailable_Exclusive_Extract_is_unavailable__0___The_option_is_disabled_and_Emby_s_native_extraction_is_used_instead_,
                    string.IsNullOrEmpty(PatchManager.ModFailureReason) ? "unknown" : PatchManager.ModFailureReason);
                extractOptions.ExclusiveExtractUnavailableStatus.StatusText = string.Empty;
                extractOptions.ExclusiveExtractUnavailableStatus.Status = ItemStatus.Warning;
            }
            else
            {
                extractOptions.ExclusiveExtractUnavailableStatus.Caption = string.Empty;
                extractOptions.ExclusiveExtractUnavailableStatus.StatusText = string.Empty;
                extractOptions.ExclusiveExtractUnavailableStatus.Status = ItemStatus.None;
            }

            var libraries = _libraryManager.GetVirtualFolders();

            var list = new List<EditorSelectOption>();
            var listShow = new List<EditorSelectOption>();
            var listMarkerEnabled = new List<EditorSelectOption>();

            list.Add(new EditorSelectOption
            {
                Value = "-1",
                Name = Resources.Favorites,
                IsEnabled = true
            });

            listMarkerEnabled.Add(new EditorSelectOption
            {
                Value = "-1",
                Name = Resources.Favorites,
                IsEnabled = true
            });

            foreach (var item in libraries)
            {
                if (ExcludedCollectionTypes.Contains(item.CollectionType))
                {
                    continue;
                }

                var selectOption = new EditorSelectOption
                {
                    Value = item.ItemId,
                    Name = item.Name,
                    IsEnabled = true,
                };

                list.Add(selectOption);

                if (item.CollectionType == "tvshows" || item.CollectionType is null)
                {
                    listShow.Add(selectOption);

                    if (item.LibraryOptions.EnableMarkerDetection)
                    {
                        listMarkerEnabled.Add(selectOption);
                    }
                }
            }

            options.MediaInfoExtractOptions.LibraryList = list;

            options.MediaInfoExtractOptions.ExclusiveControlList.Clear();
            foreach (Enum control in Enum.GetValues(typeof(ExclusiveControl)))
            {
                options.MediaInfoExtractOptions.ExclusiveControlList.Add(new EditorSelectOption
                {
                    Value = control.ToString(),
                    Name = Emby.Media.Common.Extensions.EnumExtensions.GetDescription(control),
                    IsEnabled = true,
                });
            }

            if (options.ExperienceEnhanceOptions.PinyinSortOptions != null)
            {
                options.ExperienceEnhanceOptions.PinyinSortOptions.LibraryList =
                    list.Where(option => option.Value != "-1").ToList();
            }
            options.MetadataEnhanceOptions.LibraryList = listShow;
            options.ExperienceEnhanceOptions.BeautifyMissingMetadataLibraryList = listShow;
            options.IntroSkipOptions.LibraryList = listShow;
            options.IntroSkipOptions.MarkerEnabledLibraryList = listMarkerEnabled;
            // per-library fingerprint overrides are hidden from the UI (see IntroSkipOptions)

            var catchTaskList = new List<EditorSelectOption>();
            foreach (Enum item in Enum.GetValues(typeof(CatchupTask)))
            {
                var selectOption = new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = EnumExtensions.GetDescription(item),
                    IsEnabled = true,
                };

                catchTaskList.Add(selectOption);
            }

            options.GeneralOptions.CatchupTaskList = catchTaskList;

            var persistOptionList = new List<EditorRadioOption>
            {
                new EditorRadioOption
                {
                    Value = PersistMediaInfoOption.Default,
                    PrimaryText = Resources.MediaInfoExtractOptions_PersistMediaInfo_Persist_MediaInfo,
                    SecondaryText =
                        Resources
                            .MediaInfoExtractOptions_PersistMediaInfo_Persist_media_info_in_JSON_file__Default_is_OFF_
                },
                new EditorRadioOption
                {
                    Value = PersistMediaInfoOption.Restore,
                    PrimaryText = Resources.MediaInfoExtractOptions_MediaInfoRestoreMode_MediaInfo_Restore_Mode,
                    SecondaryText =
                        Resources
                            .MediaInfoExtractOptions_MediaInfoRestoreMode_Only_restore_media_info__chapters__and_video_thumbnails_from_JSON_or_BIF__skipping_extraction__Default_is_OFF_
                },
                new EditorRadioOption
                {
                    Value = PersistMediaInfoOption.None,
                    PrimaryText = Resources.PersistMediaInfoOption_None_None
                }
            };

            options.MediaInfoExtractOptions.PersistMediaInfoOptionList = persistOptionList;

            var episodeRefreshOptionList = new List<EditorSelectOption>();
            foreach (Enum item in Enum.GetValues(typeof(EpisodeRefreshOption)))
            {
                var selectOption = new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = EnumExtensions.GetDescription(item),
                    IsEnabled = true,
                };

                episodeRefreshOptionList.Add(selectOption);
            }

            options.MetadataEnhanceOptions.EpisodeRefreshOptionList = episodeRefreshOptionList;

            options.MetadataEnhanceOptions.RefreshPersonMode =
                options.MetadataEnhanceOptions.GetEffectiveRefreshPersonMode().ToString();
            var refreshPersonModeList = new List<EditorRadioOption>();
            foreach (Enum item in Enum.GetValues(typeof(RefreshPersonModeOption)))
            {
                refreshPersonModeList.Add(new EditorRadioOption
                {
                    Value = item.ToString(),
                    PrimaryText = EnumExtensions.GetDescription(item)
                });
            }

            options.MetadataEnhanceOptions.RefreshPersonModeList = refreshPersonModeList;

            var introSkipPreferenceList = new List<EditorSelectOption>();
            foreach (Enum item in Enum.GetValues(typeof(IntroSkipPreference)))
            {
                var selectPreference = new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = EnumExtensions.GetDescription(item),
                    IsEnabled = true,
                };

                introSkipPreferenceList.Add(selectPreference);
            }

            options.IntroSkipOptions.IntroSkipPreferenceList = introSkipPreferenceList;

            options.AboutOptions.VersionInfoList.Clear();
            options.AboutOptions.VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = GetVersionHash(),
                    Icon = IconNames.info,
                    IconMode = ItemListIconMode.SmallRegular
                });

            options.AboutOptions.VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.Repo_Link,
                    Icon = IconNames.code,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/Shiyi9720/StrmAssistant-Enhanced",
                });

            options.AboutOptions.VersionInfoList.Add(
                new GenericListItem
                {
                    PrimaryText = Resources.Wiki_Link,
                    Icon = IconNames.menu_book,
                    IconMode = ItemListIconMode.SmallRegular,
                    HyperLink = "https://github.com/Shiyi9720/StrmAssistant-Enhanced#%E8%83%BD%E5%8A%9B%E6%B8%85%E5%8D%95",
                });

            var allUsers = LibraryApi.AllUsers;
            var userList = new List<EditorSelectOption>();
            foreach (var user in allUsers)
            {
                var selectOption = new EditorSelectOption
                {
                    Value = user.Key.InternalId.ToString(),
                    Name = (user.Value ? "\ud83d\udc51" : "\ud83d\udc64") + user.Key.Name,
                    IsEnabled = true,
                };

                userList.Add(selectOption);
            }

            options.IntroSkipOptions.UserList = userList;

            var searchStatus = SearchIndexService.GetStatus();

            if (!searchStatus.Enabled)
            {
                options.SearchOptions.IndexStatus.Caption = Resources.SearchOptions_IndexStatus_Disabled;
                options.SearchOptions.IndexStatus.Status = ItemStatus.None;
            }
            else if (searchStatus.IsBuilding)
            {
                options.SearchOptions.IndexStatus.Caption = string.Format(
                    Resources.SearchOptions_IndexStatus_Building, Math.Round(searchStatus.Progress));
                options.SearchOptions.IndexStatus.Status = ItemStatus.InProgress;
            }
            else if (searchStatus.Ready)
            {
                options.SearchOptions.IndexStatus.Caption = string.Format(
                    Resources.SearchOptions_IndexStatus_Ready, searchStatus.ItemCount,
                    searchStatus.BuiltAtUtc?.ToLocalTime().ToString("g") ?? "-");
                options.SearchOptions.IndexStatus.Status = ItemStatus.Succeeded;
            }
            else
            {
                options.SearchOptions.IndexStatus.Caption = Resources.SearchOptions_IndexStatus_NotReady;
                options.SearchOptions.IndexStatus.Status = ItemStatus.None;
            }

            options.SearchOptions.IndexStatus.StatusText = string.Empty;

            return base.OnBeforeShowUI(options);
        }

        private static PluginPageInfo _pluginPageInfo;

        protected override void OnCreatePageInfo(PluginPageInfo pageInfo)
        {
            _pluginPageInfo = pageInfo;
            pageInfo.Name = Name;
            pageInfo.DisplayName =
                Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant", DefaultUICulture);
            pageInfo.EnableInMainMenu = true;
            pageInfo.MenuIcon = "video_settings";

            base.OnCreatePageInfo(pageInfo);
        }

        /// <summary>
        /// 插件加载瞬间卫星资源可能尚未就绪，侧栏显示名会被固定成中性资源（英文）。
        /// 启动后再按当前 UI 文化重算一次，Emby 缓存的是同一个 PluginPageInfo 对象，改属性即生效。
        /// </summary>
        internal static void RefreshPageInfoDisplayName()
        {
            var info = _pluginPageInfo;
            if (info == null) return;

            try
            {
                Properties.Resources.ResetCachedResources();
                var culture = Instance?.DefaultUICulture ?? System.Globalization.CultureInfo.CurrentUICulture;
                var text = Properties.Resources.ResourceManager.GetString(
                    "PluginOptions_EditorTitle_Strm_Assistant", culture);
                if (!string.IsNullOrEmpty(text)) info.DisplayName = text;
            }
            catch
            {
                // 显示名刷新失败不影响插件功能
            }
        }

        /// <summary>
        /// 关于区显示的版本行。只展示干净的「版本 + 适配的 Emby 版本」，不再输出内部构建标记与提交哈希。
        /// </summary>
        private static string GetVersionHash()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            var text = version == null ? "?" : version.ToString();

            if (text.EndsWith(".0.0")) text = text.Substring(0, text.Length - 4);
            else if (text.EndsWith(".0")) text = text.Substring(0, text.Length - 2);

            return $"v{text}";
        }
    }
}
