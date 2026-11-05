using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    public class ExportIntroFingerprintTask : IScheduledTask, IConfigurableScheduledTask
    {
        private ILogger _logger => Plugin.Instance?.Logger;

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger?.Info("IntroFingerprintExport - Scheduled Task Execute");

            try
            {
                Plugin.FingerprintLibraryApi.Export(progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger?.Info("IntroFingerprintExport - Scheduled Task Cancelled");
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                _logger?.Info("IntroFingerprintExport - Failed: " + e.Message);
                _logger?.Debug(e.StackTrace);
            }

            progress.Report(100.0);
            _logger?.Info("IntroFingerprintExport - Scheduled Task Complete");

            return Task.CompletedTask;
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "ExportIntroFingerprintTask";

        public string Description => Resources.ResourceManager.GetString(
            "ExportIntroFingerprintTask_Description_Export_intro_detection_fingerprints_to_a_JSON_file",
            Plugin.Instance.DefaultUICulture);

        public string Name => Resources.ResourceManager.GetString(
            "ExportIntroFingerprintTask_Name_Export_Intro_Fingerprints", Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public bool IsHidden => !Plugin.Instance.GetPluginOptions().IntroSkipOptions.EnableIntroSkip;

        public bool IsEnabled => true;

        public bool IsLogged => true;
    }
}
