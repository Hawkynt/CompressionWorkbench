# CI/CD Pipeline — CompressionWorkbench

> Everything in this folder is the automated pipeline for this repository.
> Workflows live here, their helper scripts live in `scripts/`.

## What this does

Seven workflows — one of them an internal, shared build block — and four helper scripts:

| File                            | Trigger                                  | Purpose                                        |
|---------------------------------|------------------------------------------|------------------------------------------------|
| `ci.yml`                        | PR + `workflow_call` + manual            | Build + categorised test tiers on ubuntu + windows |
| `release.yml`                   | manual dispatch only                     | Call CI, publish, tag `v<version>`, cut a Release |
| `nightly.yml`                   | push to `main` + manual                  | Publish `nightly-YYYY-MM-DD` prerelease        |
| `_build.yml`                    | `workflow_call` (internal)               | SFX-stub staging + multi-RID publish           |
| `coverage.yml`                  | daily cron 03:17 UTC + manual            | Instrumented run + HTML coverage report        |
| `generate.yml`                  | push to any branch but `main` + manual   | Refresh derived files, commit back to the branch |
| `branch-screenshots.yml`        | push to any branch but `main`/`master` + manual | Recapture `docs/screenshots/*.png`, commit back |
| `scripts/version.pl`            | invoked by the workflows                 | Compute `X.Y.Z.BUILD`                          |
| `scripts/update-changelog.mjs`  | invoked by the workflows                 | Bucketise commits into CHANGELOG.md            |
| `scripts/prune-nightlies.mjs`   | invoked by the workflows                 | 3-gen (GFS) retention of nightlies             |
| `scripts/update-readme-screenshots.py` | invoked by the workflows          | Rewrite the screenshot row in the root README  |

## How it works

```
push to a working branch     pull request      push to main      manual dispatch
        |                         |                 |                   |
        v                         v                 v                   v
 generate.yml                  ci.yml           nightly.yml         release.yml
 branch-screenshots.yml   (ubuntu + windows,        |                   |
        |                  tiered tests)            |             runs ci.yml via
        v                                           |             workflow_call first
 commit the refreshed files                         |                   |
 back onto the branch                               v                   v
                                              _build.yml           _build.yml
                                                    |                   |
                              5 SFX stubs (win-x64, win-x86, win-arm64, linux-x64,
                              linux-arm64) staged into Compression.Lib/stubs/,
                              then the three release zips
                                                    |                   |
                                                    v                   v
                                        nightly-YYYY-MM-DD      GH Release v1.2.3
                                          (prerelease)          + NuGet packages
                                                    |
                                                    v
                                       scripts/prune-nightlies.mjs
                                       (GFS: 7 daily + 4 weekly + 3 monthly)
```


Coverage runs on its own daily schedule rather than inside `ci.yml` — see *Why it's built this way*.

## Test tiers

`ci.yml` runs one required tier and six advisory ones:

| Category            | Runs on every PR?      | Purpose                                    |
|---------------------|------------------------|--------------------------------------------|
| _default_           | ✓ (must pass)          | Unit tests, no external tools              |
| `EndToEnd`          | ✓ (allow-fail)         | Round-trip through real archivers          |
| `ExternalInterop`   | ✓ (allow-fail)         | Third-party readers over our output        |
| `ExternalFsInterop` | ✓ (allow-fail)         | Real filesystem drivers / fsck over our images |
| `OsIntegration`     | ✓ (allow-fail)         | 7-Zip / p7zip binary shell-out             |
| `PolyglotInterop`   | ✓ (allow-fail)         | Python/Perl/Ruby/Node readers              |
| `Performance`       | ✓ (allow-fail)         | Throughput and large-input measurements    |

The default tier is the required check; the external-tool tiers are advisory so an unavailable CLI on a runner doesn't block a merge. Filter with `TestCategory!=`, not `Category!=`:

```bash
dotnet test Compression.Tests \
  --filter "TestCategory!=EndToEnd&TestCategory!=OsIntegration&TestCategory!=ExternalInterop&TestCategory!=ExternalFsInterop&TestCategory!=PolyglotInterop&TestCategory!=Performance"
```

`ci.yml` also verifies that the NuGet meta-packages pack, and runs the shared
`Hawkynt/RepositoryTemplate/package-readme` action, which checks the six package
READMEs against the house template and regenerates their `REFERENCE.md`.

## What it's for

- Every PR is built and tested on ubuntu + windows before it can merge.
- Every push to `main` produces a nightly prerelease.
- Cutting a release is a deliberate manual act: it runs the full CI matrix, publishes, pushes the `Hawkynt.*` NuGet packages, and only then tags `v<version>`.
- Working branches keep their generated files (screenshots) current by themselves, so a PR never depends on a CI-only artifact.
- Old nightlies are auto-pruned on a **Grandfather-Father-Son** schedule.

## Why it's built this way

- **The release creates the tag; the tag does not create the release.** `release.yml` is manual dispatch only. It calls `ci.yml` through `workflow_call`, computes one coordinated version with `version.pl`, publishes, and tags last — so a tag never exists for a build that failed.
- **NuGet publication gates the GitHub release.** The `publish` job `needs` `publish-nuget`, which pushes through nuget.org Trusted Publishing (OIDC). A release cannot go green while the packages are missing.
- **Coverage is a metric, not a gate.** Instrumenting ~27,000 tests costs 131–233 minutes against 25 uninstrumented, and it was cancelled before reporting on 23 of its last 25 runs as a PR check. It now runs on its own daily cron with the hours it needs and blocks nobody. It is the only cron in the pipeline; everything else is event-driven.
- **Generated files are committed on the branch, not in CI.** `generate.yml` and `branch-screenshots.yml` run on working branches only and are refused on the default branch. Both commit through `Hawkynt/RepositoryTemplate/commit-generated-file`, and both carry an actor guard so a bot commit cannot retrigger them.
- **`_build.yml` runs on windows-latest for everything**, including the Linux CLI (via `--runtime linux-x64`). SFX stubs get embedded into `Compression.Lib`, so single-host staging avoids cross-runner artifact passing.
- **Stubs use `ExcludeStubs=true` during stub publish** to prevent the Roslyn PE size limit from kicking in when Compression.Lib embeds its own stubs.
- **3-generation (GFS) retention**, not "keep last N". GFS guarantees at least one build per week for a month and one per month for a quarter.

## Scripts

### `version.pl`

Resolves `<Version>X.Y.Z</Version>` from a `VERSION` file, the nearest-ancestor `Directory.Build.props` / `.targets`, or a csproj — preferring the shallowest `Directory.Build.props` that declares one. Build number is `git rev-list --count HEAD`.

```
perl .github/workflows/scripts/version.pl          # 1.0.0.71
perl .github/workflows/scripts/version.pl --base   # 1.0.0
perl .github/workflows/scripts/version.pl --build  # 71
perl .github/workflows/scripts/version.pl --list   # the files that declare a version
perl .github/workflows/scripts/version.pl --stamp  # writes X.Y.Z.BUILD into every declaring file
```

`--stamp` rewrites only the files that declare a version; projects that inherit from a stamped `Directory.Build.props` are not touched individually.

### `update-changelog.mjs`

Prepends a new section to `CHANGELOG.md`. Commit-subject convention: `+` Added, `*` Changed, `#` Fixed, `-` Removed, `!` TODO, anything else → Other. Sections are emitted in the order Added, Removed, Changed, Fixed, TODO, Other.

### `prune-nightlies.mjs`

GFS retention, defaulting to `DAILY_KEEP=7`, `WEEKLY_KEEP=4`, `MONTHLY_KEEP=3` and overridable per call. Dry-run with `--dry-run`.

### `update-readme-screenshots.py`

Rewrites the screenshot row in the root `README.md` after `branch-screenshots.yml` recaptures the images. The row it emits and the row in `README.md` must stay identical.

## Who maintains this

Every repo in the CompressionWorkbench / PNGCrushCS / AnythingToGif / ClaudeCodePortable family owns its own copy. When changing it, prototype here then mirror the change to the siblings.

## Release artifacts

| Artifact                                                   | Produced by          |
|------------------------------------------------------------|----------------------|
| `CompressionWorkbench-CLI-win-x64-<version>.zip`           | release + nightly    |
| `CompressionWorkbench-CLI-linux-x64-<version>.zip`         | release + nightly    |
| `CompressionWorkbench-UI-win-x64-<version>.zip`            | release + nightly    |
| `Hawkynt.Compression.Core`, `Hawkynt.FileFormats.{Archives,Audio,FileSystems}` NuGet packages | release (`publish-nuget`) |
| Coverage HTML report                                        | coverage.yml         |
