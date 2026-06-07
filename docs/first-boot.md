# First-boot walkthrough

The first time you open AP Stats for OpenWrt, a setup wizard takes over. There
is no `config.yml`, no `.env`, and no first-time CLI dance — everything is
configured through the UI.

## Volumes & ports

| Mount / Port | Purpose | Persist? |
|---|---|---|
| `/data` | SQLite database at `/data/AP-Stats-for-OpenWrt.db` and the ASP.NET Data Protection keyring at `/data/keys`. Holds your APs, the UI password hash, the encrypted SSH credentials, and the operator's onboarding state. | **Yes — required.** |
| `/logs` | Serilog daily-rolling files (`/logs/ap-stats-for-openwrt-*.log`), 7-day retention. | Optional; recommended. |
| `8080/tcp` | HTTP — Blazor UI + `/health` JSON endpoint. | n/a |

> **Named volumes vs bind mounts:** named volumes (the Compose default) inherit
> the in-image `chown -R app:app` cleanly. If you use a bind mount on Linux,
> `chown -R 1654:1654` the host path first or the container's non-root user will
> not be able to write.

## The setup wizard

1. Visit `http://localhost:8080`.
2. The wizard asks you to set an optional UI password (PBKDF2-hashed; skip it to
   leave the site open inside your LAN).
3. Declare your first OpenWRT AP: hostname or IP, SSH username, SSH password.
   The password is encrypted at rest immediately.
4. Pick which WLANs on that AP you want to monitor.
5. You land on the live dashboard. New APs and per-WLAN toggles are managed from
   the Settings page afterwards.

The setup wizard stays anonymously reachable only until setup is *genuinely
complete* — a password is set **and** at least one AP exists. Once both are
true, the password-write endpoint refuses an anonymous re-submission, so the
onboarding surface cannot be hijacked after the fact.

## Health & observability

The container exposes a plain HTTP `/health` endpoint on port 8080 for liveness
and readiness probes. The response shape is locked:

```json
{
  "status": "Healthy",
  "components": { "db": "Healthy", "dataProtection": "Healthy" },
  "lastPollAge": "00:00:14.3210000"
}
```

- The endpoint does **not** count toward the visitor gate, so external monitors
  will not keep the polling cycle armed.
- Serilog writes structured logs to both stdout (consumed by `docker logs`) and
  `/logs/ap-stats-for-openwrt-*.log` (daily-rolling, 7-day retention, compact
  JSON formatter).

## Access-point requirements

- **OpenWRT** with the standard CLI surface — `iwinfo`, `iw dev`, `ip neigh
  show`. The SSH transport speaks Dropbear cleanly without any manual cipher
  configuration.
- **SSH access** for a user that can run `iwinfo` and `iw dev`. Read-only is
  sufficient for discovery; the optional *Disconnect station* action
  additionally requires the SSH user to be able to run the deauth command.
- **DHCP server location is up to you.** If your AP runs `dnsmasq` and serves
  DHCP, `ip neigh show` plus reverse-DNS will resolve hostnames cleanly; if your
  DHCP server lives elsewhere, IP/hostname discovery is best-effort and may leave
  some fields blank — a known and accepted limitation.

See also: [Running behind a reverse proxy](reverse-proxy.md) ·
[Dropbear & SubnetScanRanges](dropbear-subnet-note.md) ·
[Threat model](threat-model.md).
