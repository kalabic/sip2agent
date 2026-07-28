namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib;

/// <summary>
/// Presentation time for packetized payload APIs. Stored in-memory with the packet index;
/// not a standard per-packet field inside classic WAV <c>data</c>.
/// </summary>
[Experimental("S2AWAV001")]
internal readonly record struct WaveTime
{
    /// <summary>100-nanosecond ticks (same resolution as <see cref="TimeSpan"/>).</summary>
    public long Ticks { get; }

    /// <summary>Optional sample-domain index when the application knows it.</summary>
    public long? SampleIndex { get; }

    public WaveTime(long ticks, long? sampleIndex = null)
    {
        if (ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks));
        }

        if (sampleIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleIndex));
        }

        Ticks = ticks;
        SampleIndex = sampleIndex;
    }

    public TimeSpan TimeSpan => TimeSpan.FromTicks(Ticks);

    public static WaveTime FromTimeSpan(TimeSpan time)
        => new(time.Ticks);

    public static WaveTime FromSamples(long sampleIndex, uint sampleRate)
    {
        if (sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (sampleIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleIndex));
        }

        double ticks = sampleIndex * (double)TimeSpan.TicksPerSecond / sampleRate;
        return new WaveTime((long)Math.Round(ticks), sampleIndex);
    }

    public override string ToString()
        => SampleIndex is long s
            ? $"{TimeSpan} (sample {s})"
            : TimeSpan.ToString();
}
