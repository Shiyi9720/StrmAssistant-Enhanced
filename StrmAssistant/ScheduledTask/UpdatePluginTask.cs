using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    public class UpdatePluginTask : IScheduledTask
    {
        private ILogger _logger => Plugin.Instance?.Logger;
        private readonly IApplicationHost _applicationHost;
        private readonly IApplicationPaths _applicationPaths;
        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IActivityManager _activityManager;
        private readonly ILocalizationManager _localizationManager;
        private readonly IServerApplicationHost _serverApplicationHost;

        public UpdatePluginTask(IApplicationHost applicationHost, IApplicationPaths applicationPaths,
            IHttpClient httpClient, IJsonSerializer jsonSerializer, IActivityManager activityManager,
            ILocalizationManager localizationManager, IServerApplicationHost serverApplicationHost)
        {
            _applicationHost = applicationHost;
            _applicationPaths = applicationPaths;
            _httpClient = StrmAssistant.Common.ProxyApi.Wrap(httpClient);
            _jsonSerializer = jsonSerializer;
            _activityManager = activityManager;
            _localizationManager = localizationManager;
            _serverApplicationHost = serverApplicationHost;
        }

        private static string PluginAssemblyFilename => Assembly.GetExecutingAssembly().GetName().Name + ".dll";
        private static string RepoReleaseUrl => "https://api.github.com/repos/Shiyi9720/StrmAssistant-Enhanced/releases/latest";

        public string Key => "UpdatePluginTask";

        public string Name => "Update Plugin";
        //public string Name =>
        //    Resources.ResourceManager.GetString("UpdatePluginTask_Name_Update_Plugin",
        //        Plugin.Instance.DefaultUICulture);

        public string Description => Resources.ResourceManager.GetString(
            "UpdatePluginTask_Description_Updates_plugin_to_the_latest_version", Plugin.Instance.DefaultUICulture);

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerWeekly,
                DayOfWeek = (DayOfWeek)new Random().Next(7),
                TimeOfDayTicks = TimeSpan.FromMinutes(new Random().Next(24 * 4) * 15).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Task.Yield();
            progress.Report(0);

            var failed = 0;

            try
            {
                var githubToken= Plugin.Instance.GetPluginOptions().AboutOptions.GitHubToken;

                using var response = await _httpClient.SendAsync(new HttpRequestOptions
                {
                    Url = RepoReleaseUrl,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = Plugin.Instance.UserAgent,
                    EnableDefaultUserAgent = false,
                    RequestHeaders =
                    {
                        ["Authorization"] = !string.IsNullOrWhiteSpace(githubToken)
                            ? $"token {githubToken}"
                            : string.Empty
                    }
                }, "GET").ConfigureAwait(false);

                await using var contentStream = response.Content;

                var apiResult = _jsonSerializer.DeserializeFromStream<ApiResponseInfo>(contentStream);

                var currentVersion = ParseVersion(Plugin.Instance.CurrentVersion);
                var remoteVersion = ParseVersion(apiResult?.tag_name);

                if (currentVersion == null || remoteVersion == null)
                {
                    // Not an error path on its own, but a silent skip hides a broken tag/assembly
                    // version, so surface it instead of leaving it at Info.
                    _logger?.Warn("Skip auto update: version is not comparable ({0} -> {1})",
                        Plugin.Instance.CurrentVersion, apiResult?.tag_name);
                    return;
                }

                if (currentVersion.CompareTo(remoteVersion) < 0)
                {
                    _logger?.Info("Found new plugin version: {0}", remoteVersion);

                    var asset = (apiResult?.assets ?? new List<ApiAssetInfo>())
                        .FirstOrDefault(a => a.name == PluginAssemblyFilename);
                    var url = asset?.browser_download_url;
                    if (!Uri.IsWellFormedUriString(url, UriKind.Absolute)) throw new Exception("Invalid download url");

                    // GitHub computes this digest over the uploaded asset, so it covers exactly
                    // the bytes we are about to install. No digest means the download cannot be
                    // proven intact; an unverified self-update is worse than skipping the update.
                    var expectedHash = PluginUpdateInstaller.NormalizeSha256Digest(asset.digest);
                    if (expectedHash == null)
                    {
                        throw new InvalidDataException(
                            $"Release asset {PluginAssemblyFilename} has no usable sha256 digest; refusing to install {remoteVersion}");
                    }

                    var dllFilePath = Path.Combine(_applicationPaths.PluginsPath, PluginAssemblyFilename);
                    var tempFilePath = Path.Combine(_applicationPaths.PluginsPath,
                        PluginAssemblyFilename + "." + Guid.NewGuid().ToString("N") + ".download");
                    var backupFilePath = Path.Combine(_applicationPaths.PluginsPath,
                        PluginUpdateInstaller.BuildBackupFileName(PluginAssemblyFilename,
                            Plugin.Instance.CurrentVersion, DateTimeOffset.UtcNow));

                    try
                    {
                        var githubProxy = Plugin.Instance.GetPluginOptions().AboutOptions.GitHubProxy;

                        await using (var responseStream = await _httpClient.Get(new HttpRequestOptions
                                         {
                                             Url = string.IsNullOrWhiteSpace(githubProxy)
                                                 ? url
                                                 : $"{githubProxy.TrimEnd('/')}/{url.TrimStart('/')}",
                                             CancellationToken = cancellationToken,
                                             UserAgent = Plugin.Instance.UserAgent,
                                             EnableDefaultUserAgent = false,
                                             Progress = progress,
                                             RequestHeaders =
                                             {
                                                 ["Authorization"] = !string.IsNullOrWhiteSpace(githubToken)
                                                     ? $"token {githubToken}"
                                                     : string.Empty
                                             }
                                         })
                                         .ConfigureAwait(false))
                        await using (var fileStream = new FileStream(tempFilePath, FileMode.CreateNew,
                                         FileAccess.Write, FileShare.None))
                        {
                            await responseStream.CopyToAsync(fileStream, 81920, cancellationToken)
                                .ConfigureAwait(false);
                            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }

                        var actualHash = PluginUpdateInstaller.ComputeSha256(tempFilePath);
                        if (!PluginUpdateInstaller.MatchesSha256(expectedHash, actualHash))
                        {
                            throw new InvalidDataException(
                                $"Checksum mismatch for {PluginAssemblyFilename}: expected sha256:{expectedHash}, got sha256:{actualHash}");
                        }

                        // The verified file is still a separate file at this point; only the
                        // final rename makes it live, and the old assembly is backed up first.
                        if (!PluginUpdateInstaller.TryInstall(tempFilePath, dllFilePath, backupFilePath,
                                out var installError))
                        {
                            throw new IOException("Install failed: " + installError);
                        }

                        _logger?.Info("Plugin update installed: {0} -> {1} (sha256 {2}, backup {3})",
                            Plugin.Instance.CurrentVersion, remoteVersion, actualHash,
                            Path.GetFileName(backupFilePath));

                        var pruned = PluginUpdateInstaller.PruneBackups(_applicationPaths.PluginsPath,
                            PluginAssemblyFilename, PluginUpdateInstaller.DefaultBackupRetention,
                            out var pruneError);
                        if (pruneError != null)
                        {
                            _logger?.Warn("UpdatePlugin - Failed to prune old backups: {0}", pruneError);
                        }
                        else if (pruned > 0)
                        {
                            _logger?.Info("UpdatePlugin - Removed {0} old backup(s)", pruned);
                        }
                    }
                    finally
                    {
                        if (File.Exists(tempFilePath))
                        {
                            try
                            {
                                File.Delete(tempFilePath);
                            }
                            catch (Exception cleanupError)
                            {
                                _logger?.Warn("UpdatePlugin - Failed to delete temporary download {0}: {1}",
                                    tempFilePath, cleanupError.Message);
                            }
                        }
                    }

                    _logger?.Info("Plugin update complete");

                    _activityManager.Create(new ActivityLogEntry
                    {
                        Name = string.Format(_localizationManager.GetLocalizedString("XUpdatedOnTo"), Category,
                            remoteVersion, _serverApplicationHost.FriendlyName),
                        Type = "PluginUpdateInstalled",
                        Severity = LogSeverity.Info
                    });

                    _applicationHost.NotifyPendingRestart();
                }
                else
                {
                    // The weekly trigger used to page the admins even when there was nothing to
                    // do. "Already current" is the normal outcome, so log it instead.
                    _logger?.Info("No need to update: current {0}, remote {1}",
                        Plugin.Instance.CurrentVersion, remoteVersion);
                }
            }
            catch (OperationCanceledException)
            {
                // expected: the server is shutting down; not an update failure.
                _logger?.Info("UpdatePlugin - Scheduled Task Cancelled");
                return;
            }
            catch (Exception e)
            {
                failed = 1;

                _activityManager.Create(new ActivityLogEntry
                {
                    Name = string.Format(_localizationManager.GetLocalizedString("NameInstallFailedOn"), Category,
                        _serverApplicationHost.FriendlyName),
                    Type = "PluginUpdateFailed",
                    Overview = e.Message,
                    Severity = LogSeverity.Error
                });

                _ = Plugin.NotificationApi.SendMessageToAdmins(
                    $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {Resources.Update_Failed_Message}", 1000);
                _logger?.Error("Update failed: {0}", e.Message + Environment.NewLine + e.StackTrace);
            }

            progress.Report(100);
            _logger?.Info("UpdatePlugin - Outcome: " + ScheduledTaskFailurePolicy.Summary(0, failed, 0));
            ScheduledTaskFailurePolicy.ThrowIfFailed("UpdatePlugin", 0, failed, 0);
        }

        private static Version ParseVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var text = v.StartsWith("v") ? v.Substring(1) : v;
            // 版本标记形如 public-ip-xxxx 时无法比较，返回 null 由调用方跳过自动更新
            return Version.TryParse(text, out var parsed) ? parsed : null;
        }

        internal class ApiResponseInfo
        {
            public string tag_name { get; set; }

            public List<ApiAssetInfo> assets { get; set; }
        }

        internal class ApiAssetInfo
        {
            public string name { get; set; }

            public string browser_download_url { get; set; }

            // "sha256:<hex>" computed by GitHub for the uploaded asset.
            public string digest { get; set; }
        }
    }
}
