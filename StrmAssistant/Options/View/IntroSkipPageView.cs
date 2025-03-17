using Emby.Media.Common.Extensions;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Tasks;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using StrmAssistant.Options.Store;
using StrmAssistant.Options.UIBaseClasses.Views;
using StrmAssistant.Properties;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace StrmAssistant.Options.View
{
    internal class IntroSkipPageView : PluginPageView
    {
        private readonly IntroSkipOptionsStore _store;

        public IntroSkipPageView(PluginInfo pluginInfo, ILibraryManager libraryManager,
            IntroSkipOptionsStore store)
            : base(pluginInfo.Id)
        {
            _store = store;
            ContentData = store.GetOptions();
            IntroSkipOptions.Initialize(libraryManager);
        }

        public IntroSkipOptions IntroSkipOptions => ContentData as IntroSkipOptions;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            if (ContentData is IntroSkipOptions options)
            {
                options.ValidateOrThrow();
            }

            _store.SetOptions(IntroSkipOptions);
            return base.OnSaveCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            switch (commandId)
            {
                case "ClearIntroCreditsMarkers":
                {
                    if (ContentData is IntroSkipOptions options)
                    {
                        options.ValidateOrThrow();
                    }

                    Task.Run(HandleClearIntroButton).FireAndForget(Plugin.Instance.Logger);
                    return Task.FromResult<IPluginUIView>(this);
                }
            }

            return base.RunCommand(itemId, commandId, data);
        }

        private async Task HandleClearIntroButton()
        {
            IntroSkipOptions.ClearIntroButton.IsEnabled = false;
            IntroSkipOptions.ClearIntroResult.Clear();
            var progressItem = new GenericListItem
            {
                Icon = IconNames.work_outline,
                IconMode = ItemListIconMode.SmallRegular,
                Status = ItemStatus.InProgress,
                HasPercentage = true
            };
            IntroSkipOptions.ClearIntroResult.Add(progressItem);
            RaiseUIViewInfoChanged();
            await Task.Delay(10.ms());

            var clearIntroShowIds = IntroSkipOptions.ClearIntroShows
                .Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => long.TryParse(part.Trim(), out var id) ? id : (long?)null)
                .Where(id => id.HasValue)
                .Select(id => id.Value)
                .ToArray();

            var clearShowItems = Plugin.LibraryApi.GetItemsByIds(clearIntroShowIds)
                .Where(item => item is Series || item is Season).ToList();

            foreach (var item in clearShowItems)
            {
                var listItem = new GenericListItem();

                if (item is Series series)
                {
                    listItem.PrimaryText = $"{series.Name} ({series.InternalId}) - {series.ContainingFolderPath}";
                }
                else if (item is Season season)
                {
                    listItem.PrimaryText =
                        $"{season.SeriesName} - {season.Name} ({season.InternalId}) - {season.ContainingFolderPath}";
                }

                listItem.Icon = IconNames.clear_all;
                listItem.IconMode = ItemListIconMode.SmallRegular;

                IntroSkipOptions.ClearIntroResult.Add(listItem);
            }
            
            var episodes = Plugin.ChapterApi.FetchClearTaskItems(clearShowItems);

            progressItem.PercentComplete = 20;
            RaiseUIViewInfoChanged();
            await Task.Delay(10.ms());

            var total = episodes.Count;
            var current = 0;

            foreach (var item in episodes)
            {
                Plugin.ChapterApi.RemoveIntroCreditsMarkers(item);
                current++;
                var percentDone = current * 100 / total;
                var adjustedProgress = 20 + percentDone * 80 / 100;
                progressItem.PercentComplete = adjustedProgress;
                Plugin.Instance.Logger.Info("IntroSkipClear - Task " + current + "/" + total + " - " + item.Path);
                RaiseUIViewInfoChanged();
                await Task.Delay(10.ms());
            }

            IntroSkipOptions.ClearIntroButton.IsEnabled = true;
            progressItem.HasPercentage = false;
            progressItem.SecondaryText = Resources.Operation_Success;
            progressItem.Icon = IconNames.info;
            progressItem.Status = ItemStatus.Succeeded;
            RaiseUIViewInfoChanged();
            await Task.Delay(5000);
            IntroSkipOptions.ClearIntroResult.Clear();
            RaiseUIViewInfoChanged();
        }
    }
}
