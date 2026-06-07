# Backing up /data

Everything that makes your deployment *yours* lives on the `/data` volume: the
SQLite database (`/data/AP-Stats-for-OpenWrt.db`) holding your APs, the UI
password hash, and the encrypted SSH credentials — plus the Data Protection
keyring under `/data/keys` that is required to decrypt those credentials.

> **Back up `/data` as a unit.** Because the encryption keyring lives on the same
> volume as the data it protects, a backup of `/data` is both portable and
> sensitive — protect the backup exactly as you protect the live volume (see
> [Threat model](threat-model.md)).

## Manual backup — the whole volume

The simplest backup is the whole volume. Stop the container, copy `/data`, and
your entire deployment is portable:

```sh
docker compose down
# copy the named volume's contents somewhere safe, e.g. with a helper container:
docker run --rm \
  -v apstatsforopenwrt-data:/data:ro \
  -v "$PWD":/backup \
  busybox tar czf /backup/apstats-data-backup.tar.gz -C /data .
docker compose up -d
```

Restoring is the reverse: stop the container, extract the archive back into the
volume, start it again.

## The automatic pre-migration `.bak` set

You do not have to remember to back up before an upgrade — the app does it for
you whenever a schema migration is pending. Just before applying migrations at
startup, it writes a **single rolling pre-migration copy** of the live database
to the same `/data` directory, as a `.bak` set:

| `.bak` file | Source |
|---|---|
| `AP-Stats-for-OpenWrt.db.bak` | the live database file |
| `AP-Stats-for-OpenWrt.db-wal.bak` | the `-wal` write-ahead-log sidecar (when present) |
| `AP-Stats-for-OpenWrt.db-shm.bak` | the `-shm` shared-memory sidecar (when present) |

- It is a plain **file copy**, not a database write — no checkpoint connection is
  opened, so committed-but-not-yet-checkpointed data in the `-wal` sidecar is
  captured even after a dirty shutdown. That is why all three files are copied.
- It is a **single rolling copy**: each pre-migration backup **overwrites** the
  previous `.bak` set. It is a safety net for the upgrade you are about to run,
  not a retention archive — keep your own dated backups (above) for history.
- On a **fresh volume** (no `.db` exists yet) the backup is a no-op skip — there
  is nothing to protect even though all migrations are pending.
- If the copy fails, startup **aborts** with a Critical log entry rather than
  migrating without the safety net in place.

## Restoring from a `.bak` set

If a migration fails, the failure is logged at Critical/Error level and the log
names the `.bak` files plus the one-line restore action. To restore:

1. **Stop the container** (`docker compose down`).
2. On the `/data` volume, **rename the `.bak` set back** over the live files:
   `AP-Stats-for-OpenWrt.db.bak` → `AP-Stats-for-OpenWrt.db`, and likewise for
   the `-wal.bak` / `-shm.bak` sidecars.
3. **Run the previous image** (the version you upgraded *from*) so its schema
   matches the restored database.

See also: [Upgrading](upgrading.md) · [Threat model](threat-model.md).
