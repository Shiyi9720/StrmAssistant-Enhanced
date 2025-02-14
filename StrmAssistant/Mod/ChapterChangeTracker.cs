using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class ChapterChangeTracker : PatchBase<ChapterChangeTracker>
    {
        private static MethodInfo _saveChapters;
        private static MethodInfo _deleteChapters;

        private static readonly AsyncLocal<long> BypassItem = new AsyncLocal<long>();

        public ChapterChangeTracker()
        {
            Initialize();

            if (Plugin.Instance.MediaInfoExtractStore.GetOptions().PersistMediaInfo)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var sqliteItemRepository =
                embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
            _saveChapters = sqliteItemRepository.GetMethod("SaveChapters",
                BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(long), typeof(bool), typeof(List<ChapterInfo>) }, null);
            _deleteChapters =
                sqliteItemRepository.GetMethod("DeleteChapters", BindingFlags.Instance | BindingFlags.Public);
        }

        protected override void Prepare(bool apply)
        {
            if (Plugin.Instance.MediaInfoExtractStore.GetOptions().IsModSupported)
            {
                PatchUnpatch(PatchTracker, apply, _saveChapters, postfix: nameof(SaveChaptersPostfix));
                //PatchUnpatch(PatchTracker, apply, _deleteChapters, postfix: nameof(DeleteChaptersPostfix));
            }
        }

        public static void BypassInstance(BaseItem item)
        {
            BypassItem.Value = item.InternalId;
        }

        [HarmonyPostfix]
        private static void SaveChaptersPostfix(long itemId, bool clearExtractionFailureResult,
            List<ChapterInfo> chapters)
        {
            if (chapters.Count == 0) return;

            if (BypassItem.Value != 0 && BypassItem.Value == itemId) return;

            _ = Plugin.MediaInfoApi.SerializeMediaInfo(itemId, true, "Save Chapters", CancellationToken.None);
        }

        [HarmonyPostfix]
        private static void DeleteChaptersPostfix(long itemId, MarkerType[] markerTypes)
        {
            if (BypassItem.Value != 0 && BypassItem.Value == itemId) return;

            _ = Plugin.MediaInfoApi.SerializeMediaInfo(itemId, true, "Delete Chapters", CancellationToken.None);
        }
    }
}
