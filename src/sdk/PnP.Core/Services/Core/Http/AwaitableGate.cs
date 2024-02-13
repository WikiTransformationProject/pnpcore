#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WikiTraccs.Shared.Http
{
    public class AwaitableGate
    {
        // a static gate to apply throttling across all requests - in PnP.Core and PnP.Framework!
        // no pretty solution; should merge with rate limiter to coordinate backing off across all workloads
        public static AwaitableGate Instance { get; private set; } = new();

        private readonly object gateLock = new object();
        private TaskCompletionSource<bool>? tcs;
        private Timer? timer;
        private DateTime releaseTimeUtc= DateTime.UtcNow;
        public bool IsWaiting => waitCounter > 0;
        public int WaitSecsLeft
        {
            get
            {
                return (int)(releaseTimeUtc - DateTime.UtcNow).TotalSeconds;
            }
        }
        int waitCounter;

        public AwaitableGate(int initialWaitTimeMilliseconds = Timeout.Infinite)
        {
            if (initialWaitTimeMilliseconds != Timeout.Infinite)
            {
                SetWaitTime(initialWaitTimeMilliseconds);
            }
        }

        public void SetWaitTime(int waitTimeMilliseconds)
        {
            lock (gateLock)
            {
                if (DateTime.UtcNow + TimeSpan.FromMilliseconds(waitTimeMilliseconds) < releaseTimeUtc)
                {
                    // less wait time? don't accept, wait the maximum
                    return;
                }
                releaseTimeUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(waitTimeMilliseconds);

                timer?.Dispose();

                if (waitTimeMilliseconds != Timeout.Infinite && waitTimeMilliseconds > 0)
                {
                    if (null == tcs)
                    {
                        tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    timer = new Timer(state =>
                    {
                        lock (gateLock)
                        {
                            if (null != tcs && !tcs.Task.IsCompleted)
                            {
                                tcs?.TrySetResult(true);
                                // need new tcs for new wait
                                tcs = null;
                            }
                        }
                    }, null, waitTimeMilliseconds, Timeout.Infinite);
                } else
                {
                    tcs = null;
                }
            }
        }

        public void Cancel()
        {
            lock (gateLock)
            {
                timer?.Dispose();
                tcs?.TrySetCanceled();
                tcs = null;
            }
        }

        public async Task WaitAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool>? tcsCopy;

            lock (gateLock)
            {
                // currently nothing to wait for
                if (null == tcs)
                {
                    return;
                }
                // completed? also good, nothing to wait
                if (tcs.Task.IsCompleted)
                {
                    return;
                }

                tcsCopy = tcs;
            }

            try
            {
                Interlocked.Increment(ref waitCounter);
                await Task.WhenAny(tcsCopy.Task, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                // after a completed wait - canceled or not - we need a new tcs
                tcs = null;
                Interlocked.Decrement(ref waitCounter);
            }
        }

        public void Release()
        {
            lock (gateLock)
            {
                if (null != tcs && !tcs.Task.IsCompleted)
                {
                    tcs?.TrySetResult(true);
                    tcs = null;
                }
            }
        }

        public static bool IsMicrosoftEndpoint(HttpRequestMessage? request)
        {
            if (request?.RequestUri?.Host?.Contains(".sharepoint", StringComparison.InvariantCultureIgnoreCase) == true)
            {
                return true;
            }
            if (request?.RequestUri?.Host?.Contains("graph.microsoft", StringComparison.InvariantCultureIgnoreCase) == true)
            {
                return true;
            }

            return false;
        }
    }
}
