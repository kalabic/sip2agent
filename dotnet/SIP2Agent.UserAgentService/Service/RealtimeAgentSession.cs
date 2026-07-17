namespace SIP2Agent.UserAgentService.Service;

internal readonly record struct RealtimeOutputIdentity
{
    public string ResponseId { get; }

    public string ItemId { get; }

    public int ContentIndex { get; }

    public RealtimeOutputIdentity(string responseId, string itemId, int contentIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentOutOfRangeException.ThrowIfNegative(contentIndex);

        ResponseId = responseId;
        ItemId = itemId;
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

    Task InterruptResponseAsync(CancellationToken cancellationToken);

    Task TruncateOutputItemAsync(
        string itemId,
        int contentIndex,
        TimeSpan audioEndTime,
        CancellationToken cancellationToken);

    void Cancel();
}
