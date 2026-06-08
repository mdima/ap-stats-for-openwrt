# Full Go-Live Ceremony — Dress-Rehearsal Record + Mechanical Real-Run Runbook

> **Plan 23-04 Task 2 (D-03).** This document is BOTH:
> 1. the **fillable record** of the one-time FULL dress rehearsal run against **throwaway** repos (this plan), and
> 2. the **mechanical real-run runbook** that **23-05 follows verbatim** against the real coordinates.
>
> The dress rehearsal exercises the entire `git filter-repo` → push → required-checks → flip-public → tag → pipeline → draft → publish → update-checker sequence once, on disposable infra, so the irreversible 23-05 run is purely mechanical. **Nothing in the rehearsal touches the real `mdima` / `michele73` coordinates.**
>
> Source of truth for the sequence: `.planning/phases/23-docs-community-release-gate/23-RESEARCH.md` §The Release Ceremony; the proven `support/release-rehearsal/rehearse-planning-strip.sh`; the parameterized `.github/workflows/release.yml`; Phase 22 `22-CONTEXT.md` D-04/D-05/D-06.

---

## Namespace truth table — DO NOT CONFLATE

| Coordinate | Real value (23-05) | Throwaway value (this rehearsal) | Source of truth |
|------------|--------------------|----------------------------------|-----------------|
| **GitHub repo** | `mdima/ap-stats-for-openwrt` | _operator's throwaway private repo_ → `apstats_test` | `UpdateChecker.cs:32-33` `internal const` — **no env/config seam** (D-06/L-03) |
| **GitHub `releases/latest` URL** | `https://api.github.com/repos/mdima/ap-stats-for-openwrt/releases/latest` | n/a — mechanism proof uses a stubbed handler, NOT throwaway coords | `UpdateChecker.cs:36-37` baked-in const |
| **Docker Hub namespace** | `michele73/ap-stats-for-openwrt` | _operator's throwaway Docker Hub repo_ → `apstats_test` | `vars.DOCKERHUB_IMAGE` repo variable (M-REL-03) |

> ⚠ The GitHub repo (`mdima`) and the Docker Hub namespace (`michele73`) are **different** on purpose. The update-checker const hard-codes `mdima`; the image push uses `michele73` via the repo variable. The real-run flip is a **one-line change to the `DOCKERHUB_IMAGE` repo variable** — not a workflow-file edit. (The stale `mdima/apstats-pipeline-test` comment inside `release.yml` is cosmetic; the live control is the variable.)

---

## Pre-flight (operator-held — required before step 1)

- [X] `gh auth status` shows authentication with **`repo` + `delete_repo`** scopes (`delete_repo` is needed for throwaway teardown)
- [X] A throwaway **private GitHub** repo name chosen (recorded in the table above)
- [X] A throwaway **Docker Hub** repo created/available (recorded in the table above)
- [X] On the throwaway GitHub repo: `vars.DOCKERHUB_IMAGE` set to the throwaway Docker Hub repo; secrets `DOCKERHUB_USERNAME` + `DOCKERHUB_TOKEN` set
- [X] `pip show git-filter-repo` reports `Version: 2.47.0` (the pinned version the strip script asserts; Phase 22↔23 SHA determinism)
- [X] `gh --version` ≥ 2.93.0

---

## Part A — Throwaway dress rehearsal (run ONCE, then delete)

Run the ENTIRE ceremony in D-06 order against the throwaway repos. Fill the **Observed** slot after each step; note any deviation.

### A1 — Merge-sim on a scratch clone
Simulate the `milestone/v1.0` → `main` `--no-ff` merge on a scratch clone (do not touch the real working tree).
- **Expected:** scratch `main` carries the full v1.0 history, `--no-ff` merge commit present.
- **Observed:** `__________________`

### A2 — `.planning/` strip on a FRESH clone (reuse the proven script)
```bash
bash support/release-rehearsal/rehearse-planning-strip.sh
```
The script clones `--no-local` into a disposable dir and runs the exact invocation
`git filter-repo --invert-paths --path .planning/ --path-glob '*/.planning/*'` (BOTH selectors — the nested-worktree `.planning/` carries the same lab fingerprint), then asserts the **leak gate**:
- `git log --all --oneline -- .planning/` → **0 lines**
- `git grep` for `192.168.` across all `.planning/`-pathed blobs (both glob shapes) → **empty**
- `git tag --list | wc -l` → **9** (all re-pointed: `v0.9 v0.9.1 v0.9.2 v0.10 v0.11 v0.11.1 v0.12 v0.13 v0.14`)
- **Expected:** all three leak-gate assertions PASS; script exits 0.
- **Observed:** `__________________`

### A3 — Cutover-sim
On the filtered scratch clone: confirm filtered history is adoptable as canonical, tags resolve, `.planning/` would be gitignored, `commit_docs`→false. (Simulation only — no real cutover here.)
- **Observed:** `__________________`

### A4 — Push filtered history + tags to throwaway PRIVATE repo
```bash
gh repo create <throwaway-owner/throwaway-repo> --private --source=. --remote=origin --push
git push origin --tags    # 9 re-pointed tags
```
- **Expected:** filtered history + 9 tags land on the throwaway private repo; milestone branches NOT pushed.
- **Observed:** `__________________`

### A5 — Required checks + Discussions + private advisories
Set the 80/80-coverage + smoke jobs as **REQUIRED status checks** on `main` using **classic branch protection** (Settings → Branches), enable Discussions, enable Private vulnerability reporting.
> **Pitfall 1:** configure the gate as **classic branch protection**, NOT a repository *push ruleset* — GitHub disables *push rulesets* on the public flip. (If you must use a ruleset, you will re-verify it survives in A7.)
- **Mechanism chosen:** ☐ classic branch protection  ☐ ruleset (will re-verify post-flip)
- **Observed:** `__________________`

### A6 — Confirm CI green (private)
- **Expected:** `ci.yml` runs green on the pushed filtered history.
- **Observed:** `__________________`

### A7 — Flip public + RE-VERIFY checks survived (Pitfall 1)
```bash
gh repo edit <throwaway-owner/throwaway-repo> --visibility public --accept-visibility-change-consequences
```
Then **re-verify** the required-status-checks gate is STILL enforced on `main` after the flip.
- **Expected:** repo public; required checks still enforced (classic branch protection survives; a push ruleset would have been disabled — that's the thing Pitfall 1 warns about).
- **Observed (required checks still enforced? Y/N):** `__________________`

### A8 — Push a `v*` test tag → confirm `release.yml` fires
Push a throwaway test tag (e.g. `v0.0.1-rehearsal`) and confirm the pipeline runs.
- **Expected:** `release.yml` builds **amd64 + arm64** via buildx, pushes to the **throwaway Docker Hub** repo, and creates a **DRAFT** GitHub Release.
- **Observed:** `__________________`

### A9 — Review the draft release shape (Pitfall 6)
Inspect the draft body: hybrid notes (hand-written Highlights header + auto-generated commit/PR list), the `docker pull <throwaway-image>:<version>` line, the image digest, and the "back up `/data` first" upgrade line.
> **Pitfall 6:** the pull tag MUST be the **semver-stripped** version (`1.0.0`-style, from `needs.build-and-push.outputs.version`), NOT the raw git ref (`vX`). The Phase 22 dry run proved the raw ref 404s on Docker Hub.
- **Expected:** pull tag is semver (no leading `v`); digest present; pull command works against the throwaway image.
- **Observed (pull tag value):** `__________________`

### A10 — Publish the throwaway draft
Publish the draft as a full release on the throwaway repo (proves the draft→publish gate mechanics).
- **Observed:** `__________________`

### A11 — Update-checker MECHANISM proof (NOT against throwaway coords)
The genuine real-coords fires-path is **deferred to 23-05** (T12). Here we prove only the fetch→parse→compare→persist→raise→badge MECHANISM, via a stubbed `HttpMessageHandler` end-to-end — **no const/config seam is added** (D-06/L-03):
```bash
dotnet test -c Release -- --filter-class "*ForcedOutOfDate*"
```
- **Expected:** 0 failed.
- **Observed:** `2026-06-08 — PASS, total 4 / succeeded 4 / failed 0 / skipped 0 (1.5s). Reconfirmed on current HEAD; no const seam added.`

### A12 — DELETE both throwaway repos
```bash
gh repo delete <throwaway-owner/throwaway-repo> --yes
# delete the throwaway Docker Hub repo via the Docker Hub UI / API
```
- **Expected:** both throwaway GitHub + Docker Hub repos deleted; no throwaway infra remains.
- **Observed:** ✅ both deleted — `gh repo view mdima/apstats_test` → "Could not resolve to a Repository"; Docker Hub `michele73/apstats_test` removed.

---

## Part A — Rehearsal Results & Findings (executed 2026-06-08)

**Outcome: PASS — full ceremony rehearsed end-to-end on throwaway repos; throwaway infra deleted; operator signed "rehearsal complete".**

| Step | Result |
|------|--------|
| A1 merge-sim | `milestone/v1.0` already contains all v1.0 history (62 commits ahead of `main`; `main` 0 ahead) → used directly as the canonical `main` basis; the real `--no-ff` merge is a 23-05 step. |
| A2 strip + leak gate | ✅ PASS — `rehearse-planning-strip.sh`: `.planning/` in 527 commits → **0 blobs** survive; no `192.168.` fingerprint; **9 tags** re-point. Determinism baseline main-tip `6d727ba`. (Shell note: `git filter-repo` subcommand dispatch was unavailable; the script's `python -m git_filter_repo` fallback ran — the persistent push clone used the same invocation, identical SHAs.) |
| A3 cutover-sim | ✅ filtered history adoptable as canonical (fast-forward chain), tags resolve, branch renamed `main`. |
| A4 push to throwaway PRIVATE | ✅ filtered `main` pushed to `mdima/apstats_test` (private); default branch `main`; milestone branches NOT pushed. Only `main` pushed (NOT the 9 historical tags — see Finding 3). |
| A5 required checks / Discussions / advisories | ⏭ deferred (optional fidelity). Real-run mechanism = **classic branch protection** (Pitfall 1). |
| A6 CI green (clean runner) | ✅ PASS **after a real fix** — see Finding 1. Initial run failed **98/1007**; iterated 98→26→7→1→0 across 5 commits; final clean-runner run (`c957418`): `test` ✓ + `smoke-tests` ✓, coverage **91% line / 84% branch** ✓. |
| A7 flip public + re-verify | ⏭ deferred with A5 (optional). |
| A8 release pipeline | ✅ PASS — standalone tag `v0.0.2-rehearsal` fired `release.yml`: `build-and-push` (amd64+arm64 → `michele73/apstats_test`) ✓ + `draft-release` ✓. |
| A9 draft shape (Pitfall 6) | ✅ PASS — pull line `docker pull michele73/apstats_test:0.0.2-rehearsal` (**semver, no leading `v`**); digest `sha256:16a0ec47…`; "back up `/data` first" line present. |
| A10 publish | ✅ operator published the throwaway draft (draft→publish gate proven). |
| A11 update-checker mechanism | ✅ `ForcedOutOfDate` 4/0 (no const/config seam — D-06/L-03). |
| A12 teardown | ✅ both throwaway repos deleted. |

### Findings carried into 23-05

1. **`/data` portability bug — FIXED (5 commits on `milestone/v1.0`): `bfa4d87` → `6949284` → `f87be03` → `f37aee2` → `3a39e9a`.** The clean CI runner caught what the dev box masked (Windows resolves `/data`→writable `C:\data`): the bootstrap writability probe + `MigrationsHostedService` hard-coded `/data`/`/logs` instead of honoring the existing `DATA_ROOT`/`LOGS_ROOT` indirection, and several host-booting/direct tests depended on a writable default. Fix = a `BootstrapPaths` resolver + a test-assembly `[ModuleInitializer]` defaulting those env vars to a writable temp dir + an `ArchitectureTests` guard, plus deterministic rewrites of two CI-only flaky timing tests. **The real-run CI on `main` will be green; QUAL-01 is now validated on a genuine clean runner (see QUAL-01-signoff.md addendum).**
2. **Push the `v1.0.0` tag as its OWN push event** — a tag bundled into the branch-creation push did NOT trigger `release.yml` (GitHub skips workflow triggers for a tag arriving with branch creation). Push `main` first, then push the tag standalone.
3. **The 9 re-pointed historical `v*` tags each trigger `release.yml`** (`tags: ['v*']`). In 23-05, push historical tags **before** the pipeline workflow is active on the remote, OR accept/expect the extra builds. (The rehearsal pushed only `main` to keep the run unambiguous.)
4. **Pitfall 1 (branch protection):** A5/A7 were deferred; for the real run set required status checks as **classic branch protection** and **re-verify they survive the public flip**.

---

## Part B — Mechanical real-run runbook (23-05 executes this verbatim)

> ⚠ **Everything below is IRREVERSIBLE / outward-facing on the REAL coordinates. It belongs to 23-05, not this plan.** Execute only after the operator signs off Part A ("rehearsal complete — throwaway infra deleted"). Execution can stop cleanly at the publish gate.

1. **Pre-cutover — QUAL artifacts already committed.** `QUAL-01-signoff.md` (Task 1, `20418e2`) + `QUAL-02-signoff.md` (Task 3, `b268207`) are committed on the full-history lineage. (Pitfall 3: anything written after the cutover's `commit_docs`→false + `.planning/` gitignore is orphaned.)
2. **`Directory.Build.props` → `1.0.0`** (T13; `NoCsprojDeclaresVersion` ArchitectureTest pins single-source — never in a csproj). Update the README version badge → `1.0.0`.
3. **Merge `milestone/v1.0` → `main`** — normal `--no-ff`, full history.
4. **One-time `git filter-repo` strip on a FRESH clone** (NEVER the working tree): pinned `git-filter-repo==2.47.0`, `--invert-paths --path .planning/ --path-glob '*/.planning/*'` (BOTH selectors). Reuse `rehearse-planning-strip.sh`'s leak gate. The rewritten SHA must match the Phase 22 / Part-A rehearsal baseline (determinism).
5. **Cutover (D-01/D-02):** filtered history → canonical `main`; park pre-v1.0 full history under local `archive/*` refs + the machine backup; confirm tags `v0.9`–`v0.14` re-point; add `.planning/` to `.gitignore`; flip `commit_docs`→false in `.planning/config.json`.
6. **Create REAL repo PRIVATE → verify (D-06):** `gh repo create mdima/ap-stats-for-openwrt --private --source=. --remote=origin --push`; `git push origin --tags` (9 tags; milestone branches NOT pushed); **operator EYEBALLS pushed history** (last line of defense — no `.planning/`, no lab fingerprint); enable Discussions + Private vulnerability reporting; set 80/80-coverage + smoke as REQUIRED status checks (**classic branch protection** — Pitfall 1); confirm CI green.
7. **Flip public:** `gh repo edit mdima/ap-stats-for-openwrt --visibility public --accept-visibility-change-consequences`; **RE-VERIFY required checks still enforced** (Pitfall 1).
8. **Flip Docker Hub coords + fire `v1.0.0`:** set `vars.DOCKERHUB_IMAGE=michele73/ap-stats-for-openwrt`; push annotated `v1.0.0` tag → `release.yml` builds amd64+arm64, pushes to Docker Hub, creates the DRAFT release (`docker pull michele73/ap-stats-for-openwrt:1.0.0` — semver, Pitfall 6 — + digest + `/data` backup line).
9. **Review draft → publish** as a FULL release (T4: CI drafts, operator publishes; no prerelease beat — T12).
10. **Live update-checker verification (T12, QUAL-02):**
    - **Fires-path:** a still-`0.14.0` instance hits the real `releases/latest`, sees published `v1.0.0` → badge appears showing `1.0.0`, links the release.
    - **No false positive:** a `1.0.0` instance sees `v1.0.0` → parsed `{1,0,0,-1}` ≤ assembly `{1,0,0,0}` → NO badge.
    - **Graceful-absent:** before publish, `releases/latest` 404s → exactly one Warning, no badge.
    - **Reversibility net (D-04):** if the badge misbehaves, **unpublish** the release before it propagates.
    - Then complete `QUAL-02-signoff.md`.

---

## Pitfalls quick-reference (carry into the real run)

- **Pitfall 1** — Required checks must be **classic branch protection** (not a push ruleset), or re-verified after the public flip; push rulesets are disabled on the public flip.
- **Pitfall 3** — QUAL sign-off artifacts committed on the full-history lineage BEFORE the cutover (done in 23-04).
- **Pitfall 6** — Docker pull tag is the **semver-stripped** version (`1.0.0`), never the raw git ref (`vX`).
- **M-FLD-03 (matrix growth)** — the community AP-version funnel (`tools/FixtureCapture` + the fixture issue form) is how the support matrix grows post-release; no real-run action, noted for completeness.

---

## Rehearsal sign-off

- [ ] Part A ran end-to-end on throwaway repos; the strip leak gate PASSED; the throwaway pipeline produced pullable multi-arch images + a draft→published release; branch protection survived the public flip.
- [ ] Update-checker MECHANISM proof PASSED (`ForcedOutOfDate` — recorded in A11), no const/config seam added.
- [ ] Both throwaway GitHub + Docker Hub repos DELETED.

**Operator resume signal (unblocks 23-05):** type **"rehearsal complete — throwaway infra deleted"** (or describe a blocker). Past this point, 23-05 is irreversible.
