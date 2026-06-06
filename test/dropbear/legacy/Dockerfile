# M-FLD-01 / Phase 22 Plan 22-03 (D-07/D-08) — LEGACY Dropbear server image.
#
# Digest-pinned Alpine 3.10 base (RESEARCH Pitfall 7 / D-08): 3.10 ships
# dropbear-ssh 2019.78-r1, which advertises the SHA-1-family KEX/host-key/MAC set
# the production SshAlgorithmFallbackPolicy.ExtendForOldDropbear() path must keep
# negotiating. The pin below was captured at execute time
# (docker pull alpine:3.10 -> sha256:451eee8b...); do NOT float the tag.
#
# Test-only image — scoped entirely under test/dropbear/** (T-22-06). The
# deliberately-weak legacy crypto here is NEVER referenced by the root Dockerfile
# or release.yml; `.dockerignore` excludes the whole `test/` subtree (the explicit
# `test/` entry — WR-01 review fix) so these fixtures never enter ANY image layer.
FROM alpine:3.10@sha256:451eee8bedcb2f029756dc3e9d73bab0e7943c1ac55cff3a4861c52a0fdd3e98

# Single combined RUN (root Dockerfile single-RUN-no-cache-leak hygiene, Dockerfile:21-23):
#   apk update so the 3.10 (EOL) index is fetched before install; install dropbear +
#   dropbear-ssh; create the throwaway `apstats` test user (T-22-07 — test-only creds);
#   set the password to `apstats`; generate the RSA host key Dropbear serves.
RUN apk update \
 && apk add --no-cache dropbear dropbear-ssh \
 && adduser -D -s /bin/sh apstats \
 && echo 'apstats:apstats' | chpasswd \
 && mkdir -p /etc/dropbear \
 && dropbearkey -t rsa -f /etc/dropbear/dropbear_rsa_host_key

# Deterministic round-trip sentinel baked in (no scripted-shell analog existed in-repo).
# Authored inline (heredoc) rather than COPY so the build context is self-contained
# under test/dropbear/legacy/ — `docker build test/dropbear/legacy` works standalone
# AND under the fixture's ImageFromDockerfileBuilder. The canonical source of the
# script body is test/dropbear/shell/sentinel.sh (kept in sync; same one-line echo).
RUN printf '#!/bin/sh\necho "apstats-ok"\n' > /usr/local/bin/sentinel.sh \
 && chmod 0755 /usr/local/bin/sentinel.sh

EXPOSE 22
# Foreground (-F), log to stderr (-E), serve the generated RSA host key (-r). Allow
# password auth from the test user; -s disables nothing extra (defaults keep password on).
ENTRYPOINT ["dropbear","-F","-E","-r","/etc/dropbear/dropbear_rsa_host_key"]
