using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    public class ExtractIsoVideoTask : IScheduledTask, IConfigurableScheduledTask
    {
        private ILogger _logger => Plugin.Instance?.Logger;
        private readonly ILibraryManager _libraryManager;

        public ExtractIsoVideoTask(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger?.Info("IsoExtract - Scheduled Task Execute");

            var options = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions;

            if (!options.EnableIsoExtraction)
            {
                _logger?.Info("IsoExtract - Disabled, skipping");
                progress.Report(100.0);
                return;
            }

            await Task.Yield();
            progress.Report(0);

            var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    HasPath = true,
                    IncludeItemTypes = new[] { nameof(Movie), nameof(Episode) }
                })
                .Where(i => IsoApi.IsIsoPath(i.Path))
                .ToList();

            _logger?.Info("IsoExtract - Number of items: " + items.Count);

            double total = items.Count;
            var current = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var result = await IsoApi.ExtractAsync(item.Path, null, cancellationToken)
                        .ConfigureAwait(false);

                    if (string.IsNullOrEmpty(result))
                    {
                        _logger?.Info("IsoExtract - Skipped: " + item.Path);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger?.Info("IsoExtract - Scheduled Task Cancelled");
                    return;
                }
                catch (Exception e)
                {
                    _logger?.Info("IsoExtract - Item failed: " + item.Path);
                    _logger?.Debug(e.Message);
                    _logger?.Debug(e.StackTrace);
                }

                current++;
                progress.Report(current / total * 100);
            }

            progress.Report(100.0);
            _logger?.Info("IsoExtract - Scheduled Task Complete");
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "ExtractIsoVideoTask";

        public string Description => Resources.ResourceManager.GetString(
            "IsoExtractTask_Description_Extract_the_main_video_from_ISO_images_so_it_can_be_played_as_an_additional_media_source",
            Plugin.Instance.DefaultUICulture);

        public string Name => Resources.ResourceManager.GetString("IsoExtractTask_Name_Extract_ISO_Video",
            Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public bool IsHidden => !Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.EnableIsoExtraction;

        public bool IsEnabled => true;

        public bool IsLogged => true;
    }
}
