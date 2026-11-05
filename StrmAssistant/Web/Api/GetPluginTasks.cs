using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System.Collections.Generic;

namespace StrmAssistant.Web.Api
{
    [Route("/StrmAssistant/Tasks", "GET")]
    [Authenticated]
    public class GetPluginTasks : IReturn<PluginTasksResponse>
    {
    }

    public class PluginTasksResponse
    {
        public List<PluginTaskInfo> Tasks { get; set; } = new List<PluginTaskInfo>();
    }

    public class PluginTaskInfo
    {
        public string Id { get; set; }
        public string Key { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string State { get; set; }
        public bool IsRunning { get; set; }
        public double? Progress { get; set; }
    }
}
