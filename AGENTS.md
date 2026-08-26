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

## Branches and pull requests

- **Nothing lands on `main` directly.** Every change starts on its own
  branch, one branch per feature or issue, named `<kind>/<short-slug>` —
  `feat/`, `fix/`, `docs/`, `perf/`, `refactor/`, `chore/`.
- **One concern per branch**, the same rule the commit grouping follows: a
  branch that fixes two unrelated things is two branches.
- **Merge through a pull request**, never by pushing to `main`. The PR body
  says what changed and how it was verified; link the issue it closes with
  `Closes #n` so the merge closes it.
- **Delete the branch after merge.** A stale branch outlives its reason and
  starts to look like work in progress.

## The loop (always, in this order)

1. **Branch** off current `main` (see above).
2. **Before committing**: build and run the required test tier until green
   (external-tool round-trips, OS integration, polyglot readers and
   wall-clock Performance tests are the advisory tiers — exactly the
   category scheme the house standard adopted from this repo). Tests must
   stay cross-platform: path/separator/case assumptions break the Linux leg.
3. **Commit** (rules above) and **push the branch**.
4. **Open a pull request** and let CI run against it. Fix and loop until
   everything is green.
5. **Merge the PR**, then delete the branch. On `main` a green CI triggers
   the nightly (prerelease + GFS prune, same-day replace).

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

- Public NuGet package READMEs follow `docs/PACKAGE_README_TEMPLATE.md`.
  Common order: title → badges → one-line `>` blockquote →
  `## 📦 Installation` → `## ✨ Features` → `## 🧩 Support matrix` →
  `## 🚀 Quick start` → package-specific API/architecture →
  `## 🔌 Dependencies` → `## ⚠️ Limitations` → `## ❤️ Support` →
  `## 📜 License`.
- Support varies by algorithm/format/profile, so prefer tables over prose
  lists. Link the public format/algorithm name to a neutral overview and
  keep a separate Reference column for the specification, original paper,
  standards body, or canonical author/project site.
- Do not flatten useful technical history for presentation. If a package
  README becomes too deep for a NuGet landing page, move the useful detail
  into `IMPLEMENTATION_NOTES.md` and link it from the standard README.
- `IMPLEMENTATION_NOTES.md` uses the same emoji vocabulary for equivalent
  headings. A deep technical page may be denser, but it should not regress
  to an unrelated visual structure.
- Package documentation describes **existing evidence only**: checked-in
  packable projects, compiled/public APIs and registries, tests, and release
  tooling. Never invent or advertise a planned / “coming soon” / “not yet
  published” package ID, predict a release milestone, or turn roadmap intent
  into a support claim.
- Avoid hand-maintained claims of an exhaustive registry inventory when the
  registry itself can answer the question exactly. Prefer a curated table
  plus instructions for querying the authoritative runtime source.
- License is LGPL-3.0-or-later; the `## ❤️ Support` section and
  `.github/FUNDING.yml` stay intact.
