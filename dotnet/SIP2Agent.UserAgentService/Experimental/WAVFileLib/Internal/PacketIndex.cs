namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib.Internal;

[Experimental("S2AWAV001")]
internal sealed class PacketIndex
{
    private readonly List<PacketIndexEntry> _entries = [];

    public IReadOnlyList<PacketIndexEntry> Entries => _entries;

    public void Add(long byteOffset, int length, WaveTime? timestamp, WaveTime? duration)
    {
        _entries.Add(new PacketIndexEntry(byteOffset, length, timestamp, duration));
    }

    public void Clear() => _entries.Clear();
}

[Experimental("S2AWAV001")]
internal readonly record struct PacketIndexEntry(
    long ByteOffset,
    int Length,
    WaveTime? Timestamp,
    WaveTime? Duration);
