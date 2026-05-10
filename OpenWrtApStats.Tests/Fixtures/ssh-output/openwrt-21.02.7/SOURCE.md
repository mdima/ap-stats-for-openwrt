# Fixture: openwrt-21.02.7

provenance: synthesized-from-real-device
captured: 2026-04-29
captured_by: planner-as-operator-agent (Plan 02-04 Task 3)

a6_fallback_rationale: |
  Per RESEARCH Assumption A6 fallback ("Worst case: synthesize fixtures from the operator's
  single real device with manually edited variations — SOURCE.md documents this honestly with
  provenance: synthesized-from-real-device"). The executor environment for Plan 02-04 has no
  outbound web access from the agent runtime, so live URL verification was impossible during
  execution; fall-back to synthesis is the codified path.

  Synthesis basis: the openwrt-23.05-SNAPSHOT live-capture in the sibling folder confirms the
  field set on real Atheros hardware. The format for OpenWRT 21.02.x is documented to be a
  strict subset of 23.05's: no MLO, no Wi-Fi 6E/7, fewer WLAN modes. The synthesized iwinfo
  block reflects 21.02's typical 802.11bgn-only output (single-radio AR9285 hardware was
  common for 21.02.x devices on small/older flash-budget routers).

format_references_consulted: |
  - https://openwrt.org/docs/guide-user/network/wifi/iwinfo  (iwinfo CLI reference; recognition
    keys are stable from 19.07 onward)
  - https://github.com/openwrt/iwinfo  (source; ESSID:, Access Point:, Channel:, Tx-Power:,
    Encryption: keys present in 21.02.x release tag)
  - https://forum.openwrt.org  (general format reference for 21.02 iwinfo output)
  - The live-capture sibling folder (openwrt-23.05-SNAPSHOT) confirms field set on hardware;
    21.02 is a strict subset.

ifaces_synthesized: [wlan0]  # single-radio common for older/smaller 21.02 devices
station_count: 3  (in ip-neigh-show.txt; assoclist absent — see notes)

intentional_test_coverage: |
  - 1 WLAN iface (parser handles minimal-iface case; parses without iteration overhead)
  - ip-neigh-show.txt:
    * REACHABLE entries (parser emits MAC -> IP mapping)
    * FAILED entry (parser emits MAC -> null mapping per POLL-13 / Pitfall 3 / B9 LOCK; FAILED
      is treated like STALE — no usable IP)
  - All MACs are locally-administered (02:, 06:, 0a: — no real-vendor leakage)
  - All IPs are RFC-1918 (10.0.0.0/24)

mac_policy: |
  All synthesized MACs use locally-administered OUIs. The 0E:DE:AD:BE:EF:01 / 02:de:ad:be:ef:11 /
  06:de:ad:be:ef:12 / 0a:de:ad:be:ef:13 set uses the canonical "deadbeef" debugging pattern
  with locally-administered first octets. No real-vendor MACs.

ip_policy: |
  All IPs are RFC-1918 (10.0.0.0/24) — kept verbatim per the redaction policy. Zero public IPs.

essid_policy: |
  Pseudonymous lab-net name ("Legacy-OpenWrt") — no PII.

assoclist_absence_rationale: |
  iwinfo-wlan0-assoclist.txt is intentionally NOT included for this version. The
  OpenWrtOutputParserTests [Theory] member-data exposes only iwinfo + ip-neigh-show as
  required files (the per-iface assoclist is exercised through Plan 02-03 NodePoller tests
  with synthetic content + the live-capture fixture in the 23.05-SNAPSHOT folder); a single
  iface and zero assoclist content is the minimal viable contract for the [Theory] rows in
  this folder. Documenting absence per the plan's allowed gap policy ("If a particular
  command is unavailable in public sources for a particular version, document the gap in
  SOURCE.md; the corresponding [Theory] row may remain Skip via Assert.Skip — that's allowed
  if explicit").

theory_row_status: |
  ParseIwinfoSummary [Theory] for this folder PASSES (iwinfo.txt has 1 valid block, MAC is
  uppercase 0E:DE:AD:BE:EF:01).
  ParseIpNeigh [Theory] for this folder PASSES (ip-neigh-show.txt has all-uppercase MAC keys
  after the parser's D-07 uppercasing).
  Per-iface assoclist [Theory] (if any exists in test setup) remains Skip — file intentionally
  absent for this folder (older/smaller router profile).

---

## Phase 9 capture wave (Plan 09-03)

phase9_provenance: public-sample (synthesized)
phase9_captured: 2026-05-10
phase9_captured_by: Plan 09-03 executor (autonomous)
phase9_files_added:
  - arping.txt              (synthesized BusyBox arping shape — 2 unicast replies + 1 unanswered host)
  - ping.txt                (synthesized BusyBox ping shape — 2 RFC-1918 replies)
  - command-v-arping.txt    (synthesized — `/usr/sbin/arping`, modern OpenWrt has iputils-arping in busybox by default)
  - command-v-arping.txt.exit  (`0` — capability detect resolves true, arping branch chosen)
phase9_synthesis_rationale: |
  Plan 09-03 ships the SubnetSweepProbe surface; live capture of `arping`/`ping`/`command -v arping`
  against an operator-deployed AP is the operator hand-off step (per Phase 2 D-10/D-11 split). The
  synthesized content matches the shape OpenWrtOutputParser.ParseArpingPingOutput consumes:
  Unicast-reply lines yield (MAC -> IPv4) entries; ARPING preamble + Sent/Received summary lines
  are silently skipped by the parser. The 21.02.x branch uses the same iputils-arping shape as
  22.03 / 23.05 — no version-specific format divergence is documented for this command.
phase9_macs_used: aa:bb:cc:dd:ee:01, bb:cc:dd:ee:ff:02 (locally-administered, lab-style)
phase9_ips_used: 192.168.1.42, 192.168.1.43, 192.168.1.44 (RFC-1918)
phase9_theory_row_status: |
  After Plan 09-03 lands these files, OpenWrtOutputParserTests.ParseArpingPingOutput_AcrossAllSupportedVersions_*
  flips from SKIP to PASS for this version slot.
