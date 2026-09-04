# Agent guide — CompressionWorkbench

Working agreement for **all** coding agents and human contributors working in
this repository. These rules are not optional. The full house spec lives in
the `Hawkynt/project-template` repo (`STANDARD.md`); this file is the
per-repo distillation.

## What this is

A **clean-room C# implementation** of compression primitives, archive
formats and analysis tools — every algorithm from scratch, with the narrow
exception of the vendored third-party sources described under *Code
conventions*. This repo's CI/CD pipeline was the prototype for
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

## Sourcing an implementation

Never write a format, codec, cipher or compression scheme out of your own understanding when
somebody has already got it right. Work **down** this ladder, stop at the first rung that applies,
and say in the commit body which rung you used and why the ones above it did not.

**1 — Licence-compatible source you can take.** MIT, BSD, Apache-2.0, LGPL, public domain: anything
this repository's LGPL-3.0-or-later can absorb. Search for it before writing anything. There are two
ways to take it and the choice is not cosmetic:

- **Vendor it** — a verbatim subtree under `Vendored/<Library>/` next to its own `LICENSE.txt`, kept
  in the upstream's own formatting. Do *not* restyle it: the whole point is that the next upstream
  version still applies cleanly, and a reformatted copy conflicts on every update. Keep it out of
  the published API surface with the `exclude-namespace` input of the `package-readme` action rather
  than by editing the source.
- **Convert it** — carry the algorithm across into this codebase properly. Converted code is *our*
  code, so every rule under "Code conventions" applies to it, including the current C# language
  version (C# 14) wherever that says the same thing more plainly. Do not restate those rules
  here or anywhere else: one stale copy of them is how this guide spent years asking for a brace
  style the code had never used. A conversion that still reads like C, or like a decompiler's
  output, is not finished.

Either way, record where it came from — a `THIRD_PARTY_NOTICES.md` in the package, or a
`THIRD-PARTY-NOTICE.<Name>.txt` beside the code. Attribution is a licence term, not a courtesy.

**2 — Licence-incompatible source: use it, but not its code.** GPL where we ship LGPL, anything
proprietary, anything with no licence at all. Read it and *build material from it*: a written
specification, a set of test cases, and a third-party oracle you can run to produce expected output.
Then implement from that derived material. Do not paste it, do not transliterate it line by line,
and do not carry its file layout or its identifier names across — that is still the same copy.

**Constants are not expression.** Tables, S-boxes, magic numbers, CRC polynomials, Huffman code
tables, quantisation matrices, window and filter coefficients: copy them exactly, from whichever
source is authoritative, on every rung of this ladder. A re-derived S-box is simply a wrong S-box,
and a table somebody worked out for themselves is the defect that nothing catches until real files
arrive. Where a value is arbitrary-but-agreed, matching it *is* the specification.

**3 — Original reference material.** The specification, the standard (RFC, ITU-T, ISO, ECMA), the
academic paper, the vendor's own documentation, the format author's write-up. Prefer the normative
text over anybody's description of it; where the two disagree, the normative text wins and the
disagreement is worth a comment.

**4 — Other trusted sources.** Reverse-engineering write-ups, articles and blog posts by named
people with a track record, and long-lived project wikis that cite their evidence.

**5 — Untrusted material, by agreement only.** Forum answers, unattributed gists, wiki edits with no
provenance. Only when nothing above exists, and only where several *independent* sources agree —
majority vote, discounting the ones that plainly copied each other. Treat the result as a hypothesis
and mark it as one in the code.

Whatever rung you land on, the finished implementation is judged the same way: it must agree with an
oracle or with real files, not merely compile and look plausible. When a licence-incompatible
implementation was your oracle, keep the comparison as a test wherever it can run, and where it
cannot, commit the captured expected output with a note saying what produced it.

## Code conventions

- **Clean-room is the default**: implement from specs and papers and cite
  them. Do not port or paraphrase external source into our own algorithms.
- **Vendoring is the exception, and it is all-or-nothing.** A third-party
  implementation may be taken in whole, unmodified, when every one of these
  holds:
  - its licence permits redistribution under this repository's
    `LGPL-3.0-or-later`, and its notice files come with it, unchanged;
  - it is **fully managed** — no P/Invoke, no native binaries, no unsafe
    blocks, no extra package references. A codec that needs a native library
    is not vendored, it is refused;
  - it lands under `Vendored/` inside the one project that uses it, at a
    **pinned upstream revision**, and is listed in the vendoring note for
    that area (`Codecs/VENDORED_AUDIO_CODECS.md` is the pattern: source,
    revision, licence, local path, and any local modification);
  - it stays byte-identical to that revision. Strict analysis is scoped off
    for `Vendored/**` in `.editorconfig` rather than by editing the copies,
    so a later revision can be dropped straight in. Our own integration code
    gets no such exemption.

  Paraphrasing a vendored codec into our own namespace is the one thing this
  does not permit: take it whole and credit it, or write it from the spec.
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
