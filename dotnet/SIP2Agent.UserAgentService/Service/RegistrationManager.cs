using System.Net;
using Microsoft.Extensions.Logging;
using SIPSorceryExt;
using SIPSorcery.SIP;

namespace SIP2Agent.UserAgentService.Service;


internal sealed class RegistrationManager : IDisposable
{
    private readonly object _stateLock = new();
    private readonly ILogger _logger;
    private readonly SIPEndpointConfig _config;
    private readonly SIPTransport _sipTransport;

    private SIPRegistrationUserAgent? _registrationUserAgent;
    private SIPRegistrationProfile? _activeProfile;
    private SIPEndPoint? _providerEndPoint;

    private int _temporaryFailureCycles;
    private bool _disposed;

    public RegistrationManager(SIPEndpointConfig config, SIPTransport sipTransport, ILoggerFactory loggerFactory)
    {
        _config = config;
        _sipTransport = sipTransport;
        _logger = loggerFactory.CreateLogger<RegistrationManager>();
    }

    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return _registrationUserAgent?.IsRegistered == true;
            }
        }
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RegistrationManager));
            }

            SIPRegistrationProfile? profile = _config.AccountProfiles.FirstOrDefault(x => x.Register);
            if (profile == null)
            {
                StopRegistration(false, "No SIP registrar profile is configured.");
                _temporaryFailureCycles = 0;
                _activeProfile = null;
                return;
            }

            StopRegistration(false, "Replacing the current SIP registration.");
            _logger.LogInformation(
                "Registering account {Username} with {RegistrarUri} over {Transport}.",
                profile.Username,
                profile.RegistrarUri,
                profile.Transport);

            _temporaryFailureCycles = 0;
            _activeProfile = profile;

            _registrationUserAgent = CreateRegistrationUserAgent(profile);
            _registrationUserAgent.Start();
        }
    }

    public void Disconnect()
    {
        StopRegistration(true, "Registration manually disconnected.");
    }

    public bool IsRequestFromProvider(SIPEndPoint remoteEndPoint)
    {
        if (remoteEndPoint?.Address == null)
        {
            return false;
        }

        SIPEndPoint? providerEndPoint;
        lock (_stateLock)
        {
            providerEndPoint = _providerEndPoint;
        }

        if (providerEndPoint?.Address == null)
        {
            return false;
        }

        IPAddress remoteAddress = remoteEndPoint.Address.IsIPv4MappedToIPv6 ? remoteEndPoint.Address.MapToIPv4() : remoteEndPoint.Address;
        IPAddress providerAddress = providerEndPoint.Address.IsIPv4MappedToIPv6 ? providerEndPoint.Address.MapToIPv4() : providerEndPoint.Address;

        return remoteAddress.Equals(providerAddress);
    }

    public void LogStatus()
    {
        lock (_stateLock)
        {
            if (!_disposed)
            {
                string status = _registrationUserAgent is null
                    ? "disconnected"
                    : _registrationUserAgent.IsRegistered
                        ? "connected"
                        : _temporaryFailureCycles > 0 ? "retrying" : "registering";
                _logger.LogInformation("Registration is {Status}. Provider: {Provider}. Reconnect failures: {Failures}.",
                    status,
                    _activeProfile == null ? "<none>" : $"{_activeProfile.RegistrarUri} over {_activeProfile.Transport}",
                    _temporaryFailureCycles);
            }
            else
            {
                _logger.LogInformation("Object is disposed.");
            }
        }
    }

    private SIPRegistrationUserAgent CreateRegistrationUserAgent(SIPRegistrationProfile profile)
    {
        SIPURI sipAccountAor = profile.RegistrarUri.CopyOf();
        sipAccountAor.User = profile.Username;

        SIPURI contactUri = string.IsNullOrWhiteSpace(profile.ContactHost)
            ? new SIPURI(sipAccountAor.Scheme, IPAddress.Any, 0)
            : new SIPURI(profile.SendUsernameInContactHeader ? profile.Username : null, profile.ContactHost, null, sipAccountAor.Scheme);

        if (profile.SendUsernameInContactHeader)
        {
            contactUri.User = profile.Username;
        }

        SIPRegistrationUserAgent registrationUserAgent = profile.DigestStore != null
            ? new SIPRegistrationUserAgent(
                _sipTransport,
                profile.OutboundProxy,
                sipAccountAor,
                profile.DigestStore.GetHA1Digest,
                profile.Username,
                profile.Realm,
                profile.RegistrarUri.ToString(),
                contactUri,
                profile.ExpirySeconds,
                customHeaders: null,
                maxRegistrationAttemptTimeout: profile.RegistrationAttemptTimeoutSeconds,
                registerFailureRetryInterval: profile.RegisterFailureRetryIntervalSeconds,
                maxRegisterAttempts: profile.MaxRegisterAttempts,
                exitOnUnequivocalFailure: true)
            : new SIPRegistrationUserAgent(
                _sipTransport,
                profile.OutboundProxy,
                sipAccountAor,
                profile.Username,
                profile.Password!,
                profile.Realm,
                profile.RegistrarUri.ToString(),
                contactUri,
                profile.ExpirySeconds,
                customHeaders: null,
                maxRegistrationAttemptTimeout: profile.RegistrationAttemptTimeoutSeconds,
                registerFailureRetryInterval: profile.RegisterFailureRetryIntervalSeconds,
                maxRegisterAttempts: profile.MaxRegisterAttempts,
                exitOnUnequivocalFailure: true);

        registrationUserAgent.RegistrationSuccessful += OnRegistrationSuccessful;
        registrationUserAgent.RegistrationTemporaryFailure += OnRegistrationTemporaryFailure;
        registrationUserAgent.RegistrationFailed += OnRegistrationFailed;
        registrationUserAgent.RegistrationRemoved += OnRegistrationRemoved;

        return registrationUserAgent;
    }

    private void OnRegistrationSuccessful(SIPURI uri, SIPResponse response)
    {
        lock(_stateLock)
        {
            _temporaryFailureCycles = 0;

            SIPEndPoint? providerEndPoint = response?.RemoteSIPEndPoint;
            if (providerEndPoint?.Address == null)
            {
                _logger.LogWarning("Registration succeeded for {Uri} but the response did not include a remote SIP endpoint.", uri);

                _logger.LogInformation("Registration connected for {Uri}.", uri);
            }
            else
            {
                _providerEndPoint = providerEndPoint.CopyOf();
                _logger.LogInformation("Configured provider endpoint set to {ProviderEndPoint} from registration response.", _providerEndPoint);

                _logger.LogInformation("Registration connected for {Uri}.", uri);
            }
        }
    }

    private void OnRegistrationTemporaryFailure(SIPURI uri, SIPResponse response, string error)
    {
        lock (_stateLock)
        {
            _temporaryFailureCycles++;
            int maxReconnectCycles = _activeProfile?.MaxReconnectCycles ?? 0;

            if (_temporaryFailureCycles > maxReconnectCycles)
            {
                StopRegistration(false, $"Registration retry budget exhausted for {uri}. Last error: {error}");
            }
            else
            {
                _logger.LogInformation(
                    "Registration retrying after temporary failure {FailureCycle}/{MaxReconnectCycles} for {Uri}: {Error}",
                    _temporaryFailureCycles,
                    maxReconnectCycles,
                    uri,
                    error);
            }
        }
    }

    private void OnRegistrationFailed(SIPURI uri, SIPResponse response, string error)
    {
        StopRegistration(false, $"Registration failed for {uri}: {error}");
    }

    private void OnRegistrationRemoved(SIPURI uri, SIPResponse response)
    {
        StopRegistration(false, $"Registration removed for {uri}.");
    }

    private void StopRegistration(bool sendZeroExpiryRegister, string reason)
    {
        lock (_stateLock)
        {
            SIPRegistrationUserAgent? registrationUserAgent = _registrationUserAgent;
            _registrationUserAgent = null;
            if (registrationUserAgent != null)
            {
                registrationUserAgent.RegistrationSuccessful -= OnRegistrationSuccessful;
                registrationUserAgent.RegistrationTemporaryFailure -= OnRegistrationTemporaryFailure;
                registrationUserAgent.RegistrationFailed -= OnRegistrationFailed;
                registrationUserAgent.RegistrationRemoved -= OnRegistrationRemoved;

                registrationUserAgent.Stop(sendZeroExpiryRegister);
            }

            _providerEndPoint = null;
            _logger.LogInformation("Registration disconnected. {Reason}", reason);
        }
    }

    public void Dispose()
    {
        lock(_stateLock)
        {
            if (!_disposed)
            {
                _disposed = true;

                try
                {
                    StopRegistration(true, "Object disposed.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception occured during object disposal.");
                }
            }
        }
    }
}
