namespace SIP2Agent.UserAgentService.Service;

internal readonly record struct RealtimeOutputIdentity
{
    public string ResponseId { get; }

    public string ItemId { get; }

    public int OutputIndex { get; }

    public int ContentIndex { get; }

    public RealtimeOutputIdentity(
        string responseId,
        string itemId,
        int outputIndex,
        int contentIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentOutOfRangeException.ThrowIfNegative(outputIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(contentIndex);

        ResponseId = responseId;
        ItemId = itemId;
        OutputIndex = outputIndex;
        ContentIndex = contentIndex;
    }
}

internal abstract record RealtimeAgentMediaUpdate;

/// <summary>
/// The PCM memory is borrowed for the duration of the event callback. Consumers that
/// retain it must copy it before returning.
/// </summary>
internal sealed record RealtimeOutputAudioDelta(
    RealtimeOutputIdentity Identity,
    ReadOnlyMemory<byte> Pcm16LittleEndian)
    : RealtimeAgentMediaUpdate;

internal sealed record RealtimeOutputAudioFinished(
    RealtimeOutputIdentity Identity)
    : RealtimeAgentMediaUpdate;

internal sealed record RealtimeInputSpeechStarted : RealtimeAgentMediaUpdate;

internal interface IRealtimeAgentSession : IDisposable
{
    event Action<RealtimeAgentMediaUpdate>? MediaUpdate;

    Task Ready { get; }

    Task RunAsync();

    Task StartResponseAsync(
        string? instructions,
        CancellationToken cancellationToken);

    Task InterruptOutputAsync(
        RealtimeOutputIdentity identity,
        TimeSpan playedThrough,
        bool cancelResponseIfActive,
        CancellationToken cancellationToken);

    void Cancel();
}
