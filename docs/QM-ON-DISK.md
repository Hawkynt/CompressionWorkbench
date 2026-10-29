# Qt QM — on-disk notes

The reading side follows `QTranslator` in qtbase (`src/corelib/kernel/qtranslator.cpp`, LGPL-3.0),
which is the runtime that consumes these files. The writing side reproduces what `lrelease` emits;
`lrelease` lives in qttools under GPL-3.0, so its source was read only to write down the rules below,
and our writer is implemented from this page and checked against `lrelease` 6.12.0 output byte for
byte. Constant tables (the magic, section and field tags, plural-rule byte codes and the per-language
rule assignments) are copied exactly, because matching them *is* the format.

## File

```
magic      3C B8 64 18 CA EF 9C 95 CD 21 1C BF 60 A1 BD DD
section*   UInt8 tag, UInt32BE length, byte[length]
```

Sections, in the order `lrelease` writes them (each only when non-empty):

| Tag | Section | Content |
| --- | --- | --- |
| `0xA7` | Language | UTF-8 language code from the TS `language` attribute, e.g. `de_DE` |
| `0x96` | Dependencies | a sequence of Qt-streamed strings (UInt32BE byte length, UTF-16BE) naming other catalogs |
| `0x42` | Hashes | `(UInt32BE hash, UInt32BE offset)` pairs sorted by hash, then by offset; offset is into Messages |
| `0x69` | Messages | message records, back to back |
| `0x2F` | Contexts | only with `lrelease -compress`: a context hash table (below) |
| `0x88` | NumerusRules | the plural-selection program for the language (below) |

A reader stops at a zero tag or zero length.

## Message records

A record is a run of tagged fields ending with tag `1`:

| Tag | Field | Encoding |
| --- | --- | --- |
| 1 | end | — |
| 2 | source text (Qt 3, UTF-16) | obsolete; skipped |
| 3 | translation | UInt32BE byte length (`0xFFFFFFFF` = null string), UTF-16BE; repeated once per plural form |
| 4 | context (Qt 3, UTF-16) | obsolete; skipped |
| 5 | obsolete | 4 bytes, skipped |
| 6 | source text | UInt32BE length, UTF-8 bytes |
| 7 | context | UInt32BE length, UTF-8 bytes |
| 8 | comment (disambiguation) | UInt32BE length, UTF-8 bytes |
| 9 | obsolete | skipped |

`lrelease` writes translations first, then comment, source text and context (in that order), then
the end tag. An empty context, source or comment is written with length 0 — never omitted.

The hash of a record is the ELF hash (`h = (h << 4) + c; g = h & 0xF0000000; if g: h ^= g >> 24;
h &= ~g`) over the UTF-8 bytes of source text followed by comment, and `1` when that hash is `0`.

### Which messages are written

Every TS message except `obsolete`/`vanished` ones, and except unfinished ones whose translation is
empty. Before writing, each plural message's form list is padded with null strings, or truncated, to
the number of forms the language has (one for a language with no rules, or for a non-plural
message).

A message's comment is dropped (stored as `""`) unless the comment is already empty, or the context
is empty, or the TS file contains a message with the same context and source and an empty comment;
it is also kept when a message with the same context and source already had its comment dropped. So
of two messages differing only by comment, the first loses its comment and the second keeps it;
`QTranslator` finds the first by retrying a failed lookup with an empty comment.

Records are written in ascending order of (context, source, comment) compared as unsigned bytes. Two
messages with equal (context, source, comment) collapse to the first.

### Default mode vs `-compress`

By default every record carries all three of comment, source and context. With `-compress` a record
carries only what distinguishes it from its sorted neighbours that share its hash: comparing with the
previous and next record, the "common prefix" is 0 (different hash), 1 (same hash, different context),
2 (same context, different source), 3 (same source, different comment) or 4 (identical). The record
uses level `max(prefix with previous, prefix with next + 1)`: level 1 and 4 write comment, source and
context; level 3 writes source and context; level 2 writes context only. A level-2 record therefore
has no source text on disk at all — such a file cannot be turned back into a TS file losslessly.

`-compress` also adds the Contexts section: `UInt16BE tableSize` (151, 503, 1511, 5003, 15013 by
context count < 60 / 200 / 750 / 2500 / 10000, else count × 1.5), `UInt16BE table[tableSize]`, then
a pool of Pascal strings (UInt8 length, bytes) starting with a zero word; `table[elfHash(context) %
tableSize]` holds half the pool offset of a run of contexts with that bucket, each run padded to an
even offset with an empty string.

## Plural rules

The NumerusRules section is a byte program evaluated per form, forms separated by `0xFF`; the first
form whose rule matches is chosen, and when none matches the last form is used. Operators: `0x01` ==,
`0x02` <, `0x03` <=, `0x04` between (two operands); `0x08` negates; `0x10` applies `n % 10` and
`0x20` `n % 100` before comparing, `0x40` leading-thousands; `0xFD` and, `0xFE` or.

Rule sets and the languages `lrelease` assigns them to are copied in `QmNumerus.cs`. A language is
looked up first together with its territory and then without; only Portuguese has a territory-specific
assignment (Brazil: French style), and a bare `pt` counts as Brazil because that is Qt's default
territory for Portuguese. A language with one form (Japanese style) gets an empty program, so no
section. An unrecognised language gets no section and one form.

## Verification

`Compression.Tests/Qm`: TS files covering contexts, disambiguation comments (both dropping cases),
plural padding and truncation, unfinished/vanished/obsolete messages, duplicates, dependencies,
length variants, null strings, byte escapes and non-BMP text, compiled by `lrelease` 6.12.0 in its
default mode for German, French, Portuguese (bare and `pt_PT`), Arabic, Japanese, English and no
language. Our writer reproduces each file byte for byte; our reader turns each back into TS that both
our writer and the live `lrelease` compile to the same bytes; Qt's `QTranslator` (PySide6) resolves
lookups, the comment retry and plural selection in a catalog we wrote. A `-compress` build is read
(stripped records, context table) but not written.
