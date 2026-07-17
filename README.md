# SIP2Agent

SIP2Agent is a work in progress toward an AI bridge for a long-running SIP endpoint. The current implementation registers a SIP user agent, admits trusted inbound calls, and creates an independent media session for each call. The agent integration is the next layer to build on this endpoint foundation.


## Projects

- `SIP2Agent.UserAgentService`
  - Contains endpoint configuration, SIP registration, call admission, call lifecycle, RTP allocation, and the per-call media bridge.
  - Supports registration without retaining a clear-text SIP password by using precomputed HA1 digests.
- `SIP2Agent.AgentCli` is the executable console host.
- `SIP2Agent.UserAgentService.Tests` contains configuration tests and a concurrent inbound-call integration test.


### SIP2Agent.AgentCli

```
Usage: (Windows '.exe' example)
  SIP2Agent.AgentCli.exe --registrar <sip-uri-or-host> --username <value> [--realm <value>]
                          [--digest-store <path> | --passwd <value> | --password-file <path>] [options]

Options:
  --registrar <sip-uri-or-host>    SIP registrar/PBX trunk server.
  --transport tls|udp              SIP transport for bare registrar values. Default: tls.
  --realm <value>                  Optional SIP auth realm.
  --username <value>               SIP account username.
  --passwd <value>                 SIP account password in clear text.
  --password-file <path>           File containing the SIP account password.
  --digest-store <path>            File containing precomputed SIP HA1 digest credentials.
  --outbound-proxy <endpoint>      Optional outbound proxy, e.g. udp:127.0.0.1:5060.
  --max-reconnects <count>         Temporary registration failure budget. Default: 5.
  --contact-host <host>            Contact host or public IP advertised in SIP headers.
  --local-sip-port <port>          Local SIP receive port. Default: 0, selected by the OS.
  --rtp-port-range <start-end>     Inclusive even RTP/odd RTCP port range, e.g. 10000-10019.
  --rtp-port <port>                Deprecated fixed RTP port; endpoint startup will reject it.
  --register-retry-interval <sec>  Delay after temporary registration failure. Default: 30.
  --answer-audio-file <path>       Raw 8 kHz audio file to play after answering an inbound call.
  --headless                       Run until Ctrl+C without interactive key commands.
  --verbose, -v                    Enable SIP transport trace logging.
  --help, -h                       Show this help.

Commands:
  digest                           Create a SIP digest-store file. Run 'digest --help' for details.

Create digest file for SIP HA1 authentication:
  SIP2Agent.AgentCli.exe digest --username <value> --realm <value> (--passwd <value> | --password-file <path>) --out <path>
```

## Dependencies and Third Party Notices

Third-party notices are recorded in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
