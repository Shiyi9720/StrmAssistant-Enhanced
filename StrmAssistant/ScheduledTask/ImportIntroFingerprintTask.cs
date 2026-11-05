using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    public class ImportIntroFingerprintTask : IScheduledTask, IConfigurableScheduledTask
    {
        private ILogger _logger => Plugin.Instance?.Logger;

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger?.Info("IntroFingerprintImport - Scheduled Task Execute");

            try
            {
                Plugin.FingerprintLibraryApi.Import(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger?.Info("IntroFingerprintImport - Scheduled Task Cancelled");
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                _logger?.Info("IntroFingerprintImport - Failed: " + e.Message);
                _logger?.Debug(e.StackTrace);
            }

            progress.Report(100.0);
            _logger?.Info("IntroFingerprintImport - Scheduled Task Complete");

            return Task.CompletedTask;
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "ImportIntroFingerprintTask";

        public string Description => Resources.ResourceManager.GetString(
            "ImportIntroFingerprintTask_Description_Import_intro_detection_fingerprints_from_a_JSON_file",
            Plugin.Instance.DefaultUICulture);

        public string Name => Resources.ResourceManager.GetString(
            "ImportIntroFingerprintTask_Name_Import_Intro_Fingerprints", Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public bool IsHidden => !Plugin.Instance.GetPluginOptions().IntroSkipOptions.EnableIntroSkip;

        public bool IsEnabled => true;

        public bool IsLogged => true;
    }
}
