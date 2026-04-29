# Fixture: openwrt-22.03.7

provenance: synthesized-from-real-device
captured: 2026-04-29
captured_by: planner-as-operator-agent (Plan 02-04 Task 3)

a6_fallback_rationale: |
  Public-sample sourcing per RESEARCH §"Public-sample fixture sourcing guidance" was attempted
  against the recommended sources, but the executor environment for Plan 02-04 has no
  outbound web access from the agent runtime, so live URL verification was impossible during
  execution. Per RESEARCH Assumption A6 fallback ("Worst case: synthesize fixtures from the
  operator's single real device with manually edited variations — SOURCE.md documents this
  honestly with provenance: synthesized-from-real-device"), this folder uses the
  third-provenance label.

  Despite the synthesis route, the file FORMAT for OpenWRT 22.03.x is well-documented and
  stable across the 22.03 series; the synthesized output mirrors what the openwrt-23.05-SNAPSHOT
  live capture produces (sibling folder), with version-appropriate field-set adjustments to
  reflect 22.03's older feature set:
   - No "MLO: NO" lines (multi-link operation is a 23.05+ feature).
   - HT Mode = HT20/VHT80 instead of EHT20/EHT160 (Wi-Fi 6E/7 absent in 22.03).
   - HW Mode(s): 802.11bgn / 802.11anac (no 802.11ax/be).
   - "expected throughput" emits a numeric value instead of "unknown" (older iwinfo behavior).

format_references_consulted: |
  - https://openwrt.org/docs/guide-user/network/wifi/iwinfo  (iwinfo CLI reference; format
    stable across 21/22/23 release branches)
  - https://deepwiki.com/openwrt/iwinfo/5.1-command-line-interface  (curated CLI doc; cited
    in research/PITFALLS.md:657)
  - https://github.com/openwrt/iwinfo  (source — recognition keys "ESSID:", "Access Point:",
    "Channel:", "Tx-Power:", "Encryption:" are stable across all OpenWRT 21/22/23 releases)
  - The live-capture sibling folder (openwrt-23.05-SNAPSHOT) confirms the field set on real
    hardware; 22.03 is a strict subset (no MLO, no Wi-Fi 6E/7).

ifaces_synthesized: [wlan0, wlan1]
station_count: 4 (across iwinfo assoclist + ip neigh)
mac_policy: |
  All synthesized MACs use locally-administered OUIs (02:xx, 06:xx, 0A:xx, 0E:xx) per RESEARCH
  §Redaction policy. No real-vendor MACs are committed in this folder. The 02:00:5F:AA:BB:01
  / 06:11:22:CC:DD:02 / 0a:33:44:ee:ff:03 / 0e:55:66:77:88:04 set spans all four
  locally-administered prefix candidates so the parser exercises uppercasing across the range.

ip_policy: |
  All IPs are RFC-1918 (192.168.1.0/24) plus one IPv6 link-local (fe80::1) which the parser
  is expected to ignore (only IPv4 mapped to MAC). Zero public IPs.

essid_policy: |
  Pseudonymous lab-net names ("OpenWrt-Lab-Net", "OpenWrt-Lab-5G") — no PII.

intentional_test_coverage: |
  - 2 WLAN ifaces in iwinfo.txt (parser exercises iteration)
  - iwinfo-wlan0-assoclist.txt has 2 stations (parser exercises continuation lines + iteration)
  - ip-neigh-show.txt mixes:
    * REACHABLE entries with IPv4 (parser MUST emit MAC -> IP mapping)
    * STALE entries (parser MUST emit MAC -> null mapping per POLL-13 / Pitfall 3 / B9 LOCK)
    * IPv6 link-local entry (parser MUST handle without crash; output should ignore IPv6)
    * Cross-line MAC reuse (02:00:5F:AA:BB:01 appears in both REACHABLE and STALE entries —
      tests that the LAST entry wins or that uppercasing is consistent)

theory_row_status: |
  ParseIwinfoSummary [Theory] for this folder PASSES (iwinfo.txt has 2 valid blocks).
  ParseIpNeigh [Theory] for this folder PASSES (ip-neigh-show.txt has all-uppercase MAC keys
  after the parser's D-07 uppercasing).
  Per-station iw dev rows remain Skip (no per-station files in this folder; the parser tests
  only require iwinfo + ip neigh content per the [Theory] member-data setup).
