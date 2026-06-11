# M-FLD-01 / Phase 22 Plan 22-03 (D-07/D-08) — CURRENT Dropbear server image.
#
# Digest-pinned current-stable Alpine base (D-08): 3.21 ships dropbear-ssh
# 2024.86-r0, which advertises the modern KEX/host-key/cipher/MAC set (curve25519
# / ssh-ed25519 / chacha20-poly1305 / hmac-sha2 family). Captured at execute time
# (docker pull alpine:3.21 -> sha256:48b0309c...); do NOT float the tag.
#
# Test-only image — scoped under test/dropbear/** (T-22-06). `.dockerignore`
# excludes the whole `test/` subtree (explicit `test/` entry — WR-01 review fix)
# so this fixture never enters any prod image layer.
FROM alpine:3.21@sha256:48b0309ca019d89d40f670aa1bc06e426dc0931948452e8491e3d65087abc07d

# Single combined RUN (root Dockerfile single-RUN-no-cache-leak hygiene, Dockerfile:21-23):
#   install dropbear + dropbear-ssh; create the throwaway `apstats` test user;
#   set the password; generate the RSA host key Dropbear serves.
# WR-04 review fix: pin the apk packages to the exact =version-release the
# negotiation asserts expect (DropbearCompatTests hard-asserts 2024.86). Enforces
# the determinism contract at the layer that produces the binary, not just the base
# image digest (Alpine can serve a patched point-release for the same digest).
RUN apk add --no-cache 'dropbear=2024.86-r0' 'dropbear-ssh=2024.86-r0' \
 && adduser -D -s /bin/sh apstats \
 && echo 'apstats:apstats' | chpasswd \
 && mkdir -p /etc/dropbear \
 && dropbearkey -t rsa -f /etc/dropbear/dropbear_rsa_host_key

# Deterministic round-trip sentinel baked in (authored inline — see legacy Dockerfile
# rationale: self-contained build context). Canonical source: test/dropbear/shell/sentinel.sh.
RUN printf '#!/bin/sh\necho "apstats-ok"\n' > /usr/local/bin/sentinel.sh \
 && chmod 0755 /usr/local/bin/sentinel.sh

EXPOSE 22
# Foreground (-F), log to stderr (-E), serve the generated RSA host key (-r).
ENTRYPOINT ["dropbear","-F","-E","-r","/etc/dropbear/dropbear_rsa_host_key"]
