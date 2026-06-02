#!/usr/bin/env python3
"""
QUAL-02 LIVE-UAT seed (Phase 19 — history UI surfaces).

Seeds a *fallback* dataset into the running container's SQLite DB so an operator
can walk every history surface (KPI stacked-bar chart, inline RSSI sparkline,
detail multi-series RSSI chart) when NO real AP is reachable on the LAN.

PREFER REAL APs: if you have a live OpenWRT AP, declare it via the wizard /
Settings instead — history then "fills from the right" naturally over the first
12 h and you also get to walk the wizard opt-in step (Surface 4) end-to-end.
This seed exists only so the *chart-render* surfaces have data when no live AP
is available.

What it seeds (all timestamps relative to now, full 12 h window so the charts
render across the whole axis with realistic gaps):
  - 2 declared APs (WiFiNodes) + 3 WLANs (WiFiNetworks)
  - 4 stations (WLANStations) — including one that ROAMS between APs and one
    that goes offline mid-window (to exercise the sparkline gap + continuous-
    across-roam behaviors, L-05)
  - KpiHistorySnapshots: one snapshot per 5-min bucket across 12 h, with a few
    missing buckets near the start (so the KPI chart shows the "fills from the
    right" / early-gap shape, D-03)
  - RssiHistorySamples: per-station RSSI walk, with a deliberate offline gap and
    a roam (AP switch) for the relevant stations

Usage (run from the repo root, container already `docker compose up -d`):
    python support/uat/seed-history-uat.py

It copies the DB out of the container, seeds it with host Python's bundled
sqlite3, copies it back, and restarts the container so the AppSettings cache
re-reads (settings cache → restart required; see MEMORY Dockerized DB UAT
topology). HistoryEnabled is left at its default-on (no row = warm-time ?? true).
"""
import datetime as dt
import os
import sqlite3
import subprocess
import sys
import tempfile

CONTAINER = "apstatsforopenwrt"
DB_PATH_IN_CONTAINER = "/data/AP-Stats-for-OpenWrt.db"
BUCKET_MIN = 5
WINDOW_HOURS = 12

# Status enum (matches the .NET NodeStatus / station status: 0=Offline, 1=Online, 2=Stale — Online=1 is what we seed live)
ONLINE = 1
OFFLINE = 0


def run(cmd):
    print("  $", " ".join(cmd))
    subprocess.run(cmd, check=True)


def iso(t: dt.datetime) -> str:
    # EF Core SQLite stores DateTime as ISO-8601 'yyyy-MM-dd HH:mm:ss.fffffff'
    return t.strftime("%Y-%m-%d %H:%M:%S.0000000")


def seed(db: str):
    c = sqlite3.connect(db)
    cur = c.cursor()

    # Clean slate for the UAT entities (leave AppSettings + migration history alone).
    for t in ["RssiHistorySamples", "KpiHistorySnapshots", "WLANStations", "WiFiNetworks", "WiFiNodes"]:
        cur.execute(f"DELETE FROM {t}")

    now = dt.datetime.utcnow().replace(second=0, microsecond=0)
    # Align 'now' to a 5-min boundary.
    now -= dt.timedelta(minutes=now.minute % BUCKET_MIN)
    start = now - dt.timedelta(hours=WINDOW_HOURS)

    # --- Nodes (2 APs) ---
    cur.executemany(
        "INSERT INTO WiFiNodes (Id, Hostname, Username, Password, Status, LastPollDate, SubnetScanEnabled) "
        "VALUES (?,?,?,?,?,?,0)",
        [
            (1, "ap-living-room", "root", "seeded", ONLINE, iso(now)),
            (2, "ap-office", "root", "seeded", ONLINE, iso(now)),
        ],
    )
    # --- Networks (3 WLANs) ---  (TxPower is NOT NULL)
    cur.executemany(
        "INSERT INTO WiFiNetworks (Id, WiFiNodeId, SSID, Channel, Width, MacAddress, TxPower, Enabled, IfaceName) "
        "VALUES (?,?,?,?,?,?,?,1,?)",
        [
            (1, 1, "HomeNet", 6, 20, "00:11:22:33:44:01", 20, "phy0-ap0"),
            (2, 1, "HomeNet-5G", 36, 80, "00:11:22:33:44:02", 23, "phy1-ap0"),
            (3, 2, "HomeNet", 11, 20, "00:11:22:33:44:03", 20, "phy0-ap0"),
        ],
    )

    # --- Stations (4) ---
    # MACs:
    s_roam = "AA:BB:CC:00:00:01"   # roams AP1 -> AP2 mid-window (continuous sparkline across roam)
    s_drop = "AA:BB:CC:00:00:02"   # goes offline mid-window (sparkline GAP)
    s_solid = "AA:BB:CC:00:00:03"  # steady on AP1/5G
    s_fresh = "AA:BB:CC:00:00:04"  # NO history rows -> sparkline shows the muted "—"
    # WLANStations schema: RSS (nullable), and TxBitrate/RxBitrate/LastPollDate/IsRandomMac/FirstSeen
    # are all NOT NULL. (Column is RSS, not Rssi; there is no LastSeen column.)
    cur.executemany(
        "INSERT INTO WLANStations "
        "(MacAddress, WiFiNetworkId, RSS, Status, TxBitrate, RxBitrate, LastPollDate, IsRandomMac, FirstSeen) "
        "VALUES (?,?,?,?,?,?,?,?,?)",
        _station_rows(now, start),
    )

    # --- KPI snapshots: one per 5-min bucket, a few early gaps ---
    kpi_rows = []
    t = start
    idx = 0
    while t <= now:
        # leave the first ~6 buckets empty so the chart "fills from the right"
        if idx >= 6:
            online = 3 if t < now - dt.timedelta(hours=4) else 2  # one drops offline later
            stale = 1 if (idx % 7 == 0) else 0
            offline = 4 - online  # 4 known stations total
            kpi_rows.append((iso(t), online, stale, max(offline, 0)))
        t += dt.timedelta(minutes=BUCKET_MIN)
        idx += 1
    cur.executemany(
        "INSERT INTO KpiHistorySnapshots (TimestampUtc, OnlineCount, StaleCount, OfflineCount) VALUES (?,?,?,?)",
        kpi_rows,
    )

    # --- RSSI samples ---
    rssi_rows = []
    t = start
    while t <= now:
        age_h = (now - t).total_seconds() / 3600.0
        # roamer: AP1(net1) for first half, AP2(net3) for second half — continuous line, AP attribution flips
        if age_h > 6:
            rssi_rows.append((iso(t), s_roam, 1, 1, -55 - int(5 * _wobble(t))))
        else:
            rssi_rows.append((iso(t), s_roam, 2, 3, -60 - int(5 * _wobble(t))))
        # dropper: present only in first 8 h, then a GAP (offline) for the last 4 h
        if age_h > 4:
            rssi_rows.append((iso(t), s_drop, 1, 1, -67 - int(6 * _wobble(t))))
        # solid: steady on AP1 / 5 GHz
        rssi_rows.append((iso(t), s_solid, 1, 2, -48 - int(4 * _wobble(t))))
        # s_fresh: intentionally NO rows
        t += dt.timedelta(minutes=BUCKET_MIN)
    cur.executemany(
        "INSERT INTO RssiHistorySamples (TimestampUtc, StationMac, NodeId, NetworkId, Rssi) VALUES (?,?,?,?,?)",
        rssi_rows,
    )

    c.commit()
    print(f"  seeded: {len(kpi_rows)} KPI snapshots, {len(rssi_rows)} RSSI samples, 2 APs, 3 WLANs, 4 stations")
    c.close()


def _wobble(t: dt.datetime) -> float:
    # cheap deterministic -1..1 wiggle so the line isn't flat
    return ((t.minute // BUCKET_MIN) % 5) / 2.0 - 1.0


def _has_col(cur, table, col) -> bool:
    return any(r[1] == col for r in cur.execute(f"PRAGMA table_info({table})").fetchall())


def _station_rows(now: dt.datetime, start: dt.datetime):
    fs = iso(start)   # FirstSeen — 12 h ago (NOT NULL)
    lp = iso(now)     # LastPollDate — this cycle (NOT NULL)
    # (mac, WiFiNetworkId, RSS, Status, TxBitrate, RxBitrate, LastPollDate, IsRandomMac, FirstSeen)
    return [
        ("AA:BB:CC:00:00:01", 3, -55, ONLINE, 144.4, 144.4, lp, 0, fs),  # roamer (now on AP2/net3)
        ("AA:BB:CC:00:00:02", 1, None, OFFLINE, 6.0, 6.0, lp, 0, fs),    # dropper (now offline; RSS null)
        ("AA:BB:CC:00:00:03", 2, -48, ONLINE, 433.3, 433.3, lp, 0, fs),  # solid
        ("AA:BB:CC:00:00:04", 1, -70, ONLINE, 72.2, 72.2, lp, 0, fs),    # fresh (no history yet)
    ]


def main():
    here = os.path.abspath(os.path.dirname(__file__))
    repo = os.path.abspath(os.path.join(here, "..", ".."))
    print(f"QUAL-02 UAT seed — container={CONTAINER}")
    with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
        local = os.path.join(tmp, "uat.db")
        print("[1/4] copy DB out of container")
        run(["docker", "cp", f"{CONTAINER}:{DB_PATH_IN_CONTAINER}", local])
        # pull WAL/SHM too so we read the latest committed state
        for ext in ("-wal", "-shm"):
            subprocess.run(["docker", "cp", f"{CONTAINER}:{DB_PATH_IN_CONTAINER}{ext}", local + ext], check=False)
        print("[2/4] seed history dataset")
        seed(local)
        print("[3/4] copy DB back into container")
        # checkpoint WAL into the main file before copying back, then drop sidecars
        sub = sqlite3.connect(local)
        sub.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        sub.close()
        run(["docker", "cp", local, f"{CONTAINER}:{DB_PATH_IN_CONTAINER}"])
        for ext in ("-wal", "-shm"):
            subprocess.run(
                ["docker", "exec", CONTAINER, "rm", "-f", f"{DB_PATH_IN_CONTAINER}{ext}"], check=False
            )
        print("[4/4] restart container (settings cache + WAL re-open)")
        run(["docker", "restart", CONTAINER])
    print("DONE — open http://localhost:8080 and walk the surfaces.")


if __name__ == "__main__":
    sys.exit(main())
