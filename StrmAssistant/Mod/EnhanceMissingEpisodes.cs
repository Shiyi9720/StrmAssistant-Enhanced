using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using StrmAssistant.Provider;
using System.Linq;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// Emby computes the "view missing episodes" list by asking the first enabled
    /// ISeriesMetadataProvider for the full episode list and subtracting the episodes present
    /// in the library. This mod swaps the built-in MovieDb series provider for the plugin's
    /// provider, which supports the configured TMDB mirror and the local episode group.
    /// </summary>
    public class EnhanceMissingEpisodes : PatchBase<EnhanceMissingEpisodes>
    {
        private static MethodInfo _getEnabledMetadataProviders;

        public EnhanceMissingEpisodes()
        {
            Initialize();

            if (PatchTracker.FallbackPatchApproach != PatchApproach.Harmony)
            {
                return;
            }

            Patch();
        }

        protected override void OnInitialize()
        {
            var embyProviders = Assembly.Load("Emby.Providers");
            var providerManager = embyProviders.GetType("Emby.Providers.Manager.ProviderManager");
            _getEnabledMetadataProviders = providerManager?.GetMethod("GetEnabledMetadataProviders",
                BindingFlags.Instance | BindingFlags.Public);

            if (_getEnabledMetadataProviders is null)
            {
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.FailureReason = "GetEnabledMetadataProviders could not be located";

                Plugin.Instance.Logger.Warn(
                    "EnhanceMissingEpisodes - The Emby method to patch could not be located");
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _getEnabledMetadataProviders,
                postfix: nameof(GetEnabledMetadataProvidersPostfix));
        }

        [HarmonyPostfix]
        private static void GetEnabledMetadataProvidersPostfix(BaseItem item, LibraryOptions libraryOptions,
            ref IMetadataProvider[] __result)
        {
            if (!(item is Series) || string.IsNullOrEmpty(item.GetProviderId(MetadataProviders.Tmdb)))
            {
                return;
            }

            if (__result is null) return;

            var builtInProvider = __result
                .FirstOrDefault(p => p.GetType().FullName == "MovieDb.MovieDbSeriesProvider");

            var provider = Plugin.MetadataApi.GetMovieDbSeriesProvider();
            var newResult = __result.Where(p => !(p is MovieDbSeriesProvider)).ToList();

            if (builtInProvider != null)
            {
                // Keep the position that the provider order assigned to the built-in provider.
                var index = newResult.IndexOf(builtInProvider);

                if (index < 0) index = newResult.Count;

                newResult.Insert(index, provider);
            }
            else if (!newResult.OfType<ISeriesMetadataProvider>().Any())
            {
                newResult.Add(provider);
            }
            else
            {
                return;
            }

            __result = newResult.ToArray();
        }
    }
}
