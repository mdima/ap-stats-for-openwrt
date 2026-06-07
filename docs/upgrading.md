# Upgrading

Upgrading to a newer image is the standard Compose pull-and-recreate:

```sh
docker compose pull
docker compose up -d
```

That is the whole upgrade. Everything else is handled for you.

## What happens on the first boot of the new image

1. **The schema auto-migrates.** On startup the app applies any pending EF Core
   migrations against the SQLite database on `/data` — there is no manual
   migration step and no `EnsureCreated` shortcut.
2. **A pre-migration `.bak` is written first.** Before applying migrations the
   app writes a single rolling pre-migration copy of the database (the
   `AP-Stats-for-OpenWrt.db.bak` set, including the `-wal.bak` / `-shm.bak`
   sidecars when present) to `/data`. Each upgrade overwrites the previous `.bak`
   set — it protects the upgrade you are running, not history. See
   [Backing up /data](backup.md) for the full backup/restore story.
3. **If a migration fails**, startup aborts and the failure log names the `.bak`
   files plus the one-line restore action: stop the container, rename the `.bak`
   set back, and run the previous image. (See
   [Restoring from a `.bak` set](backup.md#restoring-from-a-bak-set).)

> **Want a belt-and-braces backup too?** The automatic `.bak` set is a single
> rolling copy, not an archive. For an upgrade you are nervous about, take your
> own dated backup of `/data` first — see [Backing up /data](backup.md).

## Knowing when a new version is out

The app checks once a day — and only while a browser session is open — whether a
newer version has been published on the GitHub Releases API, and compares it to
its own version. When one is available, a calm navbar badge links to the release
and a one-time toast announces it; a Settings card shows current /
latest-known / last-checked plus a **Check now** button.

The update check is **notify-only**: it never pulls or installs anything. Pulling
the new image stays your call (`docker compose pull && docker compose up -d`). It
is one unauthenticated request per day; switch it off on the Settings page for
zero outbound traffic (air-gapped deployments).

See also: [Backing up /data](backup.md) · [First-boot walkthrough](first-boot.md).
