using MediaBrowser.Controller.Api;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Web.Api;
using System;
using System.Linq;

namespace StrmAssistant.Web.Service
{
    public class TaskService : BaseApiService
    {
        private const string RefreshLibraryKey = "RefreshLibrary";

        private readonly ITaskManager _taskManager;

        public TaskService(ITaskManager taskManager)
        {
            _taskManager = taskManager;
        }

        public object Get(GetPluginTasks request)
        {
            var pluginAssembly = typeof(Plugin).Assembly;

            var tasks = _taskManager.ScheduledTasks
                .Where(i => i.ScheduledTask != null &&
                            i.ScheduledTask.GetType().Assembly == pluginAssembly)
                .Select(i => new PluginTaskInfo
                {
                    Id = i.Id,
                    Key = i.ScheduledTask.Key,
                    Name = i.Name,
                    Category = i.Category,
                    State = i.State.ToString(),
                    IsRunning = i.State == TaskState.Running,
                    Progress = i.CurrentProgress
                })
                .OrderByDescending(i => i.IsRunning)
                .ThenBy(i => i.Name)
                .ToList();

            return new PluginTasksResponse { Tasks = tasks };
        }

        public object Post(RebuildSearchIndex request)
        {
            var user = GetUserForRequest(null);

            if (user != null && !user.Policy.IsAdministrator)
            {
                return new RebuildSearchIndexResponse();
            }

            var worker = _taskManager.ScheduledTasks
                .FirstOrDefault(i => i.ScheduledTask != null && i.ScheduledTask.Key == RefreshLibraryKey);

            if (worker is null)
            {
                Plugin.Instance.Logger.Info("SearchIndexRebuild - RefreshLibrary task not found");
                return new RebuildSearchIndexResponse();
            }

            try
            {
                _taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
                Plugin.Instance.Logger.Info("SearchIndexRebuild - Queued " + worker.Name);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Info("SearchIndexRebuild - Failed to queue: " + e.Message);
                return new RebuildSearchIndexResponse();
            }

            return new RebuildSearchIndexResponse { Queued = true, TaskName = worker.Name };
        }
    }
}
