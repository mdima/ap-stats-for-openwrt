#!/bin/sh
# M-FLD-01 / Phase 22 Plan 22-03 — deterministic round-trip sentinel for the
# DropbearCompat SSH-negotiation arm. The DropbearCompatTests round-trip fact
# runs `/usr/local/bin/sentinel.sh` over the production SSH.NET path and asserts
# the echoed token. A scripted (not interactive) command keeps the assertion
# byte-stable across BusyBox/Dropbear versions: BusyBox `echo` here emits exactly
# "apstats-ok\n", independent of the login shell's prompt/locale.
echo "apstats-ok"
