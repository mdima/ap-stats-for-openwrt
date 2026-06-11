# Documentation

Topic guides for deploying and operating **AP Stats for OpenWrt**. Start with the
first-boot walkthrough; reach for the others as your deployment grows.

| Page | What it covers |
|---|---|
| [First-boot walkthrough](first-boot.md) | The setup wizard, volumes & ports, the `/health` endpoint, and access-point requirements. |
| [Running behind a reverse proxy](reverse-proxy.md) | `ForwardedHeaders` trust, the `Csp__ConnectSrc` knob, secure-cookie behaviour, and HTTP-on-LAN posture. |
| [Threat model](threat-model.md) | Deployment posture and the documented boundaries — including the plaintext Data Protection keyring on `/data`. |
| [Backing up /data](backup.md) | Manual `/data` backups and the automatic pre-migration `.bak` set with its restore procedure. |
| [Upgrading](upgrading.md) | The `docker compose pull` upgrade path, automatic schema migration, and the rolling `.bak` safety net. |
| [Dropbear & SubnetScanRanges](dropbear-subnet-note.md) | Why stock Dropbear breaks subnet auto-derivation and how to set an explicit `SubnetScanRanges`. |
