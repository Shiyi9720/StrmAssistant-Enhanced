using System.Reflection;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    internal static class ReflectionTaskHelper
    {
        public static async Task<object> AwaitAsObject(Task task)
        {
            if (task is null) return null;

            await task.ConfigureAwait(false);

            return task.GetType()
                .GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(task);
        }
    }
}
