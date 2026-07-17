using SIPSorcery.SIP;

namespace SIP2Agent.UserAgentService;

public sealed record SIPRegistrationProfile(
    string Name,
    SIPURI RegistrarUri,
    SIPProtocolsEnum Transport,
    string Username,
    string? Password,
    string? Realm,
    SIPEndPoint? OutboundProxy,
    string? ContactHost,
    int ExpirySeconds = 120,
    int RegistrationAttemptTimeoutSeconds = 60,
    int RegisterFailureRetryIntervalSeconds = 30,
    int MaxRegisterAttempts = 3,
    int MaxReconnectCycles = 5,
    bool Register = true,
    bool SendUsernameInContactHeader = true,
    SIPDigestStore? DigestStore = null);
