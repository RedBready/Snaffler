using System;
using System.Threading;
using System.Threading.Tasks;

namespace SnaffCore.Concurrency
{
    public static class TimeoutHelper
    {
        public static T RunWithTimeout<T>(Func<T> func, int timeoutMs)
        {
            var task = Task.Run(func);
            if (!task.Wait(timeoutMs))
                throw new TimeoutException("Operation timed out after " + timeoutMs + "ms");
            if (task.IsFaulted && task.Exception != null)
                throw task.Exception.InnerException ?? task.Exception;
            return task.Result;
        }

        public static void RunWithTimeout(Action action, int timeoutMs)
        {
            var task = Task.Run(action);
            if (!task.Wait(timeoutMs))
                throw new TimeoutException("Operation timed out after " + timeoutMs + "ms");
            if (task.IsFaulted && task.Exception != null)
                throw task.Exception.InnerException ?? task.Exception;
        }
    }
}
