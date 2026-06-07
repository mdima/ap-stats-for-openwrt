# Running behind a reverse proxy

> **None of the variables on this page are required.** With nothing set, the app
> behaves exactly as it does out of the box — first-run wizard, `/data` + `/logs`
> volumes, visitor-gated polling. These knobs exist only for advanced
> deployments behind a reverse proxy or TLS terminator.

## HTTP-on-LAN is first-class

Deployment over plain HTTP inside a trusted LAN is a first-class, supported
configuration. TLS is the reverse proxy's responsibility — so the app emits
**no** `Strict-Transport-Security` (HSTS) header, and its Content-Security-Policy
carries **no** `upgrade-insecure-requests` directive. Nothing forces the browser
to upgrade an in-LAN HTTP session to HTTPS. If you need protection on the wire,
terminate TLS at a reverse proxy (nginx, Traefik, Caddy, …) and put the app
behind it.

## Trusting forwarded headers

By default the app derives the client IP from the **actual TCP peer** and
**ignores** any `X-Forwarded-For` / `X-Forwarded-Proto` header — so a forged
`X-Forwarded-For` cannot spoof the source IP that the login rate-limiter keys on.
If you put the app behind a reverse proxy, declare which proxies to trust so
their forwarded headers are honored:

| Variable | Value | Effect |
|---|---|---|
| `ForwardedHeaders__KnownProxies` | comma-separated **IP addresses** (e.g. `10.0.0.2,10.0.0.3`) | Honor `X-Forwarded-For` / `-Proto` from these proxy IPs. |
| `ForwardedHeaders__KnownNetworks` | comma-separated **CIDR ranges** (e.g. `10.0.0.0/24`) | Honor forwarded headers from any peer inside these ranges. |

- Set **either, both, or neither.** Setting at least one turns the
  forwarded-headers middleware **on**; the default (neither set) leaves it
  **off** entirely.
- **Fail-fast:** an invalid IP or CIDR makes the container **refuse to start**
  and names the offending value in the log — a typo cannot silently weaken the
  trust list.
- At boot the app logs its posture:
  `ForwardedHeaders middleware: mode=disabled|enabled trustedProxies=N trustedNetworks=M`.

```yaml
    # docker-compose.yml — trust a LAN reverse-proxy subnet
    environment:
      - ForwardedHeaders__KnownNetworks=10.0.0.0/24
```

### Secure cookie behaviour

When the forwarded-headers middleware is **enabled** (you have declared at least
one trusted proxy or network), the authentication cookie's secure policy is
automatically promoted to `Always` — so once a proxy terminates TLS, the session
cookie is only ever sent back over the secure leg. With no proxy declared
(pure HTTP-on-LAN), the cookie policy follows the request scheme. You do not set
this directly; it tracks the `ForwardedHeaders__*` configuration above.

## Tightening the Content-Security-Policy connect-src

The Blazor InteractiveServer UI opens a SignalR WebSocket back to its own origin,
so the default Content-Security-Policy is `connect-src 'self'`. If your reverse
proxy serves the app from a different origin than the browser connects to — or
you front it with a CDN/edge that needs an extra origin allow-listed — declare
the extra origins with `Csp__ConnectSrc`:

| Variable | Value | Effect |
|---|---|---|
| `Csp__ConnectSrc` | comma-separated origins (e.g. `https://ap.example.lan`) | **Appends** the listed origins to `connect-src 'self'` in the CSP. |

- The listed origins are **appended** to `connect-src 'self'`; `'self'` is always
  retained, so the same-origin SignalR circuit keeps working.
- **Fail-loud:** a malformed origin value throws at startup and the container
  refuses to boot, mirroring the `ForwardedHeaders__KnownProxies` /
  `KnownNetworks` throw-on-invalid behaviour above. A misconfigured security knob
  never silently degrades.

## Other optional variables

| Variable | Default | Purpose |
|---|---|---|
| `DATA_ROOT` | `/data` | Storage root for the SQLite DB + Data Protection keyring. Mainly for non-container / local-dev runs; Docker deployments just use the `/data` volume. |
| `LOGS_ROOT` | `/logs` | Directory for the Serilog daily-rolling log files. |
| `Polling__GateMode` | *(unset)* | Set to `AlwaysLive` to **disable** the visitor gate so the poller runs continuously even with no browser open. Intended for headless monitoring or smoke tests; leave unset for normal use. |

See also: [Threat model](threat-model.md) · [First-boot walkthrough](first-boot.md).
