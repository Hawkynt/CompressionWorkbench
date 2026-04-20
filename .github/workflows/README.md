# CI/CD Pipeline — CompressionWorkbench

> Everything in this folder is the automated pipeline for this repository.
> Workflows live here, their helper scripts live in `scripts/`.

## What this does

Three workflows, one shared build block, three helper scripts:

| File                            | Trigger                             | Purpose                                 |
|---------------------------------|-------------------------------------|-----------------------------------------|
| `ci.yml`                        | push + PR + `workflow_call`         | Build + categorised test tiers + coverage |
| `release.yml`                   | tag push `v*` + manual dispatch     | Cut a GitHub Release from a tag         |
| `nightly.yml`                   | successful CI run on `main`/`master`| Publish `nightly-YYYY-MM-DD` prerelease |
| `_build.yml`                    | `workflow_call` (internal)          | SFX-stub staging + multi-RID publish    |
| `scripts/version.pl`            | invoked by the workflows            | Compute `X.Y.Z.BUILD`                   |
| `scripts/update-changelog.mjs`  | invoked by the workflows            | Bucketise commits into CHANGELOG.md     |
| `scripts/prune-nightlies.mjs`   | invoked by the workflows            | 3-gen (GFS) retention of nightlies      |

## How it works

```
                push / PR
                    │
                    ▼
            ┌───────────────┐
            │    ci.yml     │──► tiered tests on ubuntu + windows
            └───┬───────┬───┘    + coverage on ubuntu
                │       │
    tag v* ─────┤       │  on success on main/master
                ▼       ▼
        ┌──────────┐  ┌─────────────┐
        │ release  │  │  nightly    │
        │  .yml    │  │   .yml      │
        └────┬─────┘  └─────┬───────┘
             │              │
             ▼              ▼
        (both call _build.yml)
             │              │
             │   Publishes 5 SFX stubs (win-x64, win-x86, win-arm64,
             │   linux-x64, linux-arm64) into Compression.Lib/stubs/
             │   before building the three final zips.
             ▼              ▼
     GH Release v1.2.3   nightly-YYYY-MM-DD (prerelease)
                                │
                                ▼
                       scripts/prune-nightlies.mjs
                       (GFS: 7 daily + 4 weekly + 3 monthly)
```

## Test tiers

`ci.yml` runs four categories of tests:

| Category           | Runs on every PR?      | Purpose                              |
|--------------------|------------------------|--------------------------------------|
| _default_          | ✓ (must pass)          | Unit tests, no external tools        |
| `EndToEnd`         | ✓ (allow-fail)         | Round-trip through real archivers    |
| `OsIntegration`    | ✓ (allow-fail)         | 7-Zip / p7zip binary shell-out       |
| `PolyglotInterop`  | ✓ (allow-fail)         | Python/Perl/Ruby/Node readers        |

Core tests are required; the external-tool tiers are advisory so an unavailable CLI on a runner doesn't block a merge.

## What it's for

- Every PR is built and tested on ubuntu + windows before it can merge.
- Every merge to `main`/`master` produces a **tested** nightly prerelease.
- Every `v*` tag cuts a proper release with three zips: Windows UI, Windows CLI, Linux CLI.
- Old nightlies are auto-pruned on a **Grandfather-Father-Son** schedule.

## Why it's built this way

- **No cron triggers.** Event-driven only — CI fires on PRs, nightlies fire when CI passes on main, releases fire on tag push.
- **Release calls CI via `workflow_call`.** Tag pushes don't retrigger `on: push` workflows; calling ci.yml explicitly keeps tests and releases in lockstep with zero copy-paste.
- **Nightly builds from the `workflow_run` payload's SHA**, not branch tip — so a nightly is always a build of code CI actually validated.
- **`_build.yml` runs on windows-latest for everything**, including the Linux CLI (via `--runtime linux-x64`). SFX stubs get embedded into `Compression.Lib`, so single-host staging avoids cross-runner artifact passing.
- **Stubs use `ExcludeStubs=true` during stub publish** to prevent the Roslyn PE size limit from kicking in when Compression.Lib embeds its own stubs.
- **3-generation (GFS) retention**, not "keep last N". GFS guarantees at least one build per week for a month and one per month for a quarter.

## Scripts

### `version.pl`

Reads `<Version>X.Y.Z</Version>` from the first csproj found at root or one level deep. Build number is `git rev-list --count HEAD`.

```
perl .github/workflows/scripts/version.pl          # 1.0.0.71
perl .github/workflows/scripts/version.pl --base   # 1.0.0
perl .github/workflows/scripts/version.pl --build  # 71
perl .github/workflows/scripts/version.pl --stamp  # writes X.Y.Z.BUILD into every csproj
```

### `update-changelog.mjs`

Prepends a new section to `CHANGELOG.md`. Commit-subject convention: `+` Added, `*` Changed, `#` Fixed, `-` Removed, `!` TODO, anything else → Other.

### `prune-nightlies.mjs`

GFS retention with `DAILY_KEEP=7`, `WEEKLY_KEEP=4`, `MONTHLY_KEEP=3`. Dry-run with `--dry-run`.

## Who maintains this

Every repo in the CompressionWorkbench / PNGCrushCS / AnythingToGif / ClaudeCodePortable family owns its own copy. When changing it, prototype here then mirror the change to the siblings.

## Release artifacts

| Artifact                                                   | Produced by          |
|------------------------------------------------------------|----------------------|
| `CompressionWorkbench-CLI-win-x64-<version>.zip`           | release + nightly    |
| `CompressionWorkbench-CLI-linux-x64-<version>.zip`         | release + nightly    |
| `CompressionWorkbench-UI-win-x64-<version>.zip`            | release + nightly    |
| Coverage HTML report                                        | ci.yml (coverage job)|
