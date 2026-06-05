# Agent guide — CompressionWorkbench

Working agreement for **all** coding agents and human contributors working in
this repository. These rules are not optional. The full house spec lives in
the `Hawkynt/project-template` repo (`STANDARD.md`); this file is the
per-repo distillation.

## What this is

A **clean-room C# implementation** of compression primitives, archive
formats and analysis tools — every algorithm from scratch, no external
compression source code. This repo's CI/CD pipeline was the prototype for
the house standard; pipeline changes prototype in `Hawkynt/project-template`
now and get mirrored here.

## Commits

- **Group changes semantically/logically** — one codec/format/concern per
  commit; keep the diagnostic commit-body style (symptom → root cause →
  fix).
- **Every subject line starts with a prefix**: `+` added · `-` removed ·
  `*` changed · `#` bug fixed · `!` critical todo.
- Never start a subject with "fix"/"bugfix"/"changed"/"modified".
- **No AI traces anywhere**: no `Co-Authored-By` AI lines, no "Generated
  with" footers, no agent mentions in messages, comments, or authorship.

## The loop (always, in this order)

1. **Before committing**: build and run the required test tier until green
   (external-tool round-trips, OS integration, polyglot readers and
   wall-clock Performance tests are the advisory tiers — exactly the
   category scheme the house standard adopted from this repo). Tests must
   stay cross-platform: path/separator/case assumptions break the Linux leg.
2. **Commit** (rules above) and **push**.
3. **Wait for CI**; on `main` a green CI triggers the nightly (prerelease +
   GFS prune, same-day replace). Fix and loop until everything is green.

Stable releases are **manual** (`gh workflow run release.yml`) — never cut
one unless explicitly asked.

## Code conventions

- **Clean-room is the law**: never port or paraphrase external compression
  source; implement from specs/papers and cite them.
- Latest C# features; codecs are hot paths — measure, never make a
  round-trip slower without a stated reason.
- Per-package folders with their own `<Version>` — untouched packages keep
  their version so `--skip-duplicate` re-uses the published artifact.
- Round-trip tests (compress → decompress → byte-identical) are mandatory
  for every codec/format change, plus official test vectors where they
  exist.

## README & repo conventions

- Standard frame: title → badges → one-line `>` blockquote; fixed emoji
  mapping for the standard sections (`## 📦 Quick start`, `## ❤️ Support`,
  `## 📜 License`); the architecture/format sections keep plain headers.
- License is LGPL-3.0-or-later; the `## ❤️ Support` section and
  `.github/FUNDING.yml` stay intact.
