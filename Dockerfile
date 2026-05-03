# syntax=docker/dockerfile:1.7
ARG DOTNET_VERSION=10.0

# ── BUILD STAGE ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# csproj-only restore for cache-friendly layer ordering.
COPY OpenWrtApStats.sln Directory.Build.props global.json ./
COPY OpenWrtApStats/OpenWrtApStats.csproj OpenWrtApStats/
COPY OpenWrtApStats.Tests/OpenWrtApStats.Tests.csproj OpenWrtApStats.Tests/
COPY tools/FixtureCapture/FixtureCapture.csproj tools/FixtureCapture/

RUN dotnet restore OpenWrtApStats/OpenWrtApStats.csproj

COPY . .

RUN dotnet publish OpenWrtApStats/OpenWrtApStats.csproj \
    -c ${BUILD_CONFIGURATION} \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ── RUNTIME STAGE ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
WORKDIR /app

# NOTE (Phase 7 Plan 07-01): The Docker health-check directive and the
# curl-install layer are added by Plan 07-02 (Wave 2). This baseline
# Dockerfile builds cleanly without curl; do NOT add curl or the health-check
# directive here. See 07-02-PLAN.md.

USER root
RUN mkdir -p /data /data/keys /logs \
 && chown -R app:app /data /logs \
 && chmod 700 /data/keys

COPY --from=build --chown=app:app /app/publish .

EXPOSE 8080

USER app
ENTRYPOINT ["dotnet", "OpenWrtApStats.dll"]
