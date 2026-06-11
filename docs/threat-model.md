# Threat model

> **Deployment posture:** this is a LAN-side management tool for routers you
> already operate — a trusted-network deployment, **not an internet-facing
> service.** The hardening below raises the bar against opportunistic LAN access
> and misconfiguration; it does not assume a hostile network path. If you need
> protection on the wire, terminate TLS at a reverse proxy (see
> [Running behind a reverse proxy](reverse-proxy.md)).

## Documented boundary: the Data Protection keyring is plaintext on `/data`

AP passwords are encrypted at rest with ASP.NET Data Protection. The encryption
keyring itself is persisted, **unencrypted, under `/data/keys`** (with
`SetApplicationName("ApStatsForOpenWrt")`). This is a deliberate, documented
boundary, not an oversight:

- **Whoever can read the `/data` volume can decrypt the stored node passwords.**
  The keyring sits in plaintext next to the encrypted data it protects, so an
  actor with read access to `/data` has both the ciphertext and the key.
- This is acceptable **only** because the deployment model is a trusted LAN host
  you control. Treat read access to the `/data` volume as equivalent to holding
  every monitored AP's SSH credentials.
- **Protect `/data` accordingly:** restrict host-level access to the volume,
  do not expose it on a shared/multi-tenant host, and protect your backups of it
  the same way you protect the live volume (see [Backing up /data](backup.md)).
- Surviving container recreation requires keeping the `/data` volume — losing
  `/data/keys` means the stored AP passwords can no longer be decrypted.

## Credentials at rest

- **AP passwords:** encrypted at rest via ASP.NET Data Protection, keyring under
  `/data/keys` (see the boundary above).
- **UI password:** stored as a PBKDF2 hash (`PasswordHasher`) — one-way;
  recoverable only by clearing the row in the SQLite `AppSetting` table.

## Single shared site password — no RBAC

This is a small-scale, self-hosted tool. There is **one shared site password**;
there is **no multi-user / RBAC model**, and there is no email-based password
reset. That is a deliberate scope decision, not a missing feature.

## Outbound traffic: no telemetry, one optional update check

The app sends no usage or telemetry data anywhere. Its only outbound traffic is
SSH to each declared AP — plus, if the update check is left enabled (the
default), one unauthenticated `GET` per day to the public GitHub Releases API to
see whether a newer version has been published. Nothing identifying is sent (a
generic `User-Agent` only), and the feature is notify-only. Toggle it off on the
Settings page for zero outbound HTTP (air-gapped deployments).

## Read-only by design

The polling plane is read-only. The only write-side path against an AP is the
explicit, confirmation-gated *Disconnect station* action, which issues a single
deauth/disassoc command over SSH. That command validates the station MAC and
interface against strict patterns before issuing anything, so neither field can
carry a command-injection payload.

## Hardening summary

- **Login throttling + password policy.** A UI password must be at least 8
  characters and include upper-case, lower-case, and a digit or symbol. Sign-in
  *attempts* are rate-limited to 5 per minute per source IP and 30 per minute
  server-wide; exceeding either ceiling returns HTTP 429. Only `POST` attempts
  count — viewing or reloading the login page is never throttled.
- **Session rotation on password change.** Changing or clearing the UI password
  invalidates every previously issued session cookie on its next request.
- **Anti-takeover onboarding.** All wizard form posts require an antiforgery
  token; the setup surface closes the moment setup is genuinely complete.
- **Baseline response headers.** Every response carries
  `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
  `Referrer-Policy: same-origin`, a `Permissions-Policy` disabling
  geolocation/camera/microphone, and a Content-Security-Policy. Because
  deployment is HTTP-on-LAN, there is intentionally **no**
  `Strict-Transport-Security` and **no** CSP `upgrade-insecure-requests` — see
  [Running behind a reverse proxy](reverse-proxy.md).
- **Reverse-proxy IP trust is opt-in.** Forwarded headers are ignored unless you
  declare trusted proxies/networks, so the rate-limiter's per-IP key cannot be
  spoofed by a forged `X-Forwarded-For`.
- **SSH host-key trust-on-first-use.** The first connection to an AP captures and
  pins its host-key fingerprint. The per-AP detail page shows the full SHA256
  fingerprint with a `ssh-keyscan <host> | ssh-keygen -lf -` recipe to verify it
  out-of-band, plus a per-node **Re-trust** action.
- **SSH algorithm visibility.** After each connection the negotiated
  key-exchange / host-key / cipher / MAC algorithms are logged, with a warning if
  any falls in the SHA-1 family (older Dropbear) so weak APs are easy to spot.
- **Production-environment check.** Starting the container with
  `ASPNETCORE_ENVIRONMENT=Development` logs a startup warning, so an accidentally
  dev-flagged deployment is visible in `docker logs`.

See also: [Backing up /data](backup.md) · [Upgrading](upgrading.md).
