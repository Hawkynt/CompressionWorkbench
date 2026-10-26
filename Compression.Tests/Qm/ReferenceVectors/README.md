# QM reference vectors

Every `.ts` file was written for this suite; every `.qm` file beside it was compiled from it by Qt's
own `lrelease` 6.12.0 (the `lrelease.exe` shipped in the PySide6-Essentials 6.12.0 wheel, Windows 11),
in its default mode: `lrelease -silent <name>.ts -qm <name>.qm`.

| Pair | What it covers |
| --- | --- |
| `sample_de` | two contexts; disambiguation comments where lrelease drops the first and keeps the second; a plural message; an unfinished and a vanished message; non-BMP text; language `de_DE` |
| `sample_pt`, `sample_pt_PT`, `sample_ar`, `sample_ja`, `sample_none` | the same messages with language `pt` (Brazil by default: French-style rule), `pt_PT` (English-style), `ar` (six forms: the plural is padded with null strings), `ja` (one form: the plural is truncated, no rules section) and no language at all |
| `edge_fr` | dependencies, an empty context (comment kept), a comment kept because a comment-less twin exists, a duplicate message, an unfinished empty and an obsolete message (both dropped), a plural with fewer forms than French has, length variants, whitespace and escaped markup |
| `empty_en` | a finished empty translation and an empty plural form (both null strings), a `<byte value="x7"/>` escape |

`sample_de_compress.qm` is `lrelease -compress sample_de.ts`: stripped records and the context table.

Live-tool tests (`ArchiveExternal`) run where `lrelease` is found (`CWB_TOOL_LRELEASE` or the PATH)
and where PySide6 is importable (`pip install PySide6-Essentials`, or a `pip install --target`
directory named in `CWB_PYTHONPATH`).
