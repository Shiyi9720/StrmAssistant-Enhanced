using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;

namespace StrmAssistant.Web.Api
{
    /// <summary>Read-only runtime snapshot for the dashboard panel.</summary>
    [Route("/StrmAssistant/Dashboard", "GET")]
    [Authenticated]
    public class GetDashboard : IReturn<DashboardResponse>
    {
    }

    /// <summary>Cancels a running scheduled task shown on the dashboard.</summary>
    [Route("/StrmAssistant/DashboardCancel", "POST")]
    [Authenticated]
    public class CancelDashboardTask : IReturn<CancelDashboardTaskResponse>
    {
        public string Key { get; set; }
    }

    public class DashboardResponse
    {
        public bool Enabled { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
        public DashboardMemory Memory { get; set; } = new DashboardMemory();
        public List<DashboardTaskInfo> Tasks { get; set; } = new List<DashboardTaskInfo>();
        public List<DashboardSessionInfo> Sessions { get; set; } = new List<DashboardSessionInfo>();
        public DashboardSearchInfo Search { get; set; } = new DashboardSearchInfo();
        public DashboardQueueInfo Queues { get; set; } = new DashboardQueueInfo();
    }

    public class DashboardMemory
    {
        public long WorkingSetBytes { get; set; }
        public long PeakWorkingSetBytes { get; set; }
        public long PrivateBytes { get; set; }
        public long ManagedHeapBytes { get; set; }
        public long SystemTotalBytes { get; set; }
        public long? ContainerLimitBytes { get; set; }
        public long? ContainerUsageBytes { get; set; }
        public double UsagePercent { get; set; }
        public string LimitSource { get; set; }
    }

    public class DashboardTaskInfo
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string State { get; set; }
        public double? Progress { get; set; }
        public DateTimeOffset? StartedUtc { get; set; }
        public bool Cancellable { get; set; }
    }

    public class DashboardSessionInfo
    {
        public string SessionId { get; set; }
        public string UserName { get; set; }
        public string DeviceName { get; set; }
        public string Client { get; set; }
        public string ItemName { get; set; }
        public string SeriesName { get; set; }
        public string ItemType { get; set; }
        public long PositionTicks { get; set; }
        public long RunTimeTicks { get; set; }
        public bool IsPaused { get; set; }
    }

    public class DashboardSearchInfo
    {
        public bool Enabled { get; set; }
        public bool Ready { get; set; }
        public bool IsBuilding { get; set; }
        public int ItemCount { get; set; }
        public int KeyCount { get; set; }
        public int PendingChanges { get; set; }
        public double Progress { get; set; }
        public DateTime? BuiltAtUtc { get; set; }
        public string LastError { get; set; }
    }

    public class DashboardQueueInfo
    {
        public int MediaInfoQueued { get; set; }
        public int MediaInfoRunning { get; set; }
        public int FingerprintQueued { get; set; }
        public int FingerprintRunning { get; set; }
        public int IntroQueued { get; set; }
        public int EpisodeRefreshQueued { get; set; }
        public int FavoriteQueued { get; set; }
    }

    public class CancelDashboardTaskResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }
}
