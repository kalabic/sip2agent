using SIPSorceryExt;
using SIPSorcery.SIP;
using System.Net;
using Xunit;

namespace SIP2Agent.UserAgentService.Tests;

public sealed class SIPRegistrationUserAgentIntegrationTest
{
    private const string Username = "alice";
    private const string Realm = "loopback.example";
    private const string Password = "password123";

    [Fact]
    public async Task PasswordAuthenticationStillSucceeds()
    {
        using var providerTransport = CreateLoopbackTransport();
        using var clientTransport = CreateLoopbackTransport();
        SIPEndPoint providerEndPoint = GetListeningEndPoint(providerTransport);
        var authenticatedRequest = new TaskCompletionSource<SIPRequest>(TaskCreationOptions.RunContinuationsAsynchronously);

        providerTransport.SIPTransportRequestReceived += async (_, _, request) =>
        {
            if (!request.Header.HasAuthenticationHeader)
            {
                await providerTransport.SendResponseAsync(CreateChallenge(request, DigestAlgorithmsEnum.MD5));
            }
            else
            {
                authenticatedRequest.TrySetResult(request);
                await providerTransport.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null));
            }
        };

        SIPRegistrationUserAgent agent = CreateAgent(clientTransport, providerEndPoint, Password, null);
        try
        {
            Task<SIPResponse> registered = GetRegistrationSuccess(agent);
            agent.Start();

            await registered.WaitAsync(TimeSpan.FromSeconds(5));
            SIPAuthorisationDigest digest = (await authenticatedRequest.Task.WaitAsync(TimeSpan.FromSeconds(5)))
                .Header.AuthenticationHeaders.Single().SIPDigest;

            Assert.True(agent.IsRegistered);
            Assert.Equal(DigestAlgorithmsEnum.MD5, digest.DigestAlgorithm);
            AssertDigestResponse(digest, DigestAlgorithmsEnum.MD5);
        }
        finally
        {
            agent.Stop(false);
        }
    }

    [Theory]
    [InlineData(DigestAlgorithmsEnum.MD5)]
    [InlineData(DigestAlgorithmsEnum.SHA256)]
    public async Task DigestStoreAuthenticationSucceeds(DigestAlgorithmsEnum algorithm)
    {
        using var providerTransport = CreateLoopbackTransport();
        using var clientTransport = CreateLoopbackTransport();
        SIPEndPoint providerEndPoint = GetListeningEndPoint(providerTransport);
        var authenticatedRequest = new TaskCompletionSource<SIPRequest>(TaskCreationOptions.RunContinuationsAsynchronously);

        providerTransport.SIPTransportRequestReceived += async (_, _, request) =>
        {
            if (!request.Header.HasAuthenticationHeader)
            {
                await providerTransport.SendResponseAsync(CreateChallenge(request, algorithm));
            }
            else
            {
                authenticatedRequest.TrySetResult(request);
                await providerTransport.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null));
            }
        };

        SIPDigestStore store = SIPDigestStore.NewFromPassword(Username, Realm, Password);
        SIPRegistrationUserAgent agent = CreateAgent(clientTransport, providerEndPoint, null, store);
        try
        {
            Task<SIPResponse> registered = GetRegistrationSuccess(agent);
            agent.Start();

            await registered.WaitAsync(TimeSpan.FromSeconds(5));
            SIPAuthorisationDigest digest = (await authenticatedRequest.Task.WaitAsync(TimeSpan.FromSeconds(5)))
                .Header.AuthenticationHeaders.Single().SIPDigest;

            Assert.Equal(algorithm, digest.DigestAlgorithm);
            AssertDigestResponse(digest, algorithm);
        }
        finally
        {
            agent.Stop(false);
        }
    }

    [Theory]
    [InlineData(true, DigestAlgorithmsEnum.SHA256)]
    [InlineData(false, DigestAlgorithmsEnum.MD5)]
    public async Task DigestStorePrefersSha256AndFallsBackToMd5(
        bool includeSha256Credential,
        DigestAlgorithmsEnum expectedAlgorithm)
    {
        using var providerTransport = CreateLoopbackTransport();
        using var clientTransport = CreateLoopbackTransport();
        SIPEndPoint providerEndPoint = GetListeningEndPoint(providerTransport);
        var usedAlgorithm = new TaskCompletionSource<DigestAlgorithmsEnum>(TaskCreationOptions.RunContinuationsAsynchronously);

        providerTransport.SIPTransportRequestReceived += async (_, _, request) =>
        {
            if (!request.Header.HasAuthenticationHeader)
            {
                await providerTransport.SendResponseAsync(CreateChallenge(
                    request,
                    DigestAlgorithmsEnum.MD5,
                    DigestAlgorithmsEnum.SHA256));
            }
            else
            {
                SIPAuthorisationDigest digest = request.Header.AuthenticationHeaders.Single().SIPDigest;
                usedAlgorithm.TrySetResult(digest.DigestAlgorithm);
                await providerTransport.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null));
            }
        };

        SIPDigestStore fullStore = SIPDigestStore.NewFromPassword(Username, Realm, Password);
        var store = new SIPDigestStore(fullStore.HA1MD5, includeSha256Credential ? fullStore.HA1SHA256 : null);
        SIPRegistrationUserAgent agent = CreateAgent(clientTransport, providerEndPoint, null, store);
        try
        {
            Task<SIPResponse> registered = GetRegistrationSuccess(agent);
            agent.Start();

            await registered.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(expectedAlgorithm, await usedAlgorithm.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            agent.Stop(false);
        }
    }

    [Fact]
    public async Task StopSendsAuthenticatedZeroExpiryRegistration()
    {
        using var providerTransport = CreateLoopbackTransport();
        using var clientTransport = CreateLoopbackTransport();
        SIPEndPoint providerEndPoint = GetListeningEndPoint(providerTransport);
        var zeroExpiryRequest = new TaskCompletionSource<SIPRequest>(TaskCreationOptions.RunContinuationsAsynchronously);

        providerTransport.SIPTransportRequestReceived += async (_, _, request) =>
        {
            if (!request.Header.HasAuthenticationHeader)
            {
                await providerTransport.SendResponseAsync(CreateChallenge(request, DigestAlgorithmsEnum.SHA256));
            }
            else
            {
                if (request.Header.Expires == 0)
                {
                    zeroExpiryRequest.TrySetResult(request);
                }

                await providerTransport.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null));
            }
        };

        SIPRegistrationUserAgent agent = CreateAgent(
            clientTransport,
            providerEndPoint,
            null,
            SIPDigestStore.NewFromPassword(Username, Realm, Password));
        Task<SIPResponse> registered = GetRegistrationSuccess(agent);
        agent.Start();

        await registered.WaitAsync(TimeSpan.FromSeconds(5));
        agent.Stop();

        SIPRequest unregister = await zeroExpiryRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, unregister.Header.Expires);
    }

    private static SIPRegistrationUserAgent CreateAgent(
        SIPTransport clientTransport,
        SIPEndPoint providerEndPoint,
        string? password,
        SIPDigestStore? digestStore,
        int expiry = 120)
    {
        SIPURI accountAor = SIPURI.ParseSIPURI($"sip:{Username}@127.0.0.1:{providerEndPoint.Port}");
        string registrar = $"sip:127.0.0.1:{providerEndPoint.Port};transport=udp";
        var contact = new SIPURI(SIPSchemesEnum.sip, IPAddress.Any, 0) { User = Username };

        return digestStore != null
            ? new SIPRegistrationUserAgent(
                clientTransport,
                providerEndPoint,
                accountAor,
                digestStore.GetHA1Digest,
                Username,
                Realm,
                registrar,
                contact,
                expiry,
                null,
                maxRegistrationAttemptTimeout: 2,
                registerFailureRetryInterval: 1)
            : new SIPRegistrationUserAgent(
                clientTransport,
                providerEndPoint,
                accountAor,
                Username,
                password!,
                Realm,
                registrar,
                contact,
                expiry,
                null,
                maxRegistrationAttemptTimeout: 2,
                registerFailureRetryInterval: 1);
    }

    private static Task<SIPResponse> GetRegistrationSuccess(SIPRegistrationUserAgent agent)
    {
        var completion = new TaskCompletionSource<SIPResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.RegistrationSuccessful += (_, response) => completion.TrySetResult(response);
        return completion.Task;
    }

    private static SIPResponse CreateChallenge(SIPRequest request, params DigestAlgorithmsEnum[] algorithms)
    {
        SIPResponse response = SIPResponse.GetResponse(
            request,
            SIPResponseStatusCodesEnum.Unauthorised,
            "Authentication Required");

        foreach (DigestAlgorithmsEnum algorithm in algorithms)
        {
            var authenticationHeader = new SIPAuthenticationHeader(
                SIPAuthorisationHeadersEnum.WWWAuthenticate,
                Realm,
                $"nonce-{algorithm}");
            authenticationHeader.SIPDigest = null;
            string algorithmName = algorithm == DigestAlgorithmsEnum.SHA256 ? "SHA-256" : "MD5";
            authenticationHeader.Value =
                $"Digest realm=\"{Realm}\",nonce=\"nonce-{algorithm}\",qop=\"auth\",algorithm={algorithmName}";
            response.Header.AuthenticationHeaders.Add(authenticationHeader);
        }

        return response;
    }

    private static void AssertDigestResponse(SIPAuthorisationDigest digest, DigestAlgorithmsEnum algorithm)
    {
        Assert.Equal(CalculateExpectedDigestResponse(digest, algorithm), digest.Response);
    }

    private static string CalculateExpectedDigestResponse(
        SIPAuthorisationDigest digest,
        DigestAlgorithmsEnum algorithm)
    {
        string ha1 = HTTPDigest.DigestCalcHA1(Username, Realm, Password, algorithm);
        return HTTPDigest.DigestCalcResponse(
            ha1,
            digest.URI,
            digest.Nonce,
            digest.NonceCount == 0 ? null : digest.NonceCount.ToString().PadLeft(8, '0'),
            digest.Cnonce,
            digest.Qop,
            SIPMethodsEnum.REGISTER.ToString(),
            algorithm);
    }

    private static SIPTransport CreateLoopbackTransport()
    {
        var transport = new SIPTransport();
        transport.AddSIPChannel(new SIPUDPChannel(IPAddress.Loopback, 0));
        return transport;
    }

    private static SIPEndPoint GetListeningEndPoint(SIPTransport transport)
    {
        int port = transport.GetSIPChannels().Single().ListeningSIPEndPoint.GetIPEndPoint().Port;
        return new SIPEndPoint(SIPProtocolsEnum.udp, IPAddress.Loopback, port);
    }
}
