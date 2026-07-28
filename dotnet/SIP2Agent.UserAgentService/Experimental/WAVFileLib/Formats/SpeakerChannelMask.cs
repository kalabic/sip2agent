namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib.Formats;

/// <summary>
/// WAVEFORMATEXTENSIBLE <c>dwChannelMask</c> speaker bits (ksmedia.h / mmreg.h).
/// </summary>
[Experimental("S2AWAV001")]
internal static class SpeakerChannelMask
{
    public const uint FrontLeft = 0x1;
    public const uint FrontRight = 0x2;
    public const uint FrontCenter = 0x4;
    public const uint LowFrequency = 0x8;
    public const uint BackLeft = 0x10;
    public const uint BackRight = 0x20;
    public const uint FrontLeftOfCenter = 0x40;
    public const uint FrontRightOfCenter = 0x80;
    public const uint BackCenter = 0x100;
    public const uint SideLeft = 0x200;
    public const uint SideRight = 0x400;
    public const uint TopCenter = 0x800;
    public const uint TopFrontLeft = 0x1000;
    public const uint TopFrontCenter = 0x2000;
    public const uint TopFrontRight = 0x4000;
    public const uint TopBackLeft = 0x8000;
    public const uint TopBackCenter = 0x10000;
    public const uint TopBackRight = 0x20000;

    private static readonly (uint Bit, string Name)[] Bits =
    [
        (FrontLeft, "FRONT_LEFT"),
        (FrontRight, "FRONT_RIGHT"),
        (FrontCenter, "FRONT_CENTER"),
        (LowFrequency, "LOW_FREQUENCY"),
        (BackLeft, "BACK_LEFT"),
        (BackRight, "BACK_RIGHT"),
        (FrontLeftOfCenter, "FRONT_LEFT_OF_CENTER"),
        (FrontRightOfCenter, "FRONT_RIGHT_OF_CENTER"),
        (BackCenter, "BACK_CENTER"),
        (SideLeft, "SIDE_LEFT"),
        (SideRight, "SIDE_RIGHT"),
        (TopCenter, "TOP_CENTER"),
        (TopFrontLeft, "TOP_FRONT_LEFT"),
        (TopFrontCenter, "TOP_FRONT_CENTER"),
        (TopFrontRight, "TOP_FRONT_RIGHT"),
        (TopBackLeft, "TOP_BACK_LEFT"),
        (TopBackCenter, "TOP_BACK_CENTER"),
        (TopBackRight, "TOP_BACK_RIGHT"),
    ];

    public static string Format(uint mask)
    {
        if (mask == 0)
        {
            return "0 (unspecified)";
        }

        var names = new List<string>();
        uint known = 0;
        foreach (var (bit, name) in Bits)
        {
            if ((mask & bit) != 0)
            {
                names.Add(name);
                known |= bit;
            }
        }

        uint unknown = mask & ~known;
        if (unknown != 0)
        {
            names.Add($"unknown:0x{unknown:X}");
        }

        return $"0x{mask:X8} ({string.Join(" | ", names)})";
    }
}
