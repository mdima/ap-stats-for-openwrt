# AP Stats for OpenWrt

AP Stats for OpenWrt is a self-hosted Docker dashboard that gives operators
of OpenWRT-based access points a live view of every WiFi station currently
associated with every monitored WLAN across every declared AP. The native
LuCI UI in AP mode does not surface this information cleanly across multiple
APs and WLANs, so this dashboard fills that gap. The application is delivered
as a single Docker container that polls each declared AP over SSH and renders
the results through a Blazor live UI.

## How to run

```sh
docker run -d \
  -p 8080:8080 \
  -v /local/data:/data \
  -v /local/logs:/logs \
  apstatsforopenwrt:latest
```

- `/data` — SQLite database at `/data/AP-Stats-for-OpenWrt.db` plus the
  ASP.NET Data Protection keyring under `/data/keys`. Persist this volume
  to keep settings, the onboarding state, and the encrypted SSH credentials
  across container restarts.
- `/logs` — Serilog rolling files (`/logs/ap-stats-for-openwrt-*.log`),
  7-day retention. Optional but recommended for operational visibility.
- No environment variables are required for first boot. Visit
  `http://localhost:8080` and the onboarding wizard will guide you through
  setting the UI password and declaring your first OpenWRT access point.

The container exposes a plain HTTP `/health` endpoint on port 8080 for
liveness and readiness probes; it returns the locked JSON shape
`{ status, components: { db, dataProtection }, lastPollAge }` and does not
count toward the visitor gate that arms the SSH polling cycle.
