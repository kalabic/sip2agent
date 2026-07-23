using AudioFormatLib.IO;
using DotBase.Event;
using LibRTIC.Config;
using LibRTIC.Conversation;
using LibRTIC.MiniTaskLib;
using LibRTIC.MiniTaskLib.Base;
using LibRTIC.MiniTaskLib.Events;
using Microsoft.Extensions.Logging.Abstractions;
using MiniRTICallServer.RTISorcery;
using SIP2Agent.UserAgentService.Service;
using Xunit;

namespace MiniRTICallServer.Tests.RTISorcery;

public sealed class RTIConversationSessionTests
{
    [Fact]
    public void MediaUpdates_PreserveIdentityPcmBytesAndOrder()
    {
        byte[] pcm = [0x01, 0x02, 0x03, 0x04];
        RecordingConversation conversation = new();
        using RTIConversationSession session = new(conversation);
        List<RealtimeAgentMediaUpdate> updates = [];
        session.MediaUpdate += updates.Add;

        conversation.Raise<ConversationItemStreamingAudioPartDelta>(new TestAudioDelta(
            "response-7",
            "item-9",
            3,
            new BinaryData(pcm)));
        conversation.Raise<ConversationItemStreamingAudioFinished>(
            new TestAudioFinished("response-7", "item-9", 3));
        conversation.Raise(new ConversationInputSpeechStarted());

        RealtimeOutputAudioDelta delta = Assert.IsType<RealtimeOutputAudioDelta>(updates[0]);
        RealtimeOutputAudioFinished finished =
            Assert.IsType<RealtimeOutputAudioFinished>(updates[1]);
        Assert.IsType<RealtimeInputSpeechStarted>(updates[2]);
        Assert.Equal(new RealtimeOutputIdentity("response-7", "item-9", 3), delta.Identity);
        Assert.Equal(pcm, delta.Pcm16LittleEndian.ToArray());
        Assert.Equal(delta.Identity, finished.Identity);
    }

    [Fact]
    public async Task Commands_ForwardDirectlyToConversation()
    {
        RecordingConversation conversation = new();
        using RTIConversationSession session = new(conversation);
        using CancellationTokenSource cancellation = new();

        await session.StartResponseAsync("hello", cancellation.Token);
        await session.InterruptResponseAsync(cancellation.Token);
        await session.TruncateOutputItemAsync(
            "item-4",
            2,
            TimeSpan.FromMilliseconds(340),
            cancellation.Token);
        session.Cancel();

        Assert.Equal(("hello", cancellation.Token), conversation.Response);
        Assert.Equal(cancellation.Token, conversation.InterruptToken);
        Assert.Equal(
            ("item-4", 2, TimeSpan.FromMilliseconds(340), cancellation.Token),
            conversation.Truncation);
        Assert.Equal(1, conversation.CancelCount);
    }

    [Fact]
    public async Task Ready_CompletesOnlyOnConfigured()
    {
        RecordingConversation conversation = new();
        using RTIConversationSession session = new(conversation);

        conversation.Raise(new ConversationSessionStarted());
        Assert.False(session.Ready.IsCompleted);

        conversation.Raise(new ConversationSessionConfigured());
        await session.Ready;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectionFailureFaultsReadinessWithSharedCategory(bool configurationFailure)
    {
        RecordingConversation conversation = new();
        using RTIConversationSession session = new(conversation);
        FailedToConnect.ErrorStatus status = configurationFailure
            ? FailedToConnect.ErrorStatus.EndpointOptionsMissing
            : FailedToConnect.ErrorStatus.ServerDidNotRespond;

        conversation.Raise(new FailedToConnect(status, "provider unavailable"));

        AgentPreparationException exception =
            await Assert.ThrowsAsync<AgentPreparationException>(() => session.Ready);
        Assert.Equal(
            configurationFailure
                ? AgentPreparationFailureKind.Configuration
                : AgentPreparationFailureKind.ProviderUnavailable,
            exception.FailureKind);
    }

    [Fact]
    public async Task TaskFailureFaultsReadyAndCannotBeHiddenBySuccessfulRunCompletion()
    {
        RecordingConversation conversation = new();
        using RTIConversationSession session = new(conversation);
        Task runTask = session.RunAsync();

        conversation.Raise(new TaskExceptionOccured(
            new InvalidOperationException("provider sender failed")));
        conversation.CompleteRun();

        AgentPreparationException preparation =
            await Assert.ThrowsAsync<AgentPreparationException>(() => session.Ready);
        Assert.Equal(AgentPreparationFailureKind.ProviderUnavailable, preparation.FailureKind);
        AgentPreparationException runFailure =
            await Assert.ThrowsAsync<AgentPreparationException>(() => runTask);
        Assert.Equal("provider sender failed", runFailure.InnerException?.Message);
    }

    [Fact]
    public async Task DirectRunFailureAlsoFaultsReadinessImmediately()
    {
        RecordingConversation conversation = new();
        using RTIConversationSession session = new(conversation);
        Task runTask = session.RunAsync();

        conversation.FailRun(new InvalidOperationException("websocket failed"));

        AgentPreparationException readinessFailure =
            await Assert.ThrowsAsync<AgentPreparationException>(() => session.Ready);
        AgentPreparationException runFailure =
            await Assert.ThrowsAsync<AgentPreparationException>(() => runTask);
        Assert.Same(readinessFailure, runFailure);
        Assert.Equal("websocket failed", runFailure.InnerException?.Message);
    }

    [Fact]
    public async Task Cancel_AllowsOrderlyRunCompletion()
    {
        RecordingConversation conversation = new();
        using RTIConversationSession session = new(conversation);
        Task firstRun = session.RunAsync();
        Task secondRun = session.RunAsync();

        session.Cancel();

        await Task.WhenAll(firstRun, secondRun);
        Assert.Equal(1, conversation.RunCount);
    }

    private sealed record TestAudioDelta(
        string ResponseId,
        string ItemId,
        int ContentIndex,
        BinaryData Audio) : ConversationItemStreamingAudioPartDelta;

    private sealed record TestAudioFinished(
        string ResponseId,
        string ItemId,
        int ContentIndex) : ConversationItemStreamingAudioFinished;

    private sealed class RecordingConversation : RTIConversation
    {
        private readonly RTIConversation _eventHost = RTIConversationTask.Create(
            new MicrosoftInfoLog(NullLogger.Instance),
            CancellationToken.None);
        private readonly TaskCompletionSource _run =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override EventProducerCollection ReceiverEvents => _eventHost.ReceiverEvents;
        public override EventQueue ConversationEvents => _eventHost.ConversationEvents;
        public int RunCount { get; private set; }
        public (string? Instructions, CancellationToken CancellationToken)? Response { get; private set; }
        public CancellationToken InterruptToken { get; private set; }
        public (string ItemId, int ContentIndex, TimeSpan AudioEndTime, CancellationToken CancellationToken)?
            Truncation { get; private set; }
        public int CancelCount { get; private set; }

        public void Raise<T>(T update) where T : class
            => ConversationEvents.Invoke(update);

        public void CompleteRun() => _run.TrySetResult();

        public void FailRun(Exception exception) => _run.TrySetException(exception);

        public override void ConfigureWith(
            RTICConfig options,
            IPcm16FrameOutput audioOutputFrames)
            => throw new NotSupportedException();

        public override void Run() => throw new NotSupportedException();

        public override Task RunAsync()
        {
            RunCount++;
            return _run.Task;
        }

        public override Task StartResponseAsync(
            string? instructions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Response = (instructions, cancellationToken);
            return Task.CompletedTask;
        }

        public override Task InterruptResponseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InterruptToken = cancellationToken;
            return Task.CompletedTask;
        }

        public override Task TruncateOutputItemAsync(
            string itemId,
            int contentIndex,
            TimeSpan audioEndTime,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Truncation = (itemId, contentIndex, audioEndTime, cancellationToken);
            return Task.CompletedTask;
        }

        public override TaskWithEvents? GetAwaiter() => null;

        public override void Cancel()
        {
            CancelCount++;
            _run.TrySetResult();
        }

        public override List<TaskWithEvents> GetTaskList() => [];

        public override void Await()
        {
        }

        public override Task AwaitAsync(CancellationToken finalCancellation)
            => Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _eventHost.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
