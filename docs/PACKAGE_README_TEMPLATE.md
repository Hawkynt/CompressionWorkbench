# Package README template

Use this frame for every public NuGet package in this repository. Package-specific sections may be inserted between **Quick start** and **Dependencies**, but the common sections keep this order and emoji mapping.

```markdown
# Package.Name

[![NuGet](...)](...)
[![NuGet downloads](...)](...)
[![License](...)](...)
[![CI](...)](...)
![Target](...)

> One sentence describing what the package is for and what distinguishes it.

## 📦 Installation

```bash
dotnet add package Package.Name
```

## ✨ Features

- User-visible capability, not implementation trivia.
- Prefer concise bullets; put large capability sets in the support table below.

## 🧩 Support matrix

Use tables instead of prose lists whenever support varies by algorithm, format, codec, container, filesystem, profile, or operation.

| Format / algorithm | Read / decode | Write / encode | Notes | Reference |
| --- | :---: | :---: | --- | --- |
| [Name](neutral overview) | ✅ | ✅ | Relevant scope | [Specification / original paper / author site](primary source) |

For archive/filesystem state use the repository vocabulary:

| State | Meaning |
| --- | --- |
| **R** | Read / decode / extract only. |
| **WORM** | Read plus create a fresh output, but no in-place mutation. |
| **R/W** | Read plus supported modification/encoding semantics documented by the package. |
| **⚠️** | Deliberate subset; explain the supported profile/features in the row. |

Link the **format/algorithm name** to a neutral orientation page (usually Wikipedia when useful). Keep a separate **Reference** column for the normative specification, original paper, standards body, or canonical author/project site. Do not use a project-internal page as the only external definition of a public format when a primary source exists.

For large packages, keep a curated package-level matrix first and place the exhaustive inventory or deep implementation reference later in the **same README** under package-specific emoji-headed sections. The README is the canonical package document; do not split required package information into a companion `IMPLEMENTATION_NOTES.md`.

## 🚀 Quick start

Show the smallest realistic API example that proves the package's main value.

## 📚 API / architecture

Optional package-specific sections belong here. Examples: registry API, state model, container/codec split, pseudo-archive model, filesystem mutation model, building-block composition, exhaustive reference tables, validation matrices, and implementation caveats.

## 🔌 Dependencies

Use a table when there is more than one meaningful dependency.

## ⚠️ Limitations

Call out deliberate subsets, read-only formats, non-interoperable encoders, platform constraints, and other traps that a green support check would conceal.

## ❤️ Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see the repository [LICENSE](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE).
```

## Documentation rules

- Common headings use the emoji and order above. Package-specific subheadings may use additional emojis when that improves scanning.
- Prefer capability matrices over sentences such as “supports A, B, C, D…”.
- Keep deep technical material, exhaustive inventories, implementation archaeology, validation notes, and advanced examples in the package README after the concise front section; do not create a second package-manual file.
- Distinguish **implemented** from **fully interoperable**. A parser, metadata reader, internal round-trip, subset decoder, or rebuild-based writer is not the same claim as complete support.
- Document only package/project surfaces that actually exist. Never advertise an invented, planned, “coming soon”, or “not yet published” package ID as a package users should wait for or reference.
- Treat checked-in project files, the compiled registry/public API, and tests as evidence sources. Roadmaps, TODOs, issue ideas, naming guesses, and intended future releases are not support evidence.
- Avoid exact fast-moving counts in marketing text unless they are generated from the registry/build. A stale count is worse than no count.
- When a runtime registry can answer an exact current capability question, state that it is authoritative even if the README also contains an exhaustive human-readable inventory.
- Keep examples compilable against the public package surface, including the real package ID.
- Do not predict release/stability state. Versioning text must follow the checked-in version source and release tooling rather than phrases such as “once we ship X”.
- Use absolute GitHub/NuGet URLs for links that must also work when the README is rendered on nuget.org.
- Preserve the standard Support and License sections.
