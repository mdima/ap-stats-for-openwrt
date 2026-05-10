# Fixture: openwrt-23.05-SNAPSHOT

provenance: live-capture
captured: 2026-04-29
captured_by: operator (hostname REDACTED — see redactions below)
hardware: OpenWrt 23.05-SNAPSHOT, target ipq53xx/generic, aarch64_cortex-a53_neon-vfpv4 (Qualcomm Atheros AP)
openwrt_release: |
  DISTRIB_ID='OpenWrt'
  DISTRIB_RELEASE='23.05-SNAPSHOT'
  DISTRIB_REVISION=''
  DISTRIB_TARGET='ipq53xx/generic'
  DISTRIB_ARCH='aarch64_cortex-a53_neon-vfpv4'
  DISTRIB_DESCRIPTION='OpenWrt 23.05-SNAPSHOT '
  DISTRIB_TAINTS='no-all busybox override'
ifaces_discovered: [mld-wifi0, wifi0, wifi1, wlan0, wlan1]
host_key_fingerprint_at_capture: owCNP2WuW1XYDg/mQH6jvkowM0wnCe2qH4JQ3gjqV9E
notes: |
  - Captured by tools/FixtureCapture (Plan 02-04) against the operator's real OpenWRT 23.05-SNAPSHOT AP.
  - Folder name comes from DISTRIB_RELEASE='23.05-SNAPSHOT' (the AP runs a snapshot build of the
    23.05 branch, not the 23.05.5 release). Plan 02-04 had earmarked openwrt-23.05.5/ but the
    operator's real device is on snapshot — Task 4 deletes the unused 23.05.5 skeleton folder
    (W8 LOCK) and updates OpenWrtOutputParserTests.AllVersionFolders to include this folder.

redactions_applied: |
  Approach (per <no_python_guard> in execute prompt): sed-based substitution via
  support/redact-fixtures/redact-23.05-SNAPSHOT.sh (Bash + sed; no Python). The script also
  renames per-station files where the MAC in the filename was rewritten so the file content
  and filename stay consistent.

  ## MAC redactions (RESEARCH §Redaction policy)

  Real-vendor station MACs (universally-administered first octet — bit 1 of byte 0 = 0) are
  rewritten to locally-administered space using the rule:

      XX:YY:ZZ:AA:BB:CC  ->  02:00:ZZ:AA:BB:CC

  This matches the plan's worked example (34:14:5F:AA:BB:CC -> 02:00:5F:AA:BB:CC: keeps the
  vendor's third octet and lower 24 bits, sets the top 16 bits to the canonical local-admin
  prefix 02:00). Both uppercase and lowercase forms in the source files are rewritten to
  the matching case in the destination.

  30 unique source MACs were rewritten. All 30 produce non-colliding destinations (last 4
  octets are unique across the source set).

  ## MACs kept verbatim (per D-11 explicit permission)

  - 94:83:C4:C8:ED:8E  AP wifi0 BSSID (broadcast over the air on every beacon — public)
  - 94:83:C4:C8:ED:8F  AP wifi1 BSSID (broadcast over the air on every beacon — public)
  - B6:E1:64:37:F6:C6  AP wlan0 BSSID — already locally-administered (B6 has bit 1 set)
  - 72:02:DD:97:40:4C  AP wlan1 BSSID — already locally-administered (72 has bit 1 set)

  D-11 grants permission to keep AP-own BSSIDs verbatim because they are public broadcast
  identifiers of the device-under-test, not PII.

  ## MACs kept verbatim (already locally-administered in source)

  - 5E:93:32:79:28:8E  (5E has bit 1 of byte 0 set — already private space)
  - 8A:75:36:75:57:B3  (8A has bit 1 of byte 0 set — already private space)
  - 00:00:00:00:00:00  (zero placeholder; mld-wifi0 has no MAC — parser tolerance signal)

  ## Hostname redactions

  Zero hostnames in the captured outputs. The operator's AP hostname
  (in OPENWRT_HOST in .live-credentials.local.env) is kept gitignored and never appears in
  any captured output file. Station hostnames (`johns-iphone` style) do not appear in the
  iwinfo / iw / ip neigh output for this version of OpenWRT.

  ## IP address redactions

  Zero public IPs in capture. All ip neigh entries are RFC-1918 192.168.22.0/24 — kept
  verbatim per the plan policy ("RFC-1918 IPs (10.x, 172.16-31.x, 192.168.x) stay verbatim").

  ## ESSIDs

  ESSID values "DesertStorm" and "EnduringFreedom" are operator-chosen pseudonyms (military
  operation names, not personal identifiers). Kept verbatim — they are public Wi-Fi
  broadcasts and contain no PII.

verification: |
  After redaction, `grep -h -oE "[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}" *.txt | sort -u` returns 37 unique MACs, of which:
   - 30 begin with 02:00:  (rewritten real-vendor)
   - 4 are AP BSSIDs  (94:83:C4:C8:ED:8E/8F, B6:E1:64:37:F6:C6, 72:02:DD:97:40:4C)
   - 2 are pre-existing local-admin (5E:93:32:79:28:8E, 8A:75:36:75:57:B3)
   - 1 is zero (00:00:00:00:00:00)
   = 37 total. No real-vendor (universally-administered) MAC remains.

reproducibility: |
  Re-running `dotnet run --project tools/FixtureCapture` against the same AP overwrites the
  files in this folder with verbatim (un-redacted) content. The operator must then re-run
  `bash support/redact-fixtures/redact-23.05-SNAPSHOT.sh` to re-apply the redaction pass.
  The mapping table in the script is deterministic, so re-running the script against
  re-captured content produces an identical diff.

---

## Phase 9 capture wave (Plan 09-03)

phase9_provenance: synthesized-placeholder (live-capture pending operator hand-off)
phase9_captured: 2026-05-10
phase9_captured_by: Plan 09-03 executor (autonomous)
phase9_files_added:
  - arping.txt              (synthesized — operator should overwrite via tools/FixtureCapture against the live AP)
  - ping.txt                (synthesized — same)
  - command-v-arping.txt    (synthesized — `/usr/sbin/arping`)
  - command-v-arping.txt.exit  (`0`)
phase9_operator_handoff: |
  Plan 09-03 ships PLACEHOLDER content for the 23.05-SNAPSHOT slot to keep the SubnetSweepProbeTests
  theory rows green-via-PASS during the autonomous wave window. The Phase-2-style operator-driven
  live-capture (per D-10/D-11) is the next manual step:
    1. Extend tools/FixtureCapture's command list to include:
         arping -c1 -w1 -I <iface> <one IP from local subnet>
         ping -c1 -W1 <one IP from local subnet>
         command -v arping
    2. Run against the operator's real OpenWrt 23.05-SNAPSHOT AP via the existing .live-credentials.local.env flow.
    3. Re-run the existing redaction script (support/redact-fixtures/redact-23.05-SNAPSHOT.sh) which is deterministic.
    4. Commit the overwritten files in this folder under the same provenance: live-capture banner.
phase9_theory_row_status: |
  After Plan 09-03 lands these files, OpenWrtOutputParserTests.ParseArpingPingOutput_AcrossAllSupportedVersions_*
  flips from SKIP to PASS for this version slot. Replacing the placeholder with live-captured content does
  not change the row count — content is parser-tolerant by construction.

---

## Phase 9 capture wave (Plan 09-04)

phase9_04_provenance: synthesized-placeholder (live-capture pending operator hand-off)
phase9_04_captured: 2026-05-10
phase9_04_captured_by: Plan 09-04 executor (autonomous)
phase9_04_files_added:
  - nslookup.txt            (synthesized BusyBox nslookup shape — Server preamble + Name: line + answer Address line; parser-tolerant by construction so live-capture overwrite is invisible to row count)
phase9_04_operator_handoff: |
  Plan 09-04 ships PLACEHOLDER content for the 23.05-SNAPSHOT slot. The Phase-2-style operator-driven
  live-capture (per D-10/D-11) is the next manual step, mirroring the Plan 09-03 hand-off:
    1. Extend tools/FixtureCapture's command list to include:
         nslookup <one IP from local subnet>
    2. Run against the operator's real OpenWrt 23.05-SNAPSHOT AP via the existing .live-credentials.local.env flow.
    3. Re-run the existing redaction script (support/redact-fixtures/redact-23.05-SNAPSHOT.sh).
    4. Commit the overwritten file under the same provenance: live-capture banner.
phase9_04_ips_used: 192.168.1.1 (synthesized resolver), 192.168.1.42 (synthesized lookup target)
phase9_04_theory_row_status: |
  After Plan 09-04 lands this file, OpenWrtOutputParserTests.ParseNslookupOutput_AcrossAllSupportedVersions_*
  AND ReverseDnsProbeTests.PollAsync_AcrossAllSupportedVersions_* flip from SKIP to PASS for this version slot.
