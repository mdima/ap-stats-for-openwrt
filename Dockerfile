# syntax=docker/dockerfile:1.7
ARG DOTNET_VERSION=10.0

# ── BASE STAGE ────────────────────────────────────────────────────────────
# Runtime image used by both the production `final` stage and Visual Studio's
# fast-mode F5 → "Container (Dockerfile)" debugging path. VS Container Tools
# require a stage literally named `base` that mirrors the runtime layout of
# the final stage; without it, VS targets an arbitrary stage (e.g. `build`)
# whose image has no EXPOSE, no ENTRYPOINT, and no `app` user — the container
# starts but exposes no port and runs no application.
#
# Putting curl + /data + /data/keys + /logs setup here also benefits production:
# the layer is cached independently of the build stage's source/publish output.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS base

# Install curl for the health-check directive CMD (D-04). ~7-10 MB cost; chiseled v2
# will revisit (HARD-01 in STACK.md) and likely move to a `dotnet OpenWrtApStats.dll
# --healthcheck` self-probe. Single RUN combines update + install + apt-cache cleanup
# so the cache files never appear in any image layer (T-07-10 mitigation).
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

# Pre-create volume mount-points and chown them to the app user so a fresh
# named volume (Docker creates it root-owned by default) is writable on first boot.
RUN mkdir -p /data /data/keys /logs \
 && chown -R app:app /data /logs \
 && chmod 700 /data/keys

USER app
WORKDIR /app
EXPOSE 8080

# ── BUILD STAGE ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# csproj-only restore for cache-friendly layer ordering. Only the production csproj is
# copied separately — `dotnet restore OpenWrtApStats/OpenWrtApStats.csproj` does NOT walk
# the .sln, so peer csproj files (OpenWrtApStats.Tests, tools/FixtureCapture) are not
# needed at this stage and cannot be copied anyway because .dockerignore excludes
# OpenWrtApStats.Tests/ wholesale (test source must never enter image layers).
COPY OpenWrtApStats.sln Directory.Build.props global.json ./
COPY OpenWrtApStats/OpenWrtApStats.csproj OpenWrtApStats/

RUN dotnet restore OpenWrtApStats/OpenWrtApStats.csproj

COPY . .

RUN dotnet publish OpenWrtApStats/OpenWrtApStats.csproj \
    -c ${BUILD_CONFIGURATION} \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ── FINAL STAGE ───────────────────────────────────────────────────────────
FROM base AS final
COPY --from=build --chown=app:app /app/publish .

# Health-check directive — D-03 verbatim. Drives Docker's reported liveness via the
# Phase 6 /health endpoint (D-07/D-08). start-period=30s covers EF migrations +
# DataProtection keyring init on first boot before /health answers. timeout=5s aligns
# with CLAUDE.md SSH-timeout convention. retries=3 absorbs one transient SQLite
# write-contention without flapping unhealthy (T-07-08 mitigation).
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "OpenWrtApStats.dll"]
