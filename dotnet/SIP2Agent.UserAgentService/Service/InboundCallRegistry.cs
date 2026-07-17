using Microsoft.Extensions.Logging;

namespace SIP2Agent.UserAgentService.Service;

internal sealed record TrackedInboundCall(CallSession Session, Task RunTask);

internal sealed class InboundCallRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackedInboundCall> _calls = new();
    private readonly ILogger _logger;
    private bool _accepting = true;

    internal InboundCallRegistry(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count;
            }
        }
    }

    internal bool TryStart(CallSession session, ILogger runnerLogger, out Task? runTask)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(runnerLogger);

        var startGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TrackedInboundCall? tracked = null;
        Task workflow = RunTrackedAsync(session, runnerLogger, startGate.Task, () => tracked!);
        tracked = new TrackedInboundCall(session, workflow);

        lock (_gate)
        {
            if (!_accepting || _calls.ContainsKey(session.CallId))
            {
                startGate.TrySetCanceled();
                runTask = null;
                return false;
            }

            _calls.Add(session.CallId, tracked);
        }

        startGate.TrySetResult();
        runTask = workflow;
        return true;
    }

    internal bool TryGet(string? callId, out CallSession? session)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(callId) &&
                _calls.TryGetValue(callId, out TrackedInboundCall? tracked))
            {
                session = tracked.Session;
                return true;
            }
        }

        session = null;
        return false;
    }

    internal TrackedInboundCall[] Snapshot()
    {
        lock (_gate)
        {
            return _calls.Values.ToArray();
        }
    }

    internal CallSession? GetOldest()
    {
        lock (_gate)
        {
            return _calls.Values
                .Select(call => call.Session)
                .OrderBy(call => call.Inserted)
                .FirstOrDefault();
        }
    }

    internal TrackedInboundCall[] StopAcceptingAndSnapshot()
    {
        lock (_gate)
        {
            _accepting = false;
            return _calls.Values.ToArray();
        }
    }

    internal void ListCalls()
    {
        CallSession[] calls = Snapshot()
            .Select(call => call.Session)
            .OrderBy(call => call.Inserted)
            .ToArray();
        if (calls.Length == 0)
        {
            _logger.LogInformation("There are no active calls.");
            return;
        }

        _logger.LogInformation("Current call list:");
        foreach (CallSession call in calls)
        {
            TimeSpan duration = DateTimeOffset.Now - call.Inserted;
            _logger.LogInformation(
                "{CallId}: {Remote} {Duration}s",
                call.CallId,
                call.RemoteDescription,
                Convert.ToInt32(duration.TotalSeconds));
        }
    }

    private async Task RunTrackedAsync(
        CallSession session,
        ILogger runnerLogger,
        Task startGate,
        Func<TrackedInboundCall> trackedCall)
    {
        try
        {
            await startGate.ConfigureAwait(false);
            await InboundCallRunner.RunAsync(session, runnerLogger).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (startGate.IsCanceled)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Call workflow for {CallId} failed.", session.CallId);
        }
        finally
        {
            if (startGate.IsCompletedSuccessfully)
            {
                TrackedInboundCall expected = trackedCall();
                lock (_gate)
                {
                    if (_calls.TryGetValue(session.CallId, out TrackedInboundCall? current) &&
                        ReferenceEquals(current, expected))
                    {
                        _calls.Remove(session.CallId);
                    }
                }
            }
        }
    }
}
