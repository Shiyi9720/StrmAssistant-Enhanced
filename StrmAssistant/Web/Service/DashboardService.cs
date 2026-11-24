using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// Dashboard data source: memory, running tasks, active playback sessions, search index
    /// counters and queue lengths. Everything is sampled per request (the panel polls); the only
    /// mutating operation is cancelling a scheduled task.
    ///
    /// The whole panel is opt-in (ExperienceEnhanceOptions.EnableDashboardPanel, default off) so
    /// an upgrade does not change the existing dashboard page.
    /// </summary>
    public class DashboardService : BaseApiService
    {
        private readonly ISessionManager _sessionManager;
        private readonly ITaskManager _taskManager;

        public DashboardService(ISessionManager sessionManager, ITaskManager taskManager)
        {
            _sessionManager = sessionManager;
            _taskManager = taskManager;
        }

        public object Get(GetDashboard request)
        {
            var response = new DashboardResponse
            {
                GeneratedAtUtc = DateTime.UtcNow,
                Enabled = Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions
                    ?.EnableDashboardPanel == true
            };

            if (!response.Enabled) return response;

            response.Memory = ReadMemory();
            response.Tasks = ReadTasks();
            response.Sessions = ReadSessions();
            response.Search = ReadSearch();
            response.Queues = ReadQueues();

            return response;
        }

        public object Post(CancelDashboardTask request)
        {
            var user = GetUserForRequest(null);

            if (user != null && !user.Policy.IsAdministrator)
            {
                return new CancelDashboardTaskResponse { Message = "无权限。" };
            }

            if (Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions?.EnableDashboardPanel != true)
            {
                return new CancelDashboardTaskResponse { Message = "仪表盘未启用。" };
            }

            var key = request?.Key?.Trim();

            if (string.IsNullOrEmpty(key))
            {
                return new CancelDashboardTaskResponse { Message = "缺少任务 Key。" };
            }

            var worker = (_taskManager?.ScheduledTasks ?? Array.Empty<IScheduledTaskWorker>())
                .FirstOrDefault(item => item?.ScheduledTask != null &&
                                        string.Equals(item.ScheduledTask.Key, key,
                                            StringComparison.Ordinal));

            if (worker is null)
            {
                return new CancelDashboardTaskResponse { Message = "找不到任务：" + key };
            }

            if (worker.State == TaskState.Idle)
            {
                return new CancelDashboardTaskResponse { Message = "任务当前未在运行：" + worker.Name };
            }

            try
            {
                _taskManager.Cancel(worker);
                Plugin.Instance?.Logger?.Info("Dashboard - cancel requested for task " + key);
                return new CancelDashboardTaskResponse { Success = true };
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Warn("Dashboard - cancel failed for " + key + ": " + e.Message);
                return new CancelDashboardTaskResponse { Message = "取消失败：" + e.Message };
            }
        }

        // --- memory -------------------------------------------------------------------------

        private static DashboardMemory ReadMemory()
        {
            var memory = new DashboardMemory();

            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    process.Refresh();
                    memory.WorkingSetBytes = process.WorkingSet64;
                    memory.PeakWorkingSetBytes = process.PeakWorkingSet64;
                    memory.PrivateBytes = process.PrivateMemorySize64;
                }
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Dashboard - process memory unavailable: " + e.Message);
            }

            try
            {
                memory.ManagedHeapBytes = GC.GetTotalMemory(false);
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("Dashboard - GC memory unavailable: " + e.Message);
            }

            // Linux: /proc and cgroup describe what the Emby container actually sees.
            var status = ReadProcSelfStatus();
            if (status.TryGetValue("VmRSS", out var rss)) memory.WorkingSetBytes = rss;
            if (status.TryGetValue("VmHWM", out var hwm)) memory.PeakWorkingSetBytes = hwm;

            var memInfo = ReadProcMemInfo();
            memory.SystemTotalBytes = memInfo.TotalBytes;

            var containerLimit = ReadFirstLong("/sys/fs/cgroup/memory.max",
                "/sys/fs/cgroup/memory/memory.limit_in_bytes");
            var containerUsage = ReadFirstLong("/sys/fs/cgroup/memory.current",
                "/sys/fs/cgroup/memory/memory.usage_in_bytes");

            if (containerLimit.HasValue && containerLimit.Value > 0 &&
                containerLimit.Value < long.MaxValue / 2)
            {
                memory.ContainerLimitBytes = containerLimit.Value;
                memory.ContainerUsageBytes = containerUsage;
                memory.LimitSource = "container";
                memory.UsagePercent = DashboardStats.UsagePercent(
                    containerUsage ?? memory.WorkingSetBytes, containerLimit);
            }
            else if (memory.SystemTotalBytes > 0)
            {
                memory.LimitSource = "system";
                memory.UsagePercent = DashboardStats.UsagePercent(memory.WorkingSetBytes,
                    memory.SystemTotalBytes);
            }
            else
            {
                memory.LimitSource = "process";
                memory.UsagePercent = 0;
            }

            return memory;
        }

        private static Dictionary<string, long> ReadProcSelfStatus()
        {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);

            try
            {
                foreach (var line in File.ReadLines("/proc/self/status"))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;

                    var name = line.Substring(0, colon);
                    if (name != "VmRSS" && name != "VmHWM") continue;

                    var parts = line.Substring(colon + 1).Trim()
                        .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length >= 1 && long.TryParse(parts[0], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var kb))
                    {
                        result[name] = kb * 1024;
                    }
                }
            }
            catch (Exception)
            {
                // Not Linux, or /proc unavailable: keep the Process values.
            }

            return result;
        }

        private static (long TotalBytes, long AvailableBytes) ReadProcMemInfo()
        {
            long total = 0, available = 0;

            try
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;

                    var name = line.Substring(0, colon);
                    if (name != "MemTotal" && name != "MemAvailable") continue;

                    var parts = line.Substring(colon + 1).Trim()
                        .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length >= 1 && long.TryParse(parts[0], NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var kb))
                    {
                        if (name == "MemTotal") total = kb * 1024;
                        else available = kb * 1024;
                    }
                }
            }
            catch (Exception)
            {
                // Keep zeroes; the panel shows "—" for unknown values.
            }

            return (total, available);
        }

        private static long? ReadFirstLong(params string[] paths)
        {
            foreach (var path in paths)
            {
                try
                {
                    if (!File.Exists(path)) continue;

                    var text = File.ReadAllText(path).Trim();
                    if (text == "max") return null;

                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var value)) return value;
                }
                catch (Exception)
                {
                    // Try the next candidate.
                }
            }

            return null;
        }

        // --- tasks --------------------------------------------------------------------------

        private List<DashboardTaskInfo> ReadTasks()
        {
            var result = new List<DashboardTaskInfo>();

            foreach (var worker in _taskManager?.ScheduledTasks ?? Array.Empty<IScheduledTaskWorker>())
            {
                var task = worker?.ScheduledTask;
                if (task is null || worker.State == TaskState.Idle) continue;

                var progress = worker.CurrentProgress;

                result.Add(new DashboardTaskInfo
                {
                    Key = task.Key,
                    Name = worker.Name ?? task.Name,
                    Category = worker.Category ?? task.Category,
                    State = worker.State.ToString(),
                    Progress = progress.HasValue && !double.IsNaN(progress.Value) &&
                               !double.IsInfinity(progress.Value)
                        ? (double?)Math.Round(Math.Max(0, Math.Min(100, progress.Value)), 1)
                        : null,
                    StartedUtc = worker.LastExecutionResult?.StartTimeUtc,
                    Cancellable = worker.State == TaskState.Running
                });
            }

            return result.OrderBy(task => task.Name, StringComparer.Ordinal).ToList();
        }

        // --- sessions -----------------------------------------------------------------------

        private List<DashboardSessionInfo> ReadSessions()
        {
            var result = new List<DashboardSessionInfo>();

            IEnumerable<SessionInfo> sessions;

            try
            {
                sessions = _sessionManager?.Sessions ?? Enumerable.Empty<SessionInfo>();
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Warn("Dashboard - sessions unavailable: " + e.Message);
                return result;
            }

            foreach (var session in sessions)
            {
                var item = session?.NowPlayingItem;
                if (item is null) continue;

                result.Add(new DashboardSessionInfo
                {
                    SessionId = session.Id,
                    UserName = session.UserName,
                    DeviceName = session.DeviceName,
                    Client = session.Client,
                    ItemName = item.Name,
                    SeriesName = item.SeriesName,
                    ItemType = item.Type?.ToString(),
                    PositionTicks = session.PlayState?.PositionTicks ?? 0,
                    RunTimeTicks = item.RunTimeTicks ?? 0,
                    IsPaused = session.PlayState?.IsPaused ?? false
                });
            }

            return result;
        }

        // --- search / queues ----------------------------------------------------------------

        private static DashboardSearchInfo ReadSearch()
        {
            var status = Plugin.SearchIndexService?.GetStatus();

            if (status is null) return new DashboardSearchInfo();

            return new DashboardSearchInfo
            {
                Enabled = status.Enabled,
                Ready = status.Ready,
                IsBuilding = status.IsBuilding,
                ItemCount = status.ItemCount,
                KeyCount = status.KeyCount,
                PendingChanges = status.PendingChanges,
                Progress = status.Progress,
                BuiltAtUtc = status.BuiltAtUtc,
                LastError = status.LastError
            };
        }

        private static DashboardQueueInfo ReadQueues()
        {
            var snapshot = QueueManager.GetQueueSnapshot();

            return new DashboardQueueInfo
            {
                MediaInfoQueued = snapshot.MediaInfoQueued,
                MediaInfoRunning = snapshot.MediaInfoRunning,
                FingerprintQueued = snapshot.FingerprintQueued,
                FingerprintRunning = snapshot.FingerprintRunning,
                IntroQueued = snapshot.IntroQueued,
                EpisodeRefreshQueued = snapshot.EpisodeRefreshQueued,
                FavoriteQueued = snapshot.FavoriteQueued
            };
        }
    }
}
