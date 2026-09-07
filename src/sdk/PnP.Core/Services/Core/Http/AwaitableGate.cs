#nullable enable
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WikiTraccs.Shared.Http
{

    public record RunningRequestInfo(string Uri, DateTime StartTimeUtc, TimeSpan? TimeoutForLogging, IReadOnlyList<string> Tags, Func<IDisposable>? RestoreLogScope = null);

    // written by LLM, 2026-09-04
    // One request backoff at the point where the shared gate accepts it.
    public sealed record PushbackEventInfo(
        long EventId,
        string Service,
        string Source,
        string Target,
        string Status,
        DateTime OccurredUtc,
        int RetryNumber,
        int WaitMilliseconds);

    public class AwaitableGate
    {
        // v============= HEU/LLM: Count requests in one upload flow. ==========
        // written by LLM, 2026-09-04
        // Counts logical request starts and retry events in one async flow. Parallel child tasks
        // share the counters. A nested scope also adds its values to each parent scope.
        public sealed class LogicalRequestCountScope : IDisposable
        {
            private readonly LogicalRequestCountScope? parent;
            private long logicalRequestStartCount;
            private long retryEventCount;
            private int isDisposed;

            internal LogicalRequestCountScope(LogicalRequestCountScope? parent)
            {
                this.parent = parent;
            }

            public long LogicalRequestStartCount => Interlocked.Read(ref logicalRequestStartCount);
            public long RetryEventCount => Interlocked.Read(ref retryEventCount);
            internal LogicalRequestCountScope? Parent => parent;

            internal void AddLogicalRequestStart()
            {
                Interlocked.Increment(ref logicalRequestStartCount);
            }

            internal void AddRetryEvent()
            {
                Interlocked.Increment(ref retryEventCount);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref isDisposed, 1) != 0)
                {
                    return;
                }

                if (ReferenceEquals(currentLogicalRequestCountScope.Value, this))
                {
                    currentLogicalRequestCountScope.Value = parent;
                }
            }
        }
        // ^===================================================================

        // a static gate to apply throttling across all requests - in PnP.Core and PnP.Framework!
        // no pretty solution; should merge with rate limiter to coordinate backing off across all workloads
        public static AwaitableGate MicrosoftInstance { get; private set; } = new() { ServiceName = "Microsoft" };
        // same thing as for Microsoft, but for Atlassian
        public static AwaitableGate AtlassianInstance { get; private set; } = new() { ServiceName = "Atlassian" };
        public const string PushbackLogMarker = "[WTM:SPOPUSHBACK]";
        public string ServiceName { get; init; } = "Unspecified";
        private static Dictionary<long, RunningRequestInfo> RunningRequests = new();
        private static Timer? monitoringTimer;
        public static ILogger? Logger { get; set; }
        // set from the app side; lets us read the caller's log tags
        public static Func<IReadOnlyList<string>>? CurrentTagsProvider { get; set; }
        // v============= HEU/LLM: Capture the caller's log scope for the slow-request log. ==========
        // written by LLM, 2026-09-06
        // Set from the app side. Called in the caller's async flow at request start; the returned
        // function restores that log scope (page id, site, tenant, tags) on any thread later.
        public static Func<Func<IDisposable>?>? LogScopeCaptureProvider { get; set; }
        // A request that runs at least this long is written to the log: while it runs, from the
        // monitoring timer, and when it ends, from the request's own flow.
        public const double SlowRequestSeconds = 10.0;
        // ^===================================================================
        private static HashSet<long> warnedRequests = new();
        private static readonly object monitoringLock = new object();
        // v============= HEU/LLM: Keep the current request-count scope. ==========
        private static readonly AsyncLocal<LogicalRequestCountScope?> currentLogicalRequestCountScope = new();
        // ^===================================================================

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
                    if (elapsed.TotalSeconds >= SlowRequestSeconds)
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
                // written by LLM, 2026-09-06
                // The timer thread has no page id, site, or tenant of its own. The restorer puts the
                // scope of the request start back, so the enrichers add them like for any other line.
                var message = $"[SLOW REQUEST] Request to '{info.Uri}' has been running for {elapsed.TotalSeconds:F1} seconds{(info.TimeoutForLogging.HasValue ? $" (configured timeout is {info.TimeoutForLogging.Value.TotalSeconds:F0}s; but might be more if retrying request)" : "")}";
                WriteInRestoredLogScope(info, message);
            }
        }

        // written by LLM, 2026-05-30
        // Reads the caller's current log tags on the caller's async flow. The slow-request
        // log fires from a detached timer, so we snapshot the tags here to attribute it later.
        // Wrapped so a missing provider or logging-context failure can never break request tracking.
        private static IReadOnlyList<string> TryGetCurrentTags()
        {
            try
            {
                return CurrentTagsProvider?.Invoke() ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // written by LLM, 2026-05-30
        // Builds the "[tag] [tag] " prefix used to attribute a slow-request log to its caller.
        // Returns an empty string on no tags or any failure, so it never disturbs the log call.
        private static string FormatTagPrefix(IReadOnlyList<string>? tags)
        {
            try
            {
                if (null == tags || tags.Count == 0)
                {
                    return "";
                }
                return string.Concat(tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => $"[{tag}] "));
            }
            catch
            {
                return "";
            }
        }

#if DEBUG
        public int? MaxRequestsPerSecond { get; set; } = null;
#else
        public int? MaxRequestsPerSecond { get; set; } = null;
#endif

        private readonly object gateLock = new object();
        private TaskCompletionSource<bool>? tcs;
        private Timer? timer;
        private DateTime releaseTimeUtc= DateTime.UtcNow;
        // next free client-side rate-limit slot; guarded by gateLock just like releaseTimeUtc
        private DateTime nextRateLimitSlotUtc = DateTime.UtcNow;
        public bool IsWaiting => waitCounter > 0;
        public int WaitSecsLeft
        {
            get
            {
                return (int)(releaseTimeUtc - DateTime.UtcNow).TotalSeconds;
            }
        }
        int waitCounter;
        // v============= HEU/LLM: Expose SharePoint pushback state. ==========
        private long pushbackCount;
        // written by LLM, 2026-09-03, 2026-09-04
        // How often a client of this gate had to retry: an HTTP 429, 503, or 504, a timeout,
        // or a dropped connection. A retry with zero wait also counts. Every SharePoint retry path
        // calls SetWaitTime, so this one counter sees all of them. The upload controller reads it.
        public long PushbackCount => Interlocked.Read(ref pushbackCount);
        public DateTime? LastPushbackUtc { get; private set; }
        public PushbackEventInfo? LastPushbackEvent { get; private set; }
        // ^===================================================================

        public AwaitableGate(int initialWaitTimeMilliseconds = Timeout.Infinite)
        {
            if (initialWaitTimeMilliseconds != Timeout.Infinite)
            {
                SetWaitTime(initialWaitTimeMilliseconds);
            }
        }

        // written by LLM
        // Proactive client-side rate limiting that holds under concurrency. Claims the next free
        // send slot under gateLock and returns how many ms the caller must wait for it (0 if free).
        // Each granted request pushes the slot forward by one interval, so parallel callers get
        // spaced slots instead of all firing at once. The slot is floored to now (idle leaves no
        // catch-up debt) and to releaseTimeUtc (a slot never undercuts a 429/503 throttle backoff).
        public int ReserveRateLimitSlotMs()
        {
            var maxRequestsPerSecond = MaxRequestsPerSecond;
            if (null == maxRequestsPerSecond || maxRequestsPerSecond.Value <= 0)
            {
                return 0;
            }

            var slotIntervalMs = 1000.0 / maxRequestsPerSecond.Value;
            lock (gateLock)
            {
                var now = DateTime.UtcNow;
                var earliestSlotUtc = now;
                if (releaseTimeUtc > earliestSlotUtc)
                {
                    earliestSlotUtc = releaseTimeUtc;
                }

                var slotUtc = earliestSlotUtc;
                if (nextRateLimitSlotUtc > earliestSlotUtc)
                {
                    slotUtc = nextRateLimitSlotUtc;
                }

                nextRateLimitSlotUtc = slotUtc + TimeSpan.FromMilliseconds(slotIntervalMs);

                var waitMs = (int)Math.Ceiling((slotUtc - now).TotalMilliseconds);
                if (waitMs < 0)
                {
                    waitMs = 0;
                }
                return waitMs;
            }
        }

        public static long StartRequest(string? uri, TimeSpan? timeoutForLogging)
        {
            if (null == uri)
            {
                return 0;
            }
            // written by LLM, 2026-09-04
            AddLogicalRequestStartToActiveScopes();
            var id = Random.Shared.NextInt64();
            var tags = TryGetCurrentTags();
            // v============= HEU/LLM: Capture the log scope in the caller's flow. ==========
            // written by LLM, 2026-09-06
            var restoreLogScope = TryCaptureLogScope();
            lock (RunningRequests)
            {
                RunningRequests[id] = new(uri, DateTime.UtcNow, timeoutForLogging, tags, restoreLogScope);
            }
            // ^===================================================================
            return id;
        }

        // v============= HEU/LLM: Restore the caller's log scope for a log line from another thread. ==========
        // written by LLM, 2026-09-06
        private static Func<IDisposable>? TryCaptureLogScope()
        {
            try
            {
                return LogScopeCaptureProvider?.Invoke();
            }
            catch
            {
                return null;
            }
        }

        // written by LLM, 2026-09-06
        // Without a restorer the tags go into the message text, like before.
        private static void WriteInRestoredLogScope(RunningRequestInfo info, string message)
        {
            if (null == info.RestoreLogScope)
            {
                Logger?.LogInformation($"{FormatTagPrefix(info.Tags)}{message}");
                return;
            }

            try
            {
                using (info.RestoreLogScope())
                {
                    Logger?.LogInformation(message);
                }
            }
            catch
            {
                Logger?.LogInformation($"{FormatTagPrefix(info.Tags)}{message}");
            }
        }
        // ^===================================================================

        // v============= HEU/LLM: Manage request-count scopes. ==========
        // written by LLM, 2026-09-04
        // Starts a local diagnostic count. It observes existing request boundaries and does not
        // send a request or change retry behavior.
        public static LogicalRequestCountScope StartLogicalRequestCount()
        {
            var scope = new LogicalRequestCountScope(currentLogicalRequestCountScope.Value);
            currentLogicalRequestCountScope.Value = scope;
            return scope;
        }

        // written by LLM, 2026-09-04
        private static void AddLogicalRequestStartToActiveScopes()
        {
            var scope = currentLogicalRequestCountScope.Value;
            while (null != scope)
            {
                scope.AddLogicalRequestStart();
                scope = scope.Parent;
            }
        }

        // written by LLM, 2026-09-04
        private static void AddRetryEventToActiveScopes()
        {
            var scope = currentLogicalRequestCountScope.Value;
            while (null != scope)
            {
                scope.AddRetryEvent();
                scope = scope.Parent;
            }
        }
        // ^===================================================================

        public static bool EndRequest(long id)
        {
            // v============= HEU/LLM: Log a slow request at its end, from the request's own flow. ==========
            // written by LLM, 2026-09-06
            // The end runs in the flow of the request, so this line gets the page id and the site
            // from the enrichers without a restorer. It names the total time, which the timer
            // line cannot.
            RunningRequestInfo? endedInfo;
            bool removed;
            lock (RunningRequests)
            {
                RunningRequests.TryGetValue(id, out endedInfo);
                removed = RunningRequests.Remove(id);
                if (removed)
                {
                    lock (monitoringLock)
                    {
                        warnedRequests.Remove(id);
                    }
                }
            }

            if (removed && null != endedInfo)
            {
                var elapsed = DateTime.UtcNow - endedInfo.StartTimeUtc;
                if (elapsed.TotalSeconds >= SlowRequestSeconds)
                {
                    Logger?.LogInformation($"[SLOW REQUEST] Request to '{endedInfo.Uri}' ended after {elapsed.TotalSeconds:F1} seconds");
                }
            }
            return removed;
            // ^===================================================================
        }

        // v============= HEU/LLM: Accept pushback diagnostic details. ==========
        // written by LLM, 2026-09-04
        // Records one event before the wait is combined with an existing longer wait. Thus one
        // source retry produces one event, also when it does not extend the shared gate wait.
        public void SetWaitTime(
            int waitTimeMilliseconds,
            string source = "Unspecified",
            string? target = null,
            string? status = null,
            int retryNumber = 0)
        // ^===================================================================
        {
            lock (gateLock)
            {
                // v============= HEU/LLM: Record and log this pushback event. ==========
                // written by LLM, 2026-09-03, 2026-09-04
                // Count before the shorter-wait check, so each pushback counts once. The retry
                // number distinguishes a zero-wait retry from an ordinary gate reset.
                var hasRetryMetadata = retryNumber > 0;
                if (waitTimeMilliseconds > 0 || hasRetryMetadata)
                {
                    // written by LLM, 2026-09-04
                    if (hasRetryMetadata)
                    {
                        AddRetryEventToActiveScopes();
                    }
                    var eventId = Interlocked.Increment(ref pushbackCount);
                    var occurredUtc = DateTime.UtcNow;
                    LastPushbackUtc = occurredUtc;
                    LastPushbackEvent = new PushbackEventInfo(
                        eventId,
                        ServiceName,
                        source,
                        target ?? "Unknown",
                        status ?? "Unknown",
                        occurredUtc,
                        retryNumber,
                        waitTimeMilliseconds);
                    if (ServiceName.Equals("Microsoft", StringComparison.Ordinal))
                    {
                        Logger?.LogInformation(
                            "SharePoint pushback event {EventId}: service {Service}; source {Source}; target {Target}; status {Status}; time {OccurredUtc:O}; retry {RetryNumber}; wait {WaitMilliseconds} ms " + PushbackLogMarker,
                            eventId,
                            ServiceName,
                            source,
                            target ?? "Unknown",
                            status ?? "Unknown",
                            occurredUtc,
                            retryNumber,
                            waitTimeMilliseconds);
                    }
                }
                // ^===================================================================

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
            // Confluence REST shape: on-prem paths carry "rest/", cloud carries "api/v2/".
            // Catches on-prem hosts that aren't *.atlassian. Over-matching a stray URL is fine;
            // it just gets throttled, never broken.
            var url = request?.RequestUri?.ToString();
            var looksLikeOnPremRest = url?.Contains("rest/", StringComparison.InvariantCultureIgnoreCase) == true;
            var looksLikeCloudRest = url?.Contains("api/v2/", StringComparison.InvariantCultureIgnoreCase) == true;
            if (looksLikeOnPremRest || looksLikeCloudRest)
            {
                return true;
            }
            return false;
        }

    }
}
