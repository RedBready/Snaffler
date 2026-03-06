using System;
using System.Threading;

namespace SnaffCore.Concurrency
{
    public static class TimeoutHelper
    {
        public static T RunWithTimeout<T>(Func<T> func, int timeoutMs)
        {
            T result = default(T);
            Exception caught = null;
            var completed = new ManualResetEventSlim(false);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { result = func(); }
                catch (Exception e) { caught = e; }
                finally { completed.Set(); }
            });

            if (!completed.Wait(timeoutMs))
                throw new TimeoutException("Operation timed out after " + timeoutMs + "ms");

            if (caught != null)
                throw caught;
            return result;
        }

        public static void RunWithTimeout(Action action, int timeoutMs)
        {
            Exception caught = null;
            var completed = new ManualResetEventSlim(false);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { action(); }
                catch (Exception e) { caught = e; }
                finally { completed.Set(); }
            });

            if (!completed.Wait(timeoutMs))
                throw new TimeoutException("Operation timed out after " + timeoutMs + "ms");

            if (caught != null)
                throw caught;
        }
    }
}
