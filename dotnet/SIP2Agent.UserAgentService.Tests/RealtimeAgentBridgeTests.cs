using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using SIP2Agent.UserAgentService.Service;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using System.Buffers.Binary;
using System.Threading.Channels;
using Xunit;

namespace SIP2Agent.UserAgentService.Tests;

public sealed class RealtimeAgentBridgeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Formats_AreG711OnlyAndRestrictionsAreIndependent()
    {
        using RealtimeAgentBridge endpoint = new RealtimeAgentBridge(
            NullLogger.Instance);

        Assert.Equal(
            [AudioCodecsEnum.PCMU, AudioCodecsEnum.PCMA],
            endpoint.Assistant.GetAudioSourceFormats().Select(x => x.Codec));
        Assert.Equal(
            [AudioCodecsEnum.PCMU, AudioCodecsEnum.PCMA],
            endpoint.Caller.GetAudioSinkFormats().Select(x => x.Codec));

        ((IAudioSource)endpoint.Assistant).RestrictFormats(
            x => x.Codec == AudioCodecsEnum.PCMU);

        Assert.Equal([AudioCodecsEnum.PCMU], endpoint.Assistant.GetAudioSourceFormats().Select(x => x.Codec));
        Assert.Equal(
            [AudioCodecsEnum.PCMU, AudioCodecsEnum.PCMA],
            endpoint.Caller.GetAudioSinkFormats().Select(x => x.Codec));

        ((IAudioSink)endpoint.Caller).RestrictFormats(
            x => x.Codec == AudioCodecsEnum.G722);

        Assert.Empty(endpoint.Caller.GetAudioSinkFormats());
        Assert.Equal([AudioCodecsEnum.PCMU], endpoint.Assistant.GetAudioSourceFormats().Select(x => x.Codec));
    }

    [Fact]
    public async Task LegacyInterfaceMembers_AreDeterministicAndNonThrowing()
    {
        await using RealtimeAgentBridge endpoint = new RealtimeAgentBridge(
            NullLogger.Instance);

        endpoint.Assistant.ExternalAudioSourceRawSample(
            AudioSamplingRatesEnum.Rate8KHz,
            20,
            new short[RealtimeAgentBridge.SIP_SAMPLES_PER_PACKET]);
#pragma warning disable CS0618
        endpoint.Caller.GotAudioRtp(
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1234),
            1,
            2,
            3,
            0,
            false,
            []);
#pragma warning restore CS0618

        await endpoint.Assistant.PauseAudio();
        await endpoint.Assistant.ResumeAudio();
        await endpoint.Caller.PauseAudioSink();
        await endpoint.Caller.ResumeAudioSink();

        Assert.False(endpoint.Assistant.HasEncodedAudioSubscribers());
        Assert.False(endpoint.Assistant.IsAudioSourcePaused());
        Assert.False(endpoint.Caller.IsPaused());
    }

    [Fact]
    public async Task FormatChange_AfterWorkerStartFaultsWithoutThrowingIntoNegotiation()
    {
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation);
        await endpoint.Assistant.StartAudio();

        Exception? setterException = Record.Exception(
            () => endpoint.Assistant.SetAudioSourceFormat(Format(SDPWellKnownMediaFormatsEnum.PCMA)));

        Assert.Null(setterException);
        await Assert.ThrowsAsync<InvalidOperationException>(() => endpoint.Completion);
    }

    [Fact]
    public async Task InvalidFormat_BeforeWorkerStartThrowsArgumentException()
    {
        await using RealtimeAgentBridge endpoint = new RealtimeAgentBridge(
            NullLogger.Instance);

        Assert.Throws<ArgumentException>(
            () => endpoint.Assistant.SetAudioSourceFormat(Format(SDPWellKnownMediaFormatsEnum.G722)));
        Assert.Throws<ArgumentException>(
            () => endpoint.Caller.SetAudioSinkFormat(Format(SDPWellKnownMediaFormatsEnum.G722)));
    }

    [Theory]
    [InlineData(SDPWellKnownMediaFormatsEnum.PCMU)]
    [InlineData(SDPWellKnownMediaFormatsEnum.PCMA)]
    public async Task Input_DecodesFrameCodecIndependentlyAndWritesCallerAudioBuffer(
        SDPWellKnownMediaFormatsEnum incomingCodec)
    {
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation);
        AudioFormat incomingFormat = Format(incomingCodec);
        AudioFormat outputFormat = Format(
            incomingCodec == SDPWellKnownMediaFormatsEnum.PCMU
                ? SDPWellKnownMediaFormatsEnum.PCMA
                : SDPWellKnownMediaFormatsEnum.PCMU);
        endpoint.Assistant.SetAudioSourceFormat(outputFormat);
        endpoint.Caller.SetAudioSinkFormat(incomingFormat);
        await endpoint.Caller.StartAudioSink();

        using AudioEncoder codec = new([incomingFormat, outputFormat]);
        byte[] frame = codec.EncodeAudio(CreateRamp(160), incomingFormat);
        endpoint.Caller.GotEncodedMediaFrame(
            new EncodedAudioFrame(0, incomingFormat, 20, frame));

        short[] buffered = await ReadCallerAudioAsync(endpoint);

        Assert.NotEmpty(buffered);
        Assert.Equal(0, endpoint.DroppedInputFrameCount);
    }

    [Fact]
    public async Task Input_VariableFrameLengthsProduceContinuousCallerAudio()
    {
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation);
        AudioFormat pcmu = Format(SDPWellKnownMediaFormatsEnum.PCMU);
        await endpoint.Caller.StartAudioSink();

        using AudioEncoder codec = new([pcmu, Format(SDPWellKnownMediaFormatsEnum.PCMA)]);
        byte[] halfFrame = codec.EncodeAudio(CreateRamp(80), pcmu);
        for (int index = 0; index < 3; index++)
        {
            endpoint.Caller.GotEncodedMediaFrame(
                new EncodedAudioFrame(0, pcmu, 10, halfFrame));
        }

        short[] buffered = await ReadCallerAudioAsync(endpoint, minimumFrames: 480);

        Assert.True(buffered.Length >= 480);
    }

    [Fact]
    public async Task Input_PreStartBurstDropsOldestFramesExactly()
    {
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation);
        AudioFormat pcmu = Format(SDPWellKnownMediaFormatsEnum.PCMU);

        using AudioEncoder codec = new([pcmu, Format(SDPWellKnownMediaFormatsEnum.PCMA)]);
        byte[] payload = codec.EncodeAudio(CreateRamp(160), pcmu);
        for (int index = 0; index < 25; index++)
        {
            endpoint.Caller.GotEncodedMediaFrame(
                new EncodedAudioFrame(0, pcmu, 20, payload));
        }

        Assert.Equal(15, endpoint.DroppedInputFrameCount);
        await endpoint.Caller.StartAudioSink();
        await endpoint.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Input_PauseStopsNewWritesWithoutClearingBufferedAudio()
    {
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation);
        AudioFormat pcmu = Format(SDPWellKnownMediaFormatsEnum.PCMU);
        await endpoint.Caller.StartAudioSink();
        using AudioEncoder codec = new([pcmu, Format(SDPWellKnownMediaFormatsEnum.PCMA)]);
        byte[] payload = codec.EncodeAudio(CreateRamp(160), pcmu);

        endpoint.Caller.GotEncodedMediaFrame(new EncodedAudioFrame(0, pcmu, 20, payload));
        await WaitUntilAsync(() => endpoint.CallerAudioOutput.Count > 0);
        int bufferedFramesBeforePause = endpoint.CallerAudioOutput.Count;

        await endpoint.Caller.PauseAudioSink();
        endpoint.Caller.GotEncodedMediaFrame(new EncodedAudioFrame(0, pcmu, 20, payload));
        await Task.Yield();
        Assert.Equal(bufferedFramesBeforePause, endpoint.CallerAudioOutput.Count);

        await endpoint.Caller.ResumeAudioSink();
        endpoint.Caller.GotEncodedMediaFrame(new EncodedAudioFrame(0, pcmu, 20, payload));
        await WaitUntilAsync(
            () => endpoint.CallerAudioOutput.Count > bufferedFramesBeforePause);
    }

    [Fact]
    public async Task Input_MalformedFrameFaultsCompletionWithoutEscapingCallback()
    {
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation);
        await endpoint.Caller.StartAudioSink();
        AudioFormat g722 = Format(SDPWellKnownMediaFormatsEnum.G722);

        Exception? callbackException = Record.Exception(
            () => endpoint.Caller.GotEncodedMediaFrame(
                new EncodedAudioFrame(0, g722, 20, new byte[160])));

        Assert.Null(callbackException);
        await Assert.ThrowsAsync<ArgumentException>(
            () => endpoint.Completion.WaitAsync(TestTimeout));
        Assert.True(conversation.CancelCount >= 1);
    }

    [Fact]
    public async Task Input_CallerAudioBufferExhaustionFaultsCompletion()
    {
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation);
        AudioFormat pcmu = Format(SDPWellKnownMediaFormatsEnum.PCMU);
        await endpoint.Caller.StartAudioSink();
        using AudioEncoder codec = new([pcmu, Format(SDPWellKnownMediaFormatsEnum.PCMA)]);
        byte[] payload = codec.EncodeAudio(CreateRamp(160), pcmu);

        for (int index = 0; index < 150 && !endpoint.Completion.IsCompleted; index++)
        {
            int previousFrameCount = endpoint.CallerAudioOutput.Count;
            endpoint.Caller.GotEncodedMediaFrame(
                new EncodedAudioFrame(0, pcmu, 20, payload));
            await WaitUntilAsync(
                () => endpoint.Completion.IsCompleted ||
                      endpoint.CallerAudioOutput.Count > previousFrameCount);
        }

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => endpoint.Completion.WaitAsync(TestTimeout));
        Assert.Contains("audio buffer", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(conversation.CancelCount >= 1);
    }

    [Fact]
    public async Task Output_TwoHundredMillisecondsEmitsTenPacedPacketsThenIdleSilence()
    {
        FakeTimeProvider time = new();
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation, time);
        PacketCollector packets = new(time);
        endpoint.Assistant.OnAudioSourceEncodedSample += packets.Handle;
        await endpoint.Assistant.StartAudio();

        conversation.RaiseDelta(CreateDelta(CreateRamp(4_800)));
        conversation.RaiseFinished(CreateFinished());

        List<Packet> received = [await packets.ReadAsync()];
        for (int index = 1; index < 10; index++)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));
            received.Add(await packets.ReadAsync());
        }

        time.Advance(TimeSpan.FromMilliseconds(100));
        await Task.Yield();

        Assert.Equal(10, received.Count);
        Assert.True(packets.TryRead(out Packet? idle));
        using AudioEncoder codec = new([Format(SDPWellKnownMediaFormatsEnum.PCMU)]);
        Assert.Equal(codec.EncodeAudio(new short[160], Format(SDPWellKnownMediaFormatsEnum.PCMU)), idle!.Payload);
        Assert.All(received, packet =>
        {
            Assert.Equal(160u, packet.DurationRtpUnits);
            Assert.Equal(160, packet.Payload.Length);
        });
        for (int index = 1; index < received.Count; index++)
        {
            Assert.Equal(
                TimeSpan.FromMilliseconds(20),
                time.GetElapsedTime(received[index - 1].Timestamp, received[index].Timestamp));
        }
        Assert.Equal(0, endpoint.UnplayedRealtimeSampleCount);
    }

    [Fact]
    public async Task Output_FakeTimeJumpDoesNotCreateCatchUpBurst()
    {
        FakeTimeProvider time = new();
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation, time);
        PacketCollector packets = new(time);
        endpoint.Assistant.OnAudioSourceEncodedSample += packets.Handle;
        await endpoint.Assistant.StartAudio();

        conversation.RaiseDelta(CreateDelta(CreateRamp(2_400)));
        conversation.RaiseFinished(CreateFinished());
        _ = await packets.ReadAsync();

        time.Advance(TimeSpan.FromMilliseconds(40));
        _ = await packets.ReadAsync();
        await Task.Yield();

        Assert.False(packets.TryRead(out _));

        time.Advance(TimeSpan.FromMilliseconds(20));
        _ = await packets.ReadAsync();
    }

    [Fact]
    public async Task Output_ShortFinalResponsePadsThenContinuesSilenceUntilNewResponse()
    {
        FakeTimeProvider time = new();
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation, time);
        PacketCollector packets = new(time);
        endpoint.Assistant.OnAudioSourceEncodedSample += packets.Handle;
        await endpoint.Assistant.StartAudio();

        conversation.RaiseDelta(CreateDelta(CreateRamp(240)));
        conversation.RaiseFinished(CreateFinished());

        Packet packet = await packets.ReadAsync();
        time.Advance(TimeSpan.FromMilliseconds(100));
        Packet firstIdle = await packets.ReadAsync();
        time.Advance(TimeSpan.FromMilliseconds(20));
        Packet secondIdle = await packets.ReadAsync();

        Assert.Equal(160u, packet.DurationRtpUnits);
        Assert.Equal(160, packet.Payload.Length);
        using AudioEncoder codec = new([Format(SDPWellKnownMediaFormatsEnum.PCMU)]);
        byte[] silence = codec.EncodeAudio(new short[160], Format(SDPWellKnownMediaFormatsEnum.PCMU));
        Assert.Equal(silence, firstIdle.Payload);
        Assert.Equal(silence, secondIdle.Payload);
        Assert.Equal(0, endpoint.UnplayedRealtimeSampleCount);
    }

    [Fact]
    public async Task Output_UnderrunEmitsSilenceWithoutConsumingPartialAudio()
    {
        FakeTimeProvider time = new();
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation, time);
        AudioFormat pcmu = Format(SDPWellKnownMediaFormatsEnum.PCMU);
        endpoint.Assistant.SetAudioSourceFormat(pcmu);
        PacketCollector packets = new(time);
        endpoint.Assistant.OnAudioSourceEncodedSample += packets.Handle;
        await endpoint.Assistant.StartAudio();

        conversation.RaiseDelta(CreateDelta(CreateRamp(1_440)));
        _ = await packets.ReadAsync();
        time.Advance(TimeSpan.FromMilliseconds(20));
        _ = await packets.ReadAsync();
        time.Advance(TimeSpan.FromMilliseconds(20));
        Packet underrun = await packets.ReadAsync();

        using AudioEncoder codec = new([pcmu]);
        byte[] silence = codec.EncodeAudio(new short[160], pcmu);
        Assert.Equal(silence, underrun.Payload);
        Assert.Equal(480, endpoint.UnplayedRealtimeSampleCount);

        conversation.RaiseFinished(CreateFinished());
        Packet final;
        do
        {
            time.Advance(TimeSpan.FromMilliseconds(20));
            final = await packets.ReadAsync();
        }
        while (endpoint.UnplayedRealtimeSampleCount > 0);

        Assert.NotEqual(silence, final.Payload);
        Assert.Equal(0, endpoint.UnplayedRealtimeSampleCount);
    }

    [Theory]
    [InlineData(SDPWellKnownMediaFormatsEnum.PCMU)]
    [InlineData(SDPWellKnownMediaFormatsEnum.PCMA)]
    public async Task Output_CodecSilenceIsAlwaysOneG711Packet(
        SDPWellKnownMediaFormatsEnum formatKind)
    {
        FakeTimeProvider time = new();
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation, time);
        AudioFormat format = Format(formatKind);
        endpoint.Assistant.SetAudioSourceFormat(format);
        PacketCollector packets = new(time);
        endpoint.Assistant.OnAudioSourceEncodedSample += packets.Handle;
        await endpoint.Assistant.StartAudio();

        conversation.RaiseDelta(CreateDelta(CreateRamp(1_440)));
        _ = await packets.ReadAsync();
        time.Advance(TimeSpan.FromMilliseconds(20));
        _ = await packets.ReadAsync();
        time.Advance(TimeSpan.FromMilliseconds(20));
        Packet underrun = await packets.ReadAsync();

        using AudioEncoder codec = new([format]);
        Assert.Equal(codec.EncodeAudio(new short[160], format), underrun.Payload);
        Assert.Equal(160, underrun.Payload.Length);
    }

    [Fact]
    public async Task Output_ThirtySecondBudgetOverflowFaultsInsteadOfDropping()
    {
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation);
        byte[] tooMuchAudio = new byte[
            (RealtimeAgentBridge.OUTPUT_MAX_REALTIME_SAMPLES + 1) * sizeof(short)];

        conversation.RaiseDelta(new RealtimeOutputAudioDelta(
            new RealtimeOutputIdentity("response-1", "item-1", 0),
            tooMuchAudio));

        await Assert.ThrowsAsync<InvalidDataException>(() => endpoint.Completion);
        Assert.Equal(1, endpoint.OutputOverflowCount);
        Assert.True(conversation.CancelCount >= 1);
    }

    [Fact]
    public async Task Output_CommandQueueOverflowFaultsInsteadOfDropping()
    {
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation);
        RealtimeOutputAudioDelta delta = new(
            new RealtimeOutputIdentity("response-1", "item-1", 0),
            new byte[sizeof(short)]);

        for (int index = 0; index <= RealtimeAgentBridge.OUTPUT_COMMAND_CAPACITY; index++)
        {
            conversation.RaiseDelta(delta);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => endpoint.Completion);
        Assert.Equal(1, endpoint.OutputOverflowCount);
    }

    [Fact]
    public async Task Output_SubscriberExceptionFaultsWorkerCleanly()
    {
        FakeTimeProvider time = new();
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation, time);
        endpoint.Assistant.OnAudioSourceEncodedSample += (_, _) =>
            throw new InvalidOperationException("subscriber failed");
        await endpoint.Assistant.StartAudio();

        conversation.RaiseDelta(CreateDelta(CreateRamp(1_440)));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => endpoint.Completion.WaitAsync(TestTimeout));
        Assert.Equal("subscriber failed", exception.Message);
    }

    [Fact]
    public async Task BargeIn_StopsPlaybackCancelsAndTruncatesAtRealPlayedCursor()
    {
        FakeTimeProvider time = new();
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation, time);
        PacketCollector packets = new(time);
        endpoint.Assistant.OnAudioSourceEncodedSample += packets.Handle;
        await endpoint.Assistant.StartAudio();

        conversation.RaiseDelta(CreateDelta(CreateRamp(4_800)));
        _ = await packets.ReadAsync();

        conversation.RaiseInputSpeechStarted();
        TruncationRequest truncation = await conversation.Truncations.Reader
            .ReadAsync()
            .AsTask()
            .WaitAsync(TestTimeout);

        Assert.Equal(1, conversation.InterruptResponseCount);
        Assert.Equal("item-1", truncation.ItemId);
        Assert.Equal(0, truncation.ContentIndex);
        Assert.Equal(TimeSpan.FromMilliseconds(20), truncation.AudioEndTime);
        Assert.Equal(0, endpoint.UnplayedRealtimeSampleCount);

        conversation.RaiseDelta(CreateDelta(CreateRamp(480)));
        time.Advance(TimeSpan.FromSeconds(1));
        Packet idle = await packets.ReadAsync();
        using AudioEncoder codec = new([Format(SDPWellKnownMediaFormatsEnum.PCMU)]);
        Assert.Equal(codec.EncodeAudio(new short[160], Format(SDPWellKnownMediaFormatsEnum.PCMU)), idle.Payload);
    }

    [Fact]
    public async Task BargeIn_AfterFinalMarkerTruncatesWithoutCancellingCompletedResponse()
    {
        FakeTimeProvider time = new();
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation, time);
        PacketCollector packets = new(time);
        endpoint.Assistant.OnAudioSourceEncodedSample += packets.Handle;
        await endpoint.Assistant.StartAudio();

        conversation.RaiseDelta(CreateDelta(CreateRamp(4_800)));
        conversation.RaiseFinished(CreateFinished());
        _ = await packets.ReadAsync();

        conversation.RaiseInputSpeechStarted();
        TruncationRequest truncation = await conversation.Truncations.Reader
            .ReadAsync()
            .AsTask()
            .WaitAsync(TestTimeout);

        Assert.Equal(0, conversation.InterruptResponseCount);
        Assert.Equal(TimeSpan.FromMilliseconds(20), truncation.AudioEndTime);
    }

    [Fact]
    public async Task BargeIn_ProviderFailureFaultsEndpoint()
    {
        FakeTimeProvider time = new();
        FakeRealtimeAudioSession conversation = new()
        {
            InterruptResponse = _ => throw new InvalidOperationException("interrupt failed"),
        };
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation, time);
        PacketCollector packets = new(time);
        endpoint.Assistant.OnAudioSourceEncodedSample += packets.Handle;
        await endpoint.Assistant.StartAudio();

        conversation.RaiseDelta(CreateDelta(CreateRamp(4_800)));
        _ = await packets.ReadAsync();
        conversation.RaiseInputSpeechStarted();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => endpoint.Completion.WaitAsync(TestTimeout));
        Assert.Equal("interrupt failed", exception.Message);
    }

    [Fact]
    public async Task Lifecycle_IsIdempotentAndDetachesConversationEvents()
    {
        FakeRealtimeAudioSession conversation = new();
        RealtimeAgentBridge endpoint = CreateEndpoint(conversation);

        Assert.Equal(1, conversation.AudioDeltaSubscriberCount);
        Assert.Equal(1, conversation.AudioFinishedSubscriberCount);
        Assert.Equal(1, conversation.InputSpeechSubscriberCount);

        await endpoint.Assistant.StartAudio();
        await endpoint.Assistant.StartAudio();
        await endpoint.Caller.StartAudioSink();
        await endpoint.Caller.StartAudioSink();
        await endpoint.Assistant.PauseAudio();
        await endpoint.Assistant.PauseAudio();
        await endpoint.Assistant.ResumeAudio();
        await endpoint.Assistant.ResumeAudio();
        await endpoint.Caller.PauseAudioSink();
        await endpoint.Caller.ResumeAudioSink();

        Task firstStop = endpoint.StopAsync(CancellationToken.None);
        Task secondStop = endpoint.StopAsync(CancellationToken.None);
        await Task.WhenAll(firstStop, secondStop).WaitAsync(TestTimeout);
        await endpoint.DisposeAsync();

        Assert.Equal(0, conversation.AudioDeltaSubscriberCount);
        Assert.Equal(0, conversation.AudioFinishedSubscriberCount);
        Assert.Equal(0, conversation.InputSpeechSubscriberCount);
        Assert.True(endpoint.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Lifecycle_StopCancelsPendingFakeTimeDeadline()
    {
        FakeTimeProvider time = new();
        FakeRealtimeAudioSession conversation = new();
        RealtimeAgentBridge endpoint = CreateEndpoint(conversation, time);
        PacketCollector packets = new(time);
        endpoint.Assistant.OnAudioSourceEncodedSample += packets.Handle;
        await endpoint.Assistant.StartAudio();
        conversation.RaiseDelta(CreateDelta(CreateRamp(1_440)));
        _ = await packets.ReadAsync();

        await endpoint.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.True(endpoint.Completion.IsCompletedSuccessfully);
        await endpoint.DisposeAsync();
    }

    [Fact]
    public async Task Lifecycle_ClosingSourceDoesNotStopInputWorker()
    {
        FakeRealtimeAudioSession conversation = new();
        await using RealtimeAgentBridge endpoint = CreateEndpoint(conversation);
        AudioFormat pcmu = Format(SDPWellKnownMediaFormatsEnum.PCMU);
        await endpoint.Assistant.StartAudio();
        await endpoint.Caller.StartAudioSink();
        await endpoint.Assistant.CloseAudio();
        using AudioEncoder codec = new([pcmu, Format(SDPWellKnownMediaFormatsEnum.PCMA)]);
        byte[] payload = codec.EncodeAudio(CreateRamp(160), pcmu);

        endpoint.Caller.GotEncodedMediaFrame(new EncodedAudioFrame(0, pcmu, 20, payload));

        short[] buffered = await ReadCallerAudioAsync(endpoint);
        Assert.NotEmpty(buffered);
    }

    [Fact]
    public async Task TwoEndpoints_KeepFormatsCountersTimingAndCompletionIsolated()
    {
        FakeTimeProvider firstTime = new();
        FakeTimeProvider secondTime = new();
        FakeRealtimeAudioSession firstConversation = new();
        FakeRealtimeAudioSession secondConversation = new();
        await using RealtimeAgentBridge first = CreateEndpoint(firstConversation, firstTime);
        await using RealtimeAgentBridge second = CreateEndpoint(secondConversation, secondTime);

        ((IAudioSource)first.Assistant).RestrictFormats(
            x => x.Codec == AudioCodecsEnum.PCMU);
        ((IAudioSource)second.Assistant).RestrictFormats(
            x => x.Codec == AudioCodecsEnum.PCMA);

        secondConversation.RaiseDelta(new RealtimeOutputAudioDelta(
            new RealtimeOutputIdentity("response-2", "item-2", 0),
            new byte[
                (RealtimeAgentBridge.OUTPUT_MAX_REALTIME_SAMPLES + 1) * sizeof(short)]));

        Assert.Equal([AudioCodecsEnum.PCMU], first.Assistant.GetAudioSourceFormats().Select(x => x.Codec));
        Assert.Equal([AudioCodecsEnum.PCMA], second.Assistant.GetAudioSourceFormats().Select(x => x.Codec));
        Assert.Equal(0, first.OutputOverflowCount);
        Assert.Equal(1, second.OutputOverflowCount);
        Assert.False(first.Completion.IsCompleted);
        Assert.True(second.Completion.IsFaulted);
    }

    private static RealtimeAgentBridge CreateEndpoint(
        FakeRealtimeAudioSession conversation,
        TimeProvider? timeProvider = null)
    {
        RealtimeAgentBridge endpoint = new RealtimeAgentBridge(
            NullLogger.Instance,
            timeProvider);
        endpoint.AttachSession(conversation);
        return endpoint;
    }

    private static async Task<short[]> ReadCallerAudioAsync(
        RealtimeAgentBridge endpoint,
        int minimumFrames = 1)
    {
        await WaitUntilAsync(
            () => endpoint.CallerAudioOutput.Count >= minimumFrames);

        short[] audio = new short[endpoint.CallerAudioOutput.Count];
        int framesRead = endpoint.CallerAudioOutput.Read(audio, 0, audio.Length);
        Array.Resize(ref audio, framesRead);
        return audio;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        await Task.Run(async () =>
        {
            while (!predicate())
            {
                await Task.Yield();
            }
        }).WaitAsync(TestTimeout);
    }

    private static AudioFormat Format(SDPWellKnownMediaFormatsEnum format)
        => new(format);

    private static short[] CreateRamp(int count)
        => Enumerable.Range(0, count)
            .Select(index => (short)((index * 97 % 20_000) - 10_000))
            .ToArray();

    private static byte[] ToPcmBytes(short[] samples)
    {
        byte[] bytes = new byte[samples.Length * sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(index * sizeof(short), sizeof(short)),
                samples[index]);
        }
        return bytes;
    }

    private static RealtimeOutputAudioDelta CreateDelta(short[] samples)
        => new(
            new RealtimeOutputIdentity("response-1", "item-1", 0),
            ToPcmBytes(samples));

    private static RealtimeOutputAudioFinished CreateFinished()
        => new(new RealtimeOutputIdentity("response-1", "item-1", 0));

    private sealed record Packet(long Timestamp, uint DurationRtpUnits, byte[] Payload);

    private sealed record TruncationRequest(
        string ItemId,
        int ContentIndex,
        TimeSpan AudioEndTime);

    private sealed class PacketCollector(FakeTimeProvider timeProvider)
    {
        private readonly Channel<Packet> _packets = Channel.CreateUnbounded<Packet>();

        public void Handle(uint durationRtpUnits, byte[] payload)
            => _packets.Writer.TryWrite(new Packet(
                timeProvider.GetTimestamp(),
                durationRtpUnits,
                payload.ToArray()));

        public async Task<Packet> ReadAsync()
            => await _packets.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public bool TryRead(out Packet? packet)
            => _packets.Reader.TryRead(out packet);
    }

    private sealed class FakeRealtimeAudioSession : IRealtimeAgentSession
    {
        private Action<RealtimeAgentMediaUpdate>? _mediaUpdate;

        public event Action<RealtimeAgentMediaUpdate>? MediaUpdate
        {
            add
            {
                _mediaUpdate += value;
                MediaUpdateSubscriberCount++;
            }
            remove
            {
                _mediaUpdate -= value;
                MediaUpdateSubscriberCount--;
            }
        }

        public Channel<TruncationRequest> Truncations { get; } =
            Channel.CreateUnbounded<TruncationRequest>();
        public Func<CancellationToken, Task>? InterruptResponse { get; init; }
        public Task Ready => Task.CompletedTask;
        public int MediaUpdateSubscriberCount { get; private set; }
        public int AudioDeltaSubscriberCount => MediaUpdateSubscriberCount;
        public int AudioFinishedSubscriberCount => MediaUpdateSubscriberCount;
        public int InputSpeechSubscriberCount => MediaUpdateSubscriberCount;
        public int InterruptResponseCount { get; private set; }
        public int CancelCount { get; private set; }

        public Task RunAsync() => Task.CompletedTask;

        public Task StartResponseAsync(
            string? instructions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task InterruptResponseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InterruptResponseCount++;
            return InterruptResponse?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task TruncateOutputItemAsync(
            string itemId,
            int contentIndex,
            TimeSpan audioEndTime,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Truncations.Writer.WriteAsync(
                new TruncationRequest(itemId, contentIndex, audioEndTime),
                cancellationToken).AsTask();
        }

        public void Cancel() => CancelCount++;

        public void Dispose()
        {
        }

        public void RaiseDelta(RealtimeOutputAudioDelta update)
            => _mediaUpdate?.Invoke(update);

        public void RaiseFinished(RealtimeOutputAudioFinished update)
            => _mediaUpdate?.Invoke(update);

        public void RaiseInputSpeechStarted()
            => _mediaUpdate?.Invoke(new RealtimeInputSpeechStarted());
    }
}
