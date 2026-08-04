using DotBase.Event;
using LibRTIC.Conversation;
using LibRTIC.MiniTaskLib;
using LibRTIC.MiniTaskLib.Events;
using Microsoft.Extensions.Logging.Abstractions;
using SIP2Agent.UserAgentService.Integration.LibRTIC;
using SIP2Agent.UserAgentService.Service;
using Xunit;

namespace SIP2Agent.UserAgentService.Tests.Integration.LibRTIC;

public sealed class LibRTICConversationSessionAdapterTests
{
    [Fact]
    public void MediaUpdates_PreserveIdentityPcmBytesAndMailboxOrder()
    {
        byte[] pcm = [0x01, 0x02, 0x03, 0x04];
        RecordingConversation conversation = new();
        using LibRTICConversationSessionAdapter session = new(conversation);
        List<RealtimeAgentMediaUpdate> updates = [];
        session.MediaUpdate += updates.Add;

        conversation.RaiseUpdate(
            new RTICOutputAudioDelta(
                "response-7",
                "item-9",
                0,
                3,
                global::LibRTIC.Realtime.RealtimeAudioContract.CreatePacket(pcm)));
        conversation.RaiseUpdate(
            new RTICOutputAudioCompleted("response-7", "item-9", 0, 3));
        conversation.RaiseUpdate(
            new RTICInputSpeechStarted("input-item", TimeSpan.Zero));

        RealtimeOutputAudioDelta delta = Assert.IsType<RealtimeOutputAudioDelta>(updates[0]);
        RealtimeOutputAudioFinished finished = Assert.IsType<RealtimeOutputAudioFinished>(updates[1]);
        Assert.IsType<RealtimeInputSpeechStarted>(updates[2]);
        Assert.Equal(
            new RealtimeOutputIdentity("response-7", "item-9", 0, 3),
            delta.Identity);
        Assert.Equal(pcm, delta.Pcm16LittleEndian.ToArray());
        Assert.Equal(delta.Identity, finished.Identity);
    }

    [Fact]
    public async Task Commands_MapToComposedConversationControl()
    {
        RecordingConversation conversation = new();
        using LibRTICConversationSessionAdapter session = new(conversation);
        using CancellationTokenSource cancellation = new();
        RealtimeOutputIdentity identity = new("response-3", "item-4", 1, 2);

        await session.StartResponseAsync("hello", cancellation.Token);
        await session.InterruptOutputAsync(
            identity,
            TimeSpan.FromMilliseconds(340),
            cancelResponseIfActive: true,
            cancellation.Token);
        session.Cancel();

        Assert.Equal(("hello", cancellation.Token), conversation.Response);
        Assert.Equal(
            (identity, TimeSpan.FromMilliseconds(340), true, cancellation.Token),
            conversation.Interruption);
        Assert.Equal(1, conversation.CancelCount);
    }

    [Fact]
    public async Task Ready_CompletesOnlyOnConfigured()
    {
        RecordingConversation conversation = new();
        using LibRTICConversationSessionAdapter session = new(conversation);

        conversation.RaiseUpdate(new RTICSessionCreated(SessionInfo()));
        Assert.False(session.Ready.IsCompleted);
        conversation.RaiseUpdate(new RTICSessionConfigured(SessionInfo()));

        await session.Ready;
    }

    [Theory]
    [InlineData(FailedToConnectMsg.ErrorStatus.EndpointOptionsMissing, true)]
    [InlineData(FailedToConnectMsg.ErrorStatus.ServerDidNotRespond, false)]
    public async Task ConnectionFailure_FaultsReadinessWithExpectedCategory(
        FailedToConnectMsg.ErrorStatus status,
        bool isConfigurationFailure)
    {
        RecordingConversation conversation = new();
        using LibRTICConversationSessionAdapter session = new(conversation);

        conversation.RaiseConversationEvent(
            new FailedToConnectMsg(status, "provider unavailable"));

        AgentPreparationException exception =
            await Assert.ThrowsAsync<AgentPreparationException>(() => session.Ready);
        Assert.Equal(
            isConfigurationFailure
                ? AgentPreparationFailureKind.Configuration
                : AgentPreparationFailureKind.ProviderUnavailable,
            exception.FailureKind);
    }

    [Fact]
    public async Task ProviderFailureBeforeReady_FaultsReadinessAndRun()
    {
        RecordingConversation conversation = new();
        using LibRTICConversationSessionAdapter session = new(conversation);
        Task runTask = session.RunAsync();

        conversation.RaiseConversationEvent(
            new TaskExceptionOccured(new InvalidOperationException("provider sender failed")));
        conversation.CompleteRun();

        AgentPreparationException readiness =
            await Assert.ThrowsAsync<AgentPreparationException>(() => session.Ready);
        AgentPreparationException runFailure =
            await Assert.ThrowsAsync<AgentPreparationException>(() => runTask);
        Assert.Equal(AgentPreparationFailureKind.ProviderUnavailable, readiness.FailureKind);
        Assert.Equal("provider sender failed", runFailure.InnerException?.Message);
    }

    [Fact]
    public async Task Cancel_AllowsOrderlyRunCompletionAndIsIdempotent()
    {
        RecordingConversation conversation = new();
        using LibRTICConversationSessionAdapter session = new(conversation);
        Task firstRun = session.RunAsync();
        Task secondRun = session.RunAsync();

        session.Cancel();
        session.Cancel();

        await Task.WhenAll(firstRun, secondRun);
        Assert.Equal(1, conversation.RunCount);
        Assert.Equal(2, conversation.CancelCount);
    }

    private static RTICSessionInfo SessionInfo()
        => new(null, null, [], null);

    private sealed class RecordingConversation : ILibRTICConversation
    {
        private readonly RTIConversation _eventHost = RTIConversationTask.Create(
            new MicrosoftInfoLogAdapter(NullLogger.Instance),
            CancellationToken.None);
        private readonly TaskCompletionSource _run =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EventProducerCollection ConversationEvents =>
            _eventHost.ConversationEvents;
        public EventQueue UpdatesReceiverEvents =>
            _eventHost.UpdatesReceiverEvents;
        public int RunCount { get; private set; }
        public (string? Instructions, CancellationToken CancellationToken)? Response { get; private set; }
        public (
            RealtimeOutputIdentity Identity,
            TimeSpan PlayedThrough,
            bool CancelResponseIfActive,
            CancellationToken CancellationToken)? Interruption { get; private set; }
        public int CancelCount { get; private set; }

        public void RaiseConversationEvent<T>(T update) where T : class
            => ConversationEvents.Invoke(update);
        public void RaiseUpdate<T>(T update) where T : class
            => UpdatesReceiverEvents.Invoke(update);
        public void CompleteRun() => _run.TrySetResult();

        public Task RunAsync()
        {
            RunCount++;
            return _run.Task;
        }
        public Task RequestResponseAsync(
            RTICResponseRequest request,
            CancellationToken cancellationToken)
        {
            Response = (request.Instructions, cancellationToken);
            return Task.CompletedTask;
        }
        public Task InterruptOutputAsync(
            RTICOutputInterruption request,
            CancellationToken cancellationToken)
        {
            Interruption = (
                new RealtimeOutputIdentity(
                    request.Cursor.ResponseId,
                    request.Cursor.ItemId,
                    request.Cursor.OutputIndex,
                    request.Cursor.ContentIndex),
                request.PlayedThrough,
                request.CancelResponseIfActive,
                cancellationToken);
            return Task.CompletedTask;
        }
        public void Cancel()
        {
            CancelCount++;
            _run.TrySetResult();
        }
        public void Dispose() => _eventHost.Dispose();
    }
}
