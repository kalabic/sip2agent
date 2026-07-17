using Microsoft.Extensions.Logging;
using SIP2Agent.UserAgentService.Service;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace SIP2Agent.UserAgentService.Tests;

public sealed class SIPEndpointServiceIntegrationTest
{
    [Fact]
    public async Task EndpointRegistersUsingDigestStoreProfile()
    {
        const string username = "digest-endpoint";
        const string realm = "loopback.example";
        const string password = "test-password";

        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        using var providerTransport = CreateLoopbackTransport();
        int providerPort = providerTransport.GetSIPChannels().Single().ListeningSIPEndPoint.GetIPEndPoint().Port;
        var providerEndPoint = new SIPEndPoint(SIPProtocolsEnum.udp, IPAddress.Loopback, providerPort);
        var authenticated = new TaskCompletionSource<SIPAuthorisationDigest>(TaskCreationOptions.RunContinuationsAsynchronously);

        providerTransport.SIPTransportRequestReceived += async (_, _, request) =>
        {
            if (!request.Header.HasAuthenticationHeader)
            {
                SIPResponse response = SIPResponse.GetResponse(
                    request,
                    SIPResponseStatusCodesEnum.Unauthorised,
                    "Authentication Required");
                var authenticationHeader = new SIPAuthenticationHeader(
                    SIPAuthorisationHeadersEnum.WWWAuthenticate,
                    realm,
                    "nonce-sha256")
                {
                    SIPDigest = null,
                    Value = $"Digest realm=\"{realm}\",nonce=\"nonce-sha256\",qop=\"auth\",algorithm=SHA-256"
                };
                response.Header.AuthenticationHeaders.Add(authenticationHeader);
                await providerTransport.SendResponseAsync(response);
            }
            else
            {
                authenticated.TrySetResult(request.Header.AuthenticationHeaders.Single().SIPDigest);
                await providerTransport.SendResponseAsync(
                    SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null));
            }
        };

        var config = new SIPEndpointConfig
        {
            AccountProfiles =
            [
                new SIPRegistrationProfile(
                    Name: "digest-loopback",
                    RegistrarUri: SIPURI.ParseSIPURI($"sip:127.0.0.1:{providerPort};transport=udp"),
                    Transport: SIPProtocolsEnum.udp,
                    Username: username,
                    Password: null,
                    Realm: realm,
                    OutboundProxy: providerEndPoint,
                    ContactHost: null,
                    RegisterFailureRetryIntervalSeconds: 1,
                    DigestStore: SIPDigestStore.NewFromPassword(username, realm, password))
            ]
        };

        await using var endpoint = new SIPEndpointService(config, loggerFactory);
        endpoint.Start();

        SIPAuthorisationDigest digest = await authenticated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => endpoint.IsRegistrationConnected, TimeSpan.FromSeconds(5));

        Assert.Equal(DigestAlgorithmsEnum.SHA256, digest.DigestAlgorithm);
    }

    [Fact]
    public async Task ConcurrentTrustedInboundCallsUseIndependentSessionsAndRtpPorts()
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        using var providerTransport = new SIPTransport();
        providerTransport.AddSIPChannel(new SIPUDPChannel(IPAddress.Loopback, 0));
        int providerPort = providerTransport.GetSIPChannels().Single().ListeningSIPEndPoint.GetIPEndPoint().Port;

        var registrationReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        providerTransport.SIPTransportRequestReceived += async (_, _, request) =>
        {
            if (request.Method == SIPMethodsEnum.REGISTER)
            {
                await providerTransport.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null));
                registrationReceived.TrySetResult();
            }
        };

        int endpointPort = GetAvailableUdpPort();
        var rtpPortRange = new RtpPortRange(45000, 45031);
        var config = new SIPEndpointConfig
        {
            LocalSipPort = endpointPort,
            RtpPortRange = rtpPortRange,
            AccountProfiles =
            [
                new SIPRegistrationProfile(
                    Name: "loopback",
                    RegistrarUri: SIPURI.ParseSIPURI($"sip:127.0.0.1:{providerPort};transport=udp"),
                    Transport: SIPProtocolsEnum.udp,
                    Username: "endpoint",
                    Password: "test-password",
                    Realm: null,
                    OutboundProxy: null,
                    ContactHost: null,
                    RegisterFailureRetryIntervalSeconds: 1)
            ]
        };

        SIPEndpointConfig.Validate(config);

        SIPEndpointService? endpoint = new SIPEndpointService(config, loggerFactory);
        using var callerATransport = CreateLoopbackTransport();
        using var callerBTransport = CreateLoopbackTransport();
        using var incompatibleCallerTransport = CreateLoopbackTransport();
        var callerA = new SIPUserAgent(callerATransport, null);
        var callerB = new SIPUserAgent(callerBTransport, null);
        var incompatibleCaller = new SIPUserAgent(incompatibleCallerTransport, null);

        try
        {
            endpoint.Start();
            await registrationReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => endpoint.IsRegistrationConnected, TimeSpan.FromSeconds(5));

            string destination = $"sip:127.0.0.1:{endpointPort};transport=udp";
            Task<bool> callA = callerA.Call(destination, null, null, new TestMediaSession());
            Task<bool> callB = callerB.Call(destination, null, null, new TestMediaSession());

            bool[] callResults = await Task.WhenAll(callA, callB);
            Assert.All(callResults, Assert.True);
            await WaitUntilAsync(() => endpoint.ActiveCallCount == 2, TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => endpoint.TrackedCallTaskCount == 2, TimeSpan.FromSeconds(5));

            int rtpPortA = GetAudioPort(callerA.Dialogue.RemoteSDP);
            int rtpPortB = GetAudioPort(callerB.Dialogue.RemoteSDP);
            Assert.NotEqual(rtpPortA, rtpPortB);
            Assert.InRange(rtpPortA, rtpPortRange.StartPort, rtpPortRange.EndPort);
            Assert.InRange(rtpPortB, rtpPortRange.StartPort, rtpPortRange.EndPort);

            callerA.Hangup();
            await WaitUntilAsync(() => endpoint.ActiveCallCount == 1, TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => endpoint.TrackedCallTaskCount == 1, TimeSpan.FromSeconds(5));
            Assert.True(callerB.IsCallActive);

            bool incompatibleCallResult = await incompatibleCaller.Call(
                destination,
                null,
                null,
                new TestMediaSession(SDPWellKnownMediaFormatsEnum.GSM));
            Assert.False(incompatibleCallResult);
            await WaitUntilAsync(() => endpoint.ActiveCallCount == 1, TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => endpoint.TrackedCallTaskCount == 1, TimeSpan.FromSeconds(5));
            Assert.True(callerB.IsCallActive);

            await endpoint.DisposeAsync();
            Assert.Equal(0, endpoint.ActiveCallCount);
            Assert.Equal(0, endpoint.TrackedCallTaskCount);
            endpoint = null;
            await WaitUntilAsync(() => !callerB.IsCallActive, TimeSpan.FromSeconds(5));
        }
        finally
        {
            endpoint?.Dispose();
            callerA.Dispose();
            callerB.Dispose();
            incompatibleCaller.Dispose();
        }
    }

    private static SIPTransport CreateLoopbackTransport()
    {
        var transport = new SIPTransport();
        transport.AddSIPChannel(new SIPUDPChannel(IPAddress.Loopback, 0));
        return transport;
    }

    private static int GetAvailableUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static int GetAudioPort(string remoteSdp)
    {
        SDP sdp = SDP.ParseSDPDescription(remoteSdp);
        return sdp.Media.Single(x => x.Media == SDPMediaTypesEnum.audio).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(predicate(), $"Condition was not met within {timeout}.");
    }

    private sealed class TestMediaSession : IMediaSession
    {
        private const string RTP_MEDIA_PROFILE = "RTP/AVP";
        private readonly SDPWellKnownMediaFormatsEnum _audioFormat;

        public TestMediaSession(SDPWellKnownMediaFormatsEnum audioFormat = SDPWellKnownMediaFormatsEnum.PCMU)
        {
            _audioFormat = audioFormat;
        }

        public SDP? RemoteDescription { get; private set; }

        public bool IsClosed { get; private set; }

        public bool HasAudio => true;

        public bool HasVideo => false;

        public bool HasText => false;

        public IPAddress? RtpBindAddress => null;

#pragma warning disable CS0067
        public event Action<string>? OnRtpClosed;
        public event Action<IPEndPoint, RTPEvent, RTPHeader>? OnRtpEvent;
        public event Action<SDPMediaTypesEnum>? OnTimeout;
#pragma warning restore CS0067

        public SDP CreateOffer(IPAddress? connectionAddress = null) => CreateSdp(connectionAddress);

        public SDP CreateAnswer(IPAddress? connectionAddress = null) => CreateSdp(connectionAddress);

        public SetDescriptionResultEnum SetRemoteDescription(SdpType sdpType, SDP sessionDescription)
        {
            RemoteDescription = sessionDescription;
            return SetDescriptionResultEnum.OK;
        }

        public Task Start() => Task.CompletedTask;

        public void SetMediaStreamStatus(SDPMediaTypesEnum kind, MediaStreamStatusEnum status)
        { }

        public Task SendDtmf(byte tone, CancellationToken ct) => Task.CompletedTask;

        public void Close(string reason)
        {
            IsClosed = true;
        }

        private SDP CreateSdp(IPAddress? connectionAddress)
        {
            var sdp = new SDP(IPAddress.Loopback)
            {
                SessionId = "1",
                Connection = new SDPConnectionInformation(connectionAddress ?? IPAddress.Loopback)
            };
            var audio = new SDPMediaAnnouncement(
                SDPMediaTypesEnum.audio,
                1234,
                [new SDPAudioVideoMediaFormat(_audioFormat)])
            {
                Transport = RTP_MEDIA_PROFILE
            };
            sdp.Media.Add(audio);
            return sdp;
        }
    }
}
