using Microsoft.Extensions.Logging.Abstractions;
using SIP2Agent.UserAgentService.Service;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorceryMedia.Abstractions;
using System.Net;
using Xunit;

namespace SIP2Agent.UserAgentService.Tests;

public sealed class InboundCallRegistryTests
{
    [Fact]
    public async Task RegistryRejectsDuplicatesRemovesExactEntryAndRetainsWorkflowTask()
    {
        using SIPTransport transport = CreateTransport();
        InboundCallRegistry registry = new(NullLogger.Instance);
        PendingCallAgent firstAgent = new();
        PendingCallAgent duplicateAgent = new();
        CallSession first = CreateCall(transport, "same-call-id", firstAgent);
        CallSession duplicate = CreateCall(transport, "same-call-id", duplicateAgent);

        Assert.True(registry.TryStart(first, NullLogger.Instance, out Task? firstRun));
        Assert.NotNull(firstRun);
        Assert.Single(registry.Snapshot());
        Assert.Same(firstRun, registry.Snapshot()[0].RunTask);

        Assert.False(registry.TryStart(duplicate, NullLogger.Instance, out Task? rejectedRun));
        Assert.Null(rejectedRun);
        await duplicate.StopAsync("duplicate rejected", CancellationToken.None);

        first.RequestStop(CallTerminationReason.RemoteCancellation);
        await firstRun!;
        Assert.Equal(0, registry.Count);

        PendingCallAgent replacementAgent = new();
        CallSession replacement = CreateCall(transport, "same-call-id", replacementAgent);
        Assert.True(registry.TryStart(replacement, NullLogger.Instance, out Task? replacementRun));
        replacement.RequestStop(CallTerminationReason.RemoteCancellation);
        await replacementRun!;
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task StopAcceptingAtomicallyRejectsNewAdmissionAndReturnsTrackedSnapshot()
    {
        using SIPTransport transport = CreateTransport();
        InboundCallRegistry registry = new(NullLogger.Instance);
        PendingCallAgent activeAgent = new();
        CallSession active = CreateCall(transport, "active-call", activeAgent);
        Assert.True(registry.TryStart(active, NullLogger.Instance, out Task? activeRun));

        TrackedInboundCall tracked = Assert.Single(registry.StopAcceptingAndSnapshot());
        Assert.Same(active, tracked.Session);
        Assert.Same(activeRun, tracked.RunTask);

        PendingCallAgent rejectedAgent = new();
        CallSession rejected = CreateCall(transport, "new-call", rejectedAgent);
        Assert.False(registry.TryStart(rejected, NullLogger.Instance, out _));
        await rejected.StopAsync("admission stopped", CancellationToken.None);

        active.RequestStop(CallTerminationReason.ApplicationShutdown);
        await activeRun!;
        Assert.Equal(0, registry.Count);
    }

    private static SIPTransport CreateTransport()
    {
        SIPTransport transport = new();
        transport.AddSIPChannel(new SIPUDPChannel(IPAddress.Loopback, 0));
        return transport;
    }

    private static CallSession CreateCall(
        SIPTransport transport,
        string callId,
        PendingCallAgent agent)
    {
        SIPRequest request = SIPRequest.GetRequest(
            SIPMethodsEnum.INVITE,
            SIPURI.ParseSIPURI("sip:agent@127.0.0.1"));
        request.Header.CallId = callId;

        return CallSession.CreateInbound(
            transport,
            NullLogger.Instance,
            request,
            () => agent,
            publicAnswerAddress: null,
            CancellationToken.None);
    }

    private sealed class PendingCallAgent : ICallAgent
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal PendingCallAgent()
        {
            MediaSession = new VoIPMediaSession(new MediaEndPoints());
        }

        public VoIPMediaSession MediaSession { get; }

        public Task Completion => _completion.Task;

        public Task PrepareAsync(CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public Task StartAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("The pending test agent must not start.");

        public Task StopAsync(string reason, CancellationToken cancellationToken)
        {
            MediaSession.Close(reason);
            MediaSession.Dispose();
            _completion.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
