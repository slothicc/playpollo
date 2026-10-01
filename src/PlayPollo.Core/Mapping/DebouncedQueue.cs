using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayPollo.Mapping
{
    /// <summary>
    /// Collects items and hands them over in one batch after a quiet period, so a burst of metadata edits
    /// results in one push per game. Batches never overlap.
    /// </summary>
    public class DebouncedQueue<T> : IDisposable
    {
        private readonly object gate = new object();
        private readonly TimeSpan delay;
        private readonly Func<IReadOnlyList<T>, Task> handler;
        private readonly HashSet<T> pending = new HashSet<T>();
        private readonly Timer timer;
        private bool running;
        private bool disposed;

        public DebouncedQueue(TimeSpan delay, Func<IReadOnlyList<T>, Task> handler)
        {
            this.delay = delay;
            this.handler = handler;
            timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Enqueue(T item)
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                pending.Add(item);
                timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        private void Flush()
        {
            List<T> batch;
            lock (gate)
            {
                if (disposed || pending.Count == 0)
                {
                    return;
                }

                if (running)
                {
                    // A batch is still in flight; try again once it's had time to finish.
                    timer.Change(delay, Timeout.InfiniteTimeSpan);
                    return;
                }

                running = true;
                batch = new List<T>(pending);
                pending.Clear();
            }

            try
            {
                handler(batch).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // The handler is responsible for reporting its own failures.
            }
            finally
            {
                lock (gate)
                {
                    running = false;
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                pending.Clear();
            }

            timer.Dispose();
        }
    }
}
