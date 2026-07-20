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

### Authoritative YAML configuration

Use `--config <path>` (or `-c <path>`) to load one SIP2Agent YAML file. This is an exclusive form: it must be the only command-line option, so it cannot be combined with `--help`, `--verbose`, or any other option. In YAML mode this file is authoritative: `S2A_*` environment variables and ordinary command-line settings are not read or merged. The existing direct command-line mode retains its environment-variable fallback.

The configuration file must exist, use a `.yaml` or `.yml` extension, and contain exactly one mapping document. Keys are lower snake case and unknown, incorrectly cased, duplicate, tagged, aliased, and incorrectly typed values are rejected.

```yaml
endpoint:
  local_sip_port: 0              # default 0: OS-selected SIP port
  contact_host: "203.0.113.10"

registration:
  registrar: "pbx.example.com"  # registrar and username are required if this group exists
  transport: tls                 # default tls; udp is also supported
  username: "alice"
  realm: "example.com"
  outbound_proxy: "udp:127.0.0.1:5060"
  max_reconnects: 5              # default 5
  register_retry_interval_seconds: 30 # default 30
  credentials:                   # optional for unauthenticated registration
    password_file: "./secrets/sip-password.txt"
    # Exactly one credential source may be supplied instead:
    # password: "secret"
    # digest_store_file: "./secrets/sip-digests.txt"

media:
  rtp_port_range: "10000-10019" # optional; otherwise OS-selected RTP/RTCP ports
  answer_audio_file: "./audio/answer.raw"

runtime:
  headless: true                 # default false
  verbose: false                 # default false

lib_rtic:
  api_config_path: "./rtic_api.yaml"
  session_config_path: "./rtic_session.yaml" # optional
```

All groups are optional. SIP2Agent preserves its normal defaults: OS-selected SIP and RTP ports, TLS transport, five reconnects, a 30-second registration retry, non-headless/non-verbose execution, and `AcceptRtpFromAny = true`. The deprecated fixed `rtp_port` setting is intentionally not part of the YAML schema.

Paths for `password_file`, `digest_store_file`, `answer_audio_file`, and both `lib_rtic` files are resolved relative to the primary YAML file and stored as absolute normalized paths. The referenced LibRTIC API file is required when `lib_rtic` is present; its session file is optional. Both must exist and have a YAML extension. SIP2Agent validates only those paths—it deliberately does not parse their contents or reference LibRTIC configuration types.

Inline `password` is provided for command-line parity, but file-based or digest-store credentials are recommended. Password values are never included in YAML diagnostics or logs.

## Dependencies and Third Party Notices

Third-party notices are recorded in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
