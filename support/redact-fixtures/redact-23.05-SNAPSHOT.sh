#!/usr/bin/env bash
# Plan 02-04 Task 2 redaction script — sed-based (no Python per <no_python_guard>).
# Applies RESEARCH.md §Redaction policy to the live-capture fixture set.
#
# Policy:
#  - Real-vendor station MACs -> 02:00:ZZ:AA:BB:CC where ZZ:AA:BB:CC is the source's lower 32 bits.
#    (The plan example is XX:YY:ZZ:AA:BB:CC -> 02:00:ZZ:AA:BB:CC; matches verbatim.)
#  - AP own BSSIDs (94:83:C4:C8:ED:8E/8F, B6:E1:64:37:F6:C6, 72:02:DD:97:40:4C) MAY remain — D-11 permits.
#  - Already-locally-administered MACs (5E:93:32:79:28:8E, 8A:75:36:75:57:B3) are kept verbatim.
#  - Zero MAC (00:00:00:00:00:00) kept verbatim (it's a parser-relevant placeholder).
#  - Public IPv4: none in this capture (all are RFC-1918 192.168.22.x — kept verbatim).
#  - Hostnames: none present in captured outputs (operator's hostname only in .live-credentials.local.env,
#    which is gitignored and never captured).
#
# Filename rule for renames: the FixtureCapture tool's W4-LOCK extended in this plan replaces ':' with '-'
# (Windows-safe), so per-station filenames carry MAC in dash form (e.g. iw-dev-wlan0-station-get-AA-BB-CC-DD-EE-FF.txt).
# Renames must apply the same dash form.
#
# Usage:
#   bash support/redact-fixtures/redact-23.05-SNAPSHOT.sh

set -euo pipefail

DIR="ApStatsForOpenWrt.Tests/Fixtures/ssh-output/openwrt-23.05-SNAPSHOT"

if [[ ! -d "$DIR" ]]; then
  echo "FATAL: $DIR not found. Run from repo root." >&2
  exit 1
fi

# Mapping table: SOURCE_UPPER  REPLACEMENT
# Format: one mapping per line, two whitespace-separated columns.
mapping=(
  "00:60:2F:97:F8:53  02:00:2F:97:F8:53"
  "00:E2:69:58:BB:A2  02:00:69:58:BB:A2"
  "14:A7:8B:28:04:5C  02:00:8B:28:04:5C"
  "24:62:AB:58:44:6B  02:00:AB:58:44:6B"
  "30:F6:EF:B2:98:0F  02:00:EF:B2:98:0F"
  "44:5D:5E:02:7B:4E  02:00:5E:02:7B:4E"
  "48:D8:90:9B:C1:61  02:00:90:9B:C1:61"
  "58:8C:81:AE:30:00  02:00:81:AE:30:00"
  "5C:01:3B:70:A1:B8  02:00:3B:70:A1:B8"
  "60:18:95:71:47:90  02:00:95:71:47:90"
  "64:E8:33:87:8F:04  02:00:33:87:8F:04"
  "78:33:C6:BE:44:AA  02:00:C6:BE:44:AA"
  "7C:87:CE:A5:71:40  02:00:CE:A5:71:40"
  "94:83:C4:C8:E1:23  02:00:C4:C8:E1:23"
  "94:E3:6D:7F:7D:76  02:00:6D:7F:7D:76"
  "98:F1:12:23:52:61  02:00:12:23:52:61"
  "A4:CF:12:D9:B9:B2  02:00:12:D9:B9:B2"
  "C8:2B:96:E5:24:0B  02:00:96:E5:24:0B"
  "C8:2B:96:E5:25:BB  02:00:96:E5:25:BB"
  "CC:50:E3:67:FD:A2  02:00:E3:67:FD:A2"
  "CC:50:E3:68:8F:1B  02:00:E3:68:8F:1B"
  "CC:50:E3:68:CA:61  02:00:E3:68:CA:61"
  "D0:AD:08:1A:FD:60  02:00:08:1A:FD:60"
  "D8:F1:5B:81:33:57  02:00:5B:81:33:57"
  "E8:DB:84:9D:A6:5C  02:00:84:9D:A6:5C"
  "EC:FA:BC:50:CD:74  02:00:BC:50:CD:74"
  "EC:FA:BC:50:DF:65  02:00:BC:50:DF:65"
  "EC:FA:BC:50:E3:B4  02:00:BC:50:E3:B4"
  "EC:FA:BC:50:E6:64  02:00:BC:50:E6:64"
  "F4:CF:A2:F5:FE:5D  02:00:A2:F5:FE:5D"
)

echo "Applying MAC redaction sed pass over $DIR/*.txt ..."

# Build a sed script that handles uppercase, lowercase, and dash-separated MAC forms.
SED_SCRIPT=$(mktemp)
trap 'rm -f "$SED_SCRIPT"' EXIT

for entry in "${mapping[@]}"; do
  # Split on whitespace.
  src=$(echo "$entry" | awk '{print $1}')
  dst=$(echo "$entry" | awk '{print $2}')

  # Lowercase form (used in iw dev / ip neigh output).
  src_lower=$(echo "$src" | tr 'A-F' 'a-f')
  dst_lower=$(echo "$dst" | tr 'A-F' 'a-f')

  # Dash form (used in per-station filenames and would appear if any tool
  # ever wrote dash MACs into command output — defensive).
  src_dash=$(echo "$src" | tr ':' '-')
  dst_dash=$(echo "$dst" | tr ':' '-')

  # Emit four substitution commands per source MAC. Using # as delimiter since
  # MAC values contain : (and would also conflict with / for sed's default).
  {
    echo "s#${src}#${dst}#g"
    echo "s#${src_lower}#${dst_lower}#g"
    echo "s#${src_dash}#${dst_dash}#g"
  } >> "$SED_SCRIPT"
done

# Apply sed to all .txt files in the fixture dir.
for f in "$DIR"/*.txt; do
  [[ -f "$f" ]] || continue
  sed -i.bak -f "$SED_SCRIPT" "$f"
  rm -f "$f.bak"
done

echo "MAC redaction in file content: complete."

# ---------- rename per-station files where the MAC in the filename was redacted ----------
echo "Renaming per-station files where redacted MAC differs ..."
for entry in "${mapping[@]}"; do
  src=$(echo "$entry" | awk '{print $1}')
  dst=$(echo "$entry" | awk '{print $2}')
  src_dash=$(echo "$src" | tr ':' '-')
  dst_dash=$(echo "$dst" | tr ':' '-')

  # Look for files containing src_dash; rename to dst_dash form. Both upper- and lowercase variants.
  for case_variant in "$src_dash" "$(echo "$src_dash" | tr 'A-F' 'a-f')"; do
    # Find files whose name contains the dash MAC.
    while IFS= read -r -d '' file; do
      base=$(basename "$file")
      newbase=${base//$case_variant/$dst_dash}
      if [[ "$base" != "$newbase" ]]; then
        mv -v "$file" "$DIR/$newbase"
      fi
    done < <(find "$DIR" -maxdepth 1 -name "*${case_variant}*" -print0 2>/dev/null)
  done
done

echo "Per-station file renames: complete."

echo ""
echo "Redaction summary:"
echo "  - ${#mapping[@]} real-vendor station MACs rewritten to locally-administered (02:00:ZZ:AA:BB:CC)"
echo "  - 4 AP BSSIDs (94:83:C4:C8:ED:8E/8F, B6:E1:64:37:F6:C6, 72:02:DD:97:40:4C) kept verbatim per D-11"
echo "  - 2 already-local-admin MACs (5E:93:32:79:28:8E, 8A:75:36:75:57:B3) kept verbatim"
echo "  - 1 zero MAC (00:00:00:00:00:00) kept verbatim (parser placeholder)"
echo "  - 0 hostnames or public IPs in capture (RFC-1918 only; no PII)"
echo ""
echo "VERIFY: grep -rE \"[A-Z]{2}:[A-Z]{2}:[A-Z]{2}\" $DIR | grep -v -E \"02:00:|94:83:C4:C8:ED:8|B6:E1:64:37:F6:C6|72:02:DD:97:40:4C|00:00:00:00:00:00|5E:93:32:79:28:8E|8A:75:36:75:57:B3\" | wc -l   # should be 0"
