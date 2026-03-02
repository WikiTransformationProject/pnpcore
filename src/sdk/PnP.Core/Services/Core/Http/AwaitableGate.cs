#nullable enable
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WikiTraccs.Shared.Http
{

    public record RunningRequestInfo(string Uri, DateTime StartTimeUtc, TimeSpan? TimeoutForLogging);
    public class AwaitableGate
    {
        // a static gate to apply throttling across all requests - in PnP.Core and PnP.Framework!
        // no pretty solution; should merge with rate limiter to coordinate backing off across all workloads
        public static AwaitableGate MicrosoftInstance { get; private set; } = new();
        // same thing as for Microsoft, but for Atlassian
        public static AwaitableGate AtlassianInstance { get; private set; } = new();
        private static Dictionary<long, RunningRequestInfo> RunningRequests = new();
        private static Timer? monitoringTimer;
        public static ILogger? Logger { get; set; }
        private static HashSet<long> warnedRequests = new();
        private static readonly object monitoringLock = new object();

        static AwaitableGate()
        {
            SetupRequestMonitoring();
        }

        private static void SetupRequestMonitoring()
        {
            monitoringTimer = new Timer(static (_) =>
            {
                try
                {
                    CheckForLongRunningRequests();
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Request monitoring error: {ex.Message}");
                }
            }, null, 3000, 3000);
        }

        private static void CheckForLongRunningRequests()
        {
            var currentTimeUtc = DateTime.UtcNow;
            var requestsToWarn = new List<(long Id, RunningRequestInfo Info)>();

            lock (RunningRequests)
            {
                foreach (var kvp in RunningRequests)
                {
                    var elapsed = currentTimeUtc - kvp.Value.StartTimeUtc;
                    if (elapsed.TotalSeconds >= 10.0)
                    {
                        lock (monitoringLock)
                        {
                            warnedRequests.Add(kvp.Key);
                            requestsToWarn.Add((kvp.Key, kvp.Value));
                        }
                    }
                }
            }

            foreach (var (_, info) in requestsToWarn)
            {
                var elapsed = currentTimeUtc - info.StartTimeUtc;
                Logger?.LogInformation($"[SLOW REQUEST] Request to '{info.Uri}' has been running for {elapsed.TotalSeconds:F1} seconds{(info.TimeoutForLogging.HasValue ? $" (configured timeout is {info.TimeoutForLogging.Value.TotalSeconds:F0}s; but might be more if retrying request)" : "")}");
            }
        }

        private DateTime lastRequestTimeUtc = DateTime.UtcNow.AddMinutes(-60);
#if DEBUG
        public int? MaxRequestsPerSecond { get; set; } = null;
#else
        public int? MaxRequestsPerSecond { get; set; } = null;
#endif

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

        public int BumpWaitTimeToConfiguredRateLimit()
        {
            if (!MaxRequestsPerSecond.HasValue || MaxRequestsPerSecond.Value <= 0)
            {
                return 0;
            }

            var waitTimeBetweenRequestsMs = 1000.0 / MaxRequestsPerSecond.Value;
            var alreadyPassedWaitTimeSinceLastRequest = (DateTime.UtcNow - lastRequestTimeUtc).TotalMilliseconds;
            var waitTimeLeftMs = (int)Math.Ceiling(waitTimeBetweenRequestsMs - alreadyPassedWaitTimeSinceLastRequest);
            if (waitTimeLeftMs < 0 || DateTime.UtcNow + TimeSpan.FromMilliseconds(waitTimeLeftMs) <= releaseTimeUtc)
            {
                // already waiting long enough? fine, nothing to do
                return 0;
            } 
            // otherwise: wait
            if (waitTimeLeftMs > 0)
            {
                if (waitTimeLeftMs > 1000)
                {
                    Debugger.Break();
                }
                SetWaitTime(waitTimeLeftMs);
                return waitTimeLeftMs;
            }
            return 0;
        }

        public static long StartRequest(string? uri, TimeSpan? timeoutForLogging)
        {
            if (null == uri)
            {
                return 0;
            }
            var id = Random.Shared.NextInt64();
            lock (RunningRequests)
            {
                RunningRequests[id] = new(uri, DateTime.UtcNow, timeoutForLogging);
            }
            return id;
        }

        public static bool EndRequest(long id)
        {
            lock (RunningRequests)
            {
                var removed = RunningRequests.Remove(id);
                if (removed)
                {
                    lock (monitoringLock)
                    {
                        warnedRequests.Remove(id);
                    }
                }
                return removed;
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

        public void RegisterNowAsLastRequestTime()
        {
            lastRequestTimeUtc = DateTime.UtcNow;
        }

        public bool IsMicrosoftEndpoint(HttpRequestMessage? request)
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

        public bool IsAtlassianEndpoint(HttpRequestMessage? request)
        {
            if (request?.RequestUri?.Host?.Contains(".atlassian", StringComparison.InvariantCultureIgnoreCase) == true)
            {
                return true;
            }
            // heu note: implicit knowledge: we add this header
            if (request?.Headers.NonValidated.Contains("X-Atlassian-Token") == true)
            {
                return true;
            }
            return false;
        }

    }
}
