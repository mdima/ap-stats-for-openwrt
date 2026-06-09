<!-- GSD:project-start source:PROJECT.md -->
## Project

**AP Stats for OpenWrt** — A self-hosted Docker dashboard that gives operators of OpenWRT-based access points a live view of who is connected to which WLAN on which AP. The native LuCI UI in AP mode does not surface this information cleanly across multiple APs and WLANs, so this dashboard fills that gap. The application is delivered as a single Docker container that polls each declared AP over SSH and renders the results through a Blazor live UI.

**Core value:** A network operator can see, at a glance and in real time, every WiFi station currently associated with every monitored WLAN across every declared OpenWRT access point.

See `.planning/PROJECT.md` for the living project context (requirements, constraints, key decisions, evolution log).
<!-- GSD:project-end -->

<!-- GSD:stack-start source:research/STACK.md -->
## Technology Stack

- **Runtime / framework:** .NET 10 (LTS) + Blazor Server with **InteractiveServer** render mode
- **Persistence:** SQLite via EF Core 10.0.x — DB at `/data/AP-Stats-for-OpenWrt.db`, auto-migrated at startup with real migration history
- **DB access pattern:** `IDbContextFactory<DatabaseContext>` (NOT scoped `DbContext` — captive dependency in `BackgroundService`)
- **SSH:** SSH.NET 2025.1.x — works with current OpenWRT/Dropbear without manual cipher config
- **Logging:** Serilog.AspNetCore 10.x — console + daily rolling file under `/logs`, 7-day retention
- **Encryption:** ASP.NET Data Protection with `SetApplicationName("ApStatsForOpenWrt")` — keys persisted to `/data/keys`
- **UI auth:** PBKDF2 hashed UI password via `PasswordHasher<T>` — NOT Data Protection (one-way)
- **Frontend template:** AdminLTE 4.0.0-rc7 — vendored to `wwwroot/lib/adminlte/` from the bundled zip in `support/`
- **Test stack:** xUnit v3 + bUnit + `Microsoft.Testing.Extensions.CodeCoverage` (Cobertura) — **NOT coverlet** (incompatible with .NET 10 MTP)
- **Container base:** `mcr.microsoft.com/dotnet/aspnet:10.0` (Debian) for v1; chiseled migration deferred to v2
- **IDE:** Visual Studio 2026 is the canonical development environment

Detailed rationale and version pins: `.planning/research/STACK.md`.
<!-- GSD:stack-end -->

<!-- GSD:conventions-start source:CONVENTIONS.md -->
## Conventions

- **DB writes are orchestrator-only.** Per-node SSH workers return POCOs; only the `PollOrchestrator` calls `SaveChangesAsync`, inside one coordinator transaction per cycle. SQLite is single-writer — multiple writers cause `database is locked`.
- **Mark-Offline-then-upsert.** At cycle start, all nodes/stations are flipped to `Offline` in the same transaction that upserts live data back to `Online`. Atomicity is the invariant.
- **MAC is the station identity.** A station can appear on different WLANs over time; `WLANStation.MacAddress` is the dedup key.
- **`PeriodicTimer.WaitForNextTickAsync` only.** Do NOT use `System.Threading.Timer` with a period — the tick semantics are different.
- **SSH timeouts are belt + braces.** `ConnectionInfo.Timeout = 5s` AND a linked `CancellationTokenSource(TimeSpan.FromSeconds(5))`. Always dispose `SshClient` inside `try/catch` (SSH.NET dispose-hang risk).
- **`/health` is plain HTTP.** It is a `MapHealthChecks` endpoint, never a Blazor route. It must NOT increment the visitor counter.
- **Visitor count is `Interlocked` + TCS.** Implemented via a singleton `CircuitTracker` fed by a `CircuitHandler`. Disconnect grace period = **30 s pause-debounce inside `CircuitTracker`** (260518-gpd). Blazor's `DisconnectedCircuitRetentionPeriod = 3 min` only covers unintended disconnects (network blip, sleep); F5 / tab close = graceful close = immediate `OnCircuitClosedAsync`, which is why the 30 s in-tracker debounce is required to suppress spurious cold-start kicks.
- **Tests use bUnit, not mocks of `CircuitHandler`.** Real circuits, real lifecycle. Coverage gate is line + branch ≥ 80%.
- **No `EnsureCreated` anywhere.** Always EF migrations.
- **`<Version>` is owned by `Directory.Build.props`.** Never declare `<Version>` (or `<AssemblyVersion>` / `<FileVersion>`) inside any csproj — MSBuild imports `Directory.Build.props` before each csproj's `<PropertyGroup>`, so a duplicate in a csproj silently overrides the props value and creates two-place version drift. `ArchitectureTests.NoCsprojDeclaresVersion_DirectoryBuildPropsIsTheOnlySource` pins this (260518-vbs).

See `.planning/research/PITFALLS.md` for the full anti-pattern list with phase mapping.
<!-- GSD:conventions-end -->

<!-- GSD:architecture-start source:research/ARCHITECTURE.md -->
## Architecture

Single .NET 10 process, three logical planes inside one container:

1. **SSH polling plane** (background) — `BackgroundService` (`PollingHostedService`) → `PollOrchestrator` → fan-out of `NodePoller` tasks → SSH.NET → parser → returns POCO `NodeProbeResult`s.
2. **Persistence plane** — `IDbContextFactory<DatabaseContext>` → repository layer (`INodeRepo`, `INetworkRepo`, `IStationRepo`, `IAppSettingsService`) → SQLite at `/data/AP-Stats-for-OpenWrt.db`.
3. **UI plane** — Blazor InteractiveServer (`MainLayout` styled with AdminLTE) → singleton `PollNotifier` (event-based pub/sub) → component subscriptions in `OnInitialized` / unsubscribe in `Dispose`, wrapped in `await InvokeAsync(StateHasChanged)`.

**Polling-policy gate:** singleton `CircuitTracker` (Interlocked counter + re-armable `TaskCompletionSource`); `BackgroundService` awaits `tracker.WaitForVisitorAsync(stoppingToken)` before each cycle. `/health` is a plain MapGet endpoint — structurally cannot increment the gate.

**Bootstrap order (sequential `IHostedService` registration):**
1. `MigrationsHostedService` — `Database.Migrate()` + seed defaults
2. `AppSettingsBootstrapHostedService` — load polling interval into singleton `AppSettingsService`
3. `PollingHostedService` — start, immediately await first visitor

**Build order = phase order:** Foundation → SSH Probing → Live UI Shell → Onboarding → Live Push → Visitor Gating → Packaging.

See `.planning/research/ARCHITECTURE.md` for the full component map and data flow.
<!-- GSD:architecture-end -->

<!-- GSD:skills-start source:skills/ -->
## Project Skills

No project skills found. Add skills to any of: `.claude/skills/`, `.agents/skills/`, `.cursor/skills/`, or `.github/skills/` with a `SKILL.md` index file.
<!-- GSD:skills-end -->

<!-- GSD:workflow-start source:GSD defaults -->
## GSD Workflow Enforcement

Before using Edit, Write, or other file-changing tools, start work through a GSD command so planning artifacts and execution context stay in sync.

Use these entry points:
- `/gsd-quick` for small fixes, doc updates, and ad-hoc tasks
- `/gsd-debug` for investigation and bug fixing
- `/gsd-execute-phase` for planned phase work

Do not make direct repo edits outside a GSD workflow unless the user explicitly asks to bypass it.

**Current state:** Project initialized 2026-04-28. 7 phases mapped, 71 v1 requirements. Next: `/gsd-discuss-phase 1` to gather Phase 1 (Foundation) context, or `/gsd-plan-phase 1` to plan directly.
<!-- GSD:workflow-end -->

<!-- GSD:profile-start -->
## Developer Profile

> Profile not yet configured. Run `/gsd-profile-user` to generate your developer profile.
> This section is managed by `generate-claude-profile` — do not edit manually.
<!-- GSD:profile-end -->

## Branching & Git workflow

- **Integration branch is `main`.** Defined milestones merge back to `main` with `--no-ff` plus an annotated `vX.Y` tag at close — see `milestone/v0.10` and `milestone/v0.11` for the established pattern.
- **Defined-milestone work goes on a dedicated `milestone/vX.Y` branch** cut from `main`. Create it at milestone start (`git switch -c milestone/vX.Y`) and keep all of that milestone's commits — features, fixes, milestone quick tasks, planning/docs — on it until the milestone closes and merges to `main`.
- **Small / ad-hoc / out-of-milestone work MAY be committed directly to `main`.** These branch rules apply ONLY to defined milestones — not to one-off fixes, doc tweaks, or quick tasks that aren't part of a milestone. (No hard stop on `main`; use judgment about whether the work belongs to a milestone.)
- **Every milestone close ends with a private mirror backup.** After the `--no-ff` merge to `main` + the annotated `vX.Y` tag, push the full repo to the private homelab Gitea backup: `git push --mirror gitea` (remote `gitea` = `http://dockerserver.michelecasa:8017/`; mirrors ALL branches, tags, and refs incl. `.planning` + `milestone/*`). Direction is LOCAL → Gitea ONLY — never configure Gitea as a pull-mirror from a public remote (the public repo has `.planning` stripped). This reinstates the backup-first invariant that superseded D-01 (see PROJECT.md Evolution Log 2026-06-09).
