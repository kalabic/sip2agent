using AudioFormatLib;
using AudioFormatLib.IO;
using DotBase.Log;
using LibRTIC.Config;
using LibRTIC.Realtime;
using MiniRTICallServer.RTISorcery;
using SIP2Agent.UserAgentService.Service;
using Xunit;

namespace MiniRTICallServer.Tests.RTISorcery;

public sealed class RTICCallAgentTests
{
    private const string Greeting = "A precise test greeting.";

    [Fact]
    public async Task PrepareAsync_WaitsForProviderReadyAndPassesCallerBuffer()
    {
        FakeRealtimeSession provider = new();
        IPcm16FrameOutput? callerAudio = null;
        await using RTICCallAgent agent = CreateAgent(
            provider,
            captureCallerAudio: output => callerAudio = output);

        Task preparation = agent.PrepareAsync(CancellationToken.None);
        Assert.False(preparation.IsCompleted);
        Assert.NotNull(callerAudio);
        Assert.Equal(ASampleValueFormat.S16, callerAudio.Format.SampleValueFormat);
        Assert.Equal(24_000, callerAudio.Format.SampleRate);
        Assert.Equal(1, callerAudio.Format.ChannelCount);
        Assert.Equal(AByteOrder.LittleEndian, callerAudio.Format.ByteOrder);

        provider.CompleteReady();
        await preparation;
        Assert.Equal(1, provider.RunCount);
    }

    [Fact]
    public async Task PrepareAsync_TimeoutCancelsProvider()
    {
        FakeRealtimeSession provider = new();
        await using RTICCallAgent agent = CreateAgent(
            provider,
            preparationTimeout: TimeSpan.FromMilliseconds(50));

        AgentPreparationException exception =
            await Assert.ThrowsAsync<AgentPreparationException>(
                () => agent.PrepareAsync(CancellationToken.None));

        Assert.Equal(AgentPreparationFailureKind.ProviderUnavailable, exception.FailureKind);
        Assert.True(provider.CancelCount >= 1);
    }

    [Fact]
    public async Task StartAsync_RequiresReadiness()
    {
        FakeRealtimeSession provider = new();
        await using RTICCallAgent agent = CreateAgent(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_StartsMediaThenRequestsExactGreetingOnce()
    {
        List<string> operations = [];
        FakeRealtimeSession provider = new()
        {
            OnStartResponse = instructions => operations.Add($"response:{instructions}"),
        };
        await using RTICCallAgent agent = CreateAgent(
            provider,
            startMediaAsync: () =>
            {
                operations.Add("media");
                return Task.CompletedTask;
            });
        Task preparation = agent.PrepareAsync(CancellationToken.None);
        provider.CompleteReady();
        await preparation;

        await Task.WhenAll(
            agent.StartAsync(CancellationToken.None),
            agent.StartAsync(CancellationToken.None));

        Assert.Equal(["media", $"response:{Greeting}"], operations);
        Assert.Equal(1, provider.StartResponseCount);
    }

    [Fact]
    public async Task Completion_FaultsWhenProviderFailsAfterReadiness()
    {
        FakeRealtimeSession provider = new();
        await using RTICCallAgent agent = CreateAgent(provider);
        Task preparation = agent.PrepareAsync(CancellationToken.None);
        provider.CompleteReady();
        await preparation;

        provider.FailRun(new InvalidOperationException("provider sender failed"));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.Completion);
        Assert.Equal("provider sender failed", exception.Message);
    }

    [Fact]
    public async Task StopAndDispose_AreIdempotentAndAwaitProviderBeforeDisposal()
    {
        List<string> operations = [];
        FakeRealtimeSession provider = new()
        {
            CompleteRunOnCancel = false,
            OnDispose = () => operations.Add("provider-disposed"),
        };
        RTICCallAgent agent = CreateAgent(provider);
        Task preparation = agent.PrepareAsync(CancellationToken.None);
        provider.CompleteReady();
        await preparation;

        Task firstStop = agent.StopAsync("test stop", CancellationToken.None);
        Task secondStop = agent.StopAsync("duplicate stop", CancellationToken.None);
        Assert.False(firstStop.IsCompleted);
        Assert.Equal(1, provider.CancelCount);
        Assert.Empty(operations);

        provider.CompleteRun();
        await Task.WhenAll(firstStop, secondStop);
        await agent.DisposeAsync();

        Assert.Equal(["provider-disposed"], operations);
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task StopTimeout_UsesProviderDisposalAsTheSingleForcedCloseBoundary()
    {
        FakeRealtimeSession provider = new()
        {
            CompleteRunOnCancel = false,
            CompleteRunOnDispose = true,
        };
        RTICCallAgent agent = CreateAgent(
            provider,
            stopTimeout: TimeSpan.FromMilliseconds(40));
        Task preparation = agent.PrepareAsync(CancellationToken.None);
        provider.CompleteReady();
        await preparation;

        await agent.StopAsync("forced test stop", TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, provider.DisposeCount);
        Assert.True(provider.RunCompleted);
        await agent.DisposeAsync();
    }

    private static RTICCallAgent CreateAgent(
        FakeRealtimeSession provider,
        RTICConfig? configuration = null,
        TimeSpan? preparationTimeout = null,
        TimeSpan? stopTimeout = null,
        Func<Task>? startMediaAsync = null,
        Action<IPcm16FrameOutput>? captureCallerAudio = null,
        Action? onFactoryCall = null)
    {
        RTICCallAgentOptions options = new()
        {
            PreparationTimeout = preparationTimeout ?? TimeSpan.FromSeconds(5),
            StopTimeout = stopTimeout ?? TimeSpan.FromSeconds(1),
            GreetingInstructions = Greeting,
        };

        return RTICCallAgent.CreateForTesting(
            configuration ?? ValidConfiguration(),
            options,
            (InfoLog _, RTICConfig _, IPcm16FrameOutput output, CancellationToken _) =>
            {
                onFactoryCall?.Invoke();
                captureCallerAudio?.Invoke(output);
                return provider;
            },
            startMediaAsync ?? (() => Task.CompletedTask));
    }

    private static RTICConfig ValidConfiguration()
        => new(
            new OpenAIProviderOptions("test-key"),
            RealtimeSessionOptionsFactory.Default);

    private sealed class FakeRealtimeSession : IRealtimeAgentSession
    {
        private readonly TaskCompletionSource _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _run =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<RealtimeAgentMediaUpdate>? MediaUpdate
        {
            add { }
            remove { }
        }

        public Task Ready => _ready.Task;
        public bool CompleteRunOnCancel { get; init; } = true;
        public bool CompleteRunOnDispose { get; init; }
        public Action<string?>? OnStartResponse { get; init; }
        public Action? OnDispose { get; init; }
        public int RunCount { get; private set; }
        public int StartResponseCount { get; private set; }
        public int CancelCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool RunCompleted => _run.Task.IsCompleted;

        public Task RunAsync()
        {
            RunCount++;
            return _run.Task;
        }

        public Task StartResponseAsync(string? instructions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartResponseCount++;
            OnStartResponse?.Invoke(instructions);
            return Task.CompletedTask;
        }

        public Task InterruptResponseAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task TruncateOutputItemAsync(
            string itemId,
            int contentIndex,
            TimeSpan audioEndTime,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Cancel()
        {
            CancelCount++;
            if (CompleteRunOnCancel)
            {
                _run.TrySetResult();
            }
        }

        public void Dispose()
        {
            DisposeCount++;
            if (CompleteRunOnDispose)
            {
                _run.TrySetResult();
            }
            OnDispose?.Invoke();
        }

        public void CompleteReady() => _ready.TrySetResult();
        public void CompleteRun() => _run.TrySetResult();
        public void FailRun(Exception exception) => _run.TrySetException(exception);
    }
}
