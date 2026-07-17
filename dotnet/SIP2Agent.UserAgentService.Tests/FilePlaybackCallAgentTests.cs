using Microsoft.Extensions.Logging.Abstractions;
using SIP2Agent.UserAgentService.Service;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using System.Net;
using Xunit;

namespace SIP2Agent.UserAgentService.Tests;

public sealed class FilePlaybackCallAgentTests
{
    [Fact]
    public async Task NoFile_AdvertisesLegacyCodecsAndCompletesOnlyWhenStopped()
    {
        var agent = new FilePlaybackCallAgent(
            NullLogger.Instance,
            resolvedAudioFile: null,
            acceptRtpFromAny: true);

        SDP offer = agent.MediaSession.CreateOffer(IPAddress.Loopback);
        string audioLine = offer.ToString()
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("m=audio ", StringComparison.Ordinal));
        string[] payloadTypes = audioLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[3..];

        Assert.Equal(["0", "8", "9"], payloadTypes[..3]);
        Assert.False(agent.Completion.IsCompleted);

        await agent.PrepareAsync(CancellationToken.None);
        await agent.StartAsync(CancellationToken.None);
        Assert.False(agent.Completion.IsCompleted);

        await agent.StopAsync("test completed", CancellationToken.None);
        await agent.Completion;
        Assert.True(agent.MediaSession.IsClosed);
    }

    [Fact]
    public async Task AnswerFile_CompletesWhenRetainedPlaybackFinishes()
    {
        string path = Path.GetTempFileName();
        FilePlaybackCallAgent? agent = null;
        try
        {
            await File.WriteAllBytesAsync(path, new byte[160]);
            agent = new FilePlaybackCallAgent(
                NullLogger.Instance,
                path,
                acceptRtpFromAny: false);

            SDP offer = agent.MediaSession.CreateOffer(IPAddress.Loopback);
            Assert.Equal(
                SetDescriptionResultEnum.OK,
                agent.MediaSession.SetRemoteDescription(SdpType.answer, offer));
            await agent.MediaSession.Start();
            await agent.StartAsync(CancellationToken.None);
            await agent.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (agent is not null)
            {
                await agent.StopAsync("test completed", CancellationToken.None);
            }
            File.Delete(path);
        }
    }
}
