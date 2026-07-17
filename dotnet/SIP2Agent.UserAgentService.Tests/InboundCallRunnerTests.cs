using Microsoft.Extensions.Logging.Abstractions;
using SIP2Agent.UserAgentService.Service;
using SIPSorcery.SIP;
using Xunit;

namespace SIP2Agent.UserAgentService.Tests;

public sealed class InboundCallRunnerTests
{
    [Fact]
    public async Task RunAsync_PreparesThenAnswersThenStartsAgent()
    {
        FakeInboundCall call = new();
        Task runTask = InboundCallRunner.RunAsync(call, NullLogger.Instance);

        Assert.Equal(["prepare"], call.Operations);
        Assert.Equal(0, call.AnswerCount);

        call.CompletePreparation();
        await call.AgentStarted;
        Assert.Equal(["prepare", "answer", "start"], call.Operations);

        call.SignalTermination(CallTerminationReason.RemoteHangup, callRemainsActive: false);
        await runTask;
        Assert.Equal(1, call.StopCount);
    }

    [Fact]
    public async Task RunAsync_CancellationBeforeReadyNeverAnswersOrRejects()
    {
        FakeInboundCall call = new();
        Task runTask = InboundCallRunner.RunAsync(call, NullLogger.Instance);

        call.SignalTermination(
            CallTerminationReason.RemoteCancellation,
            callRemainsActive: false);
        await runTask;

        Assert.Equal(0, call.AnswerCount);
        Assert.Equal(0, call.RejectCount);
        Assert.Equal(1, call.StopCount);
    }

    [Theory]
    [InlineData(true, SIPResponseStatusCodesEnum.InternalServerError, "Internal Server Error")]
    [InlineData(false, SIPResponseStatusCodesEnum.ServiceUnavailable, "Service Unavailable")]
    public async Task RunAsync_MapsPreparationFailureToGenericSipStatus(
        bool configurationFailure,
        SIPResponseStatusCodesEnum expectedStatus,
        string expectedReason)
    {
        AgentPreparationFailureKind kind = configurationFailure
            ? AgentPreparationFailureKind.Configuration
            : AgentPreparationFailureKind.ProviderUnavailable;
        FakeInboundCall call = new();
        Task runTask = InboundCallRunner.RunAsync(call, NullLogger.Instance);

        call.FailPreparation(new AgentPreparationException(kind, "internal detail"));
        await runTask;

        Assert.Equal(expectedStatus, call.RejectedStatus);
        Assert.Equal(expectedReason, call.RejectedReason);
        Assert.Equal(1, call.RejectCount);
        Assert.Equal(1, call.StopCount);
    }

    [Fact]
    public async Task RunAsync_AgentFailureAfterAnswerHangsUp()
    {
        FakeInboundCall call = new();
        Task runTask = InboundCallRunner.RunAsync(call, NullLogger.Instance);
        call.CompletePreparation();
        await call.AgentStarted;

        call.FailAgent(new InvalidOperationException("provider ended"));
        await runTask;

        Assert.True(call.Answered);
        Assert.Equal(1, call.HangupCount);
        Assert.Equal(0, call.RejectCount);
        Assert.Equal(1, call.StopCount);
    }

    [Fact]
    public async Task RunAsync_SimultaneousTerminationAndAgentFailureStopsOnce()
    {
        FakeInboundCall call = new();
        Task runTask = InboundCallRunner.RunAsync(call, NullLogger.Instance);
        call.CompletePreparation();
        await call.AgentStarted;

        call.SignalTermination(CallTerminationReason.RemoteHangup, callRemainsActive: false);
        call.FailAgent(new InvalidOperationException("simultaneous provider failure"));
        await runTask;

        Assert.Equal(1, call.StopCount);
        Assert.Equal(0, call.RejectCount);
    }

    [Fact]
    public async Task RunAsync_PreservesApplicationShutdownReason()
    {
        FakeInboundCall call = new();
        Task runTask = InboundCallRunner.RunAsync(call, NullLogger.Instance);
        call.SignalTermination(
            CallTerminationReason.ApplicationShutdown,
            callRemainsActive: false);

        await runTask;
        Assert.Equal(CallTerminationReason.ApplicationShutdown, await call.Termination);
    }

    private sealed class FakeInboundCall : IInboundCall
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _preparation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CallTerminationReason> _termination =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _agentCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _agentStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string CallId => "test-call";
        public bool Answered { get; private set; }
        public CancellationToken CancellationToken => _cancellation.Token;
        public Task<CallTerminationReason> Termination => _termination.Task;
        public Task AgentCompletion => _agentCompletion.Task;
        public bool IsCallActive { get; private set; }
        public Task AgentStarted => _agentStarted.Task;
        public List<string> Operations { get; } = [];
        public int AnswerCount { get; private set; }
        public int RejectCount { get; private set; }
        public int HangupCount { get; private set; }
        public int StopCount { get; private set; }
        public SIPResponseStatusCodesEnum? RejectedStatus { get; private set; }
        public string? RejectedReason { get; private set; }

        public Task PrepareAgentAsync()
        {
            Operations.Add("prepare");
            return _preparation.Task.WaitAsync(_cancellation.Token);
        }

        public Task<bool> AnswerAsync()
        {
            Operations.Add("answer");
            AnswerCount++;
            Answered = true;
            IsCallActive = true;
            return Task.FromResult(true);
        }

        public Task StartAgentAsync()
        {
            Operations.Add("start");
            _agentStarted.TrySetResult();
            return Task.CompletedTask;
        }

        public void Reject(SIPResponseStatusCodesEnum status, string reason)
        {
            RejectCount++;
            RejectedStatus = status;
            RejectedReason = reason;
        }

        public void Hangup()
        {
            HangupCount++;
            IsCallActive = false;
        }

        public Task StopAsync(string reason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            Operations.Add("stop");
            return Task.CompletedTask;
        }

        public void CompletePreparation() => _preparation.TrySetResult();

        public void FailPreparation(Exception exception)
            => _preparation.TrySetException(exception);

        public void FailAgent(Exception exception)
            => _agentCompletion.TrySetException(exception);

        public void SignalTermination(
            CallTerminationReason reason,
            bool callRemainsActive)
        {
            if (_termination.TrySetResult(reason))
            {
                IsCallActive = callRemainsActive;
                _cancellation.Cancel();
            }
        }
    }
}
