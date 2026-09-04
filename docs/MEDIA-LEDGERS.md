# Where the media ledgers live

This page owns no rows. It says which ledger answers which question, and states
the two rules every one of them is written under.

CompressionWorkbench deliberately separates **containers**, **audio codecs** and **video codecs**. A file being recognized or demuxed does not imply that its carried streams can be decoded, and a codec implementation does not imply that every historical container tag is wired to it. Each ledger belongs to the package that ships the code it describes:

| Domain | The ledger | Also |
| --- | --- | --- |
| Media containers | the *Media containers* table in the [archive package README](../Hawkynt.FileFormats.Archives/README.md) | — |
| Audio codecs | the [audio package README](../Hawkynt.FileFormats.Audio/README.md) | [how its identifiers are sourced and mapped](AUDIO-IDENTIFIER-REGISTRY.md) |
| Video codecs | the [`Hawkynt.FileFormats.Video` package README](https://github.com/Hawkynt/PNGCrushCS/blob/main/Hawkynt.FileFormats.Video/README.md) of the sibling PNGCrushCS project, which is where the decoders live | [which codecs were investigated and not implemented, and why](https://github.com/Hawkynt/PNGCrushCS/blob/main/Hawkynt.FileFormats.Video/codec-investigations.md) |
| Video frame / GOP structure | [this repository's analysis surface](VIDEO-FRAME-ANALYSIS.md) | — |

Those record what is true today. The order the remaining rows should be closed in, and what closing one has to mean, is in [the GSpot parity roadmap](GSPOT-ROADMAP.md).

## Baseline catalogues

The audit uses several independent historical/catalogue sources as coverage oracles:

| Catalogue | What it contributes |
| --- | --- |
| [GSpot file types](https://www.headbands.com/gspot/filetypes.html) | Broad content-based container/raw-stream/file identification baseline. |
| [GSpot audio codecs](https://gspot.headbands.com/audiocodecs.htm) | 245 historical WAVE/ACM audio format tags and aliases. |
| [GSpot video codecs](https://gspot.headbands.com/videocodecs.html) | 719 historical AVI/VfW video FourCC identifiers and aliases. |
| [ftyps.com](https://www.ftyps.com/) | Historical MP4/QuickTime `ftyp` brand catalogue. |
| [MP4 Registration Authority](https://mp4ra.org/) | Preferred current authority for ISO-BMFF registrations. |

GSpot and ftyps are used as **compatibility catalogues**, not as implementation specifications. Codec/container behavior comes from normative specifications, published vectors, licence-compatible references, or clean-room behavioral analysis.

## What a mark has to mean

Each ledger carries its own legend, because each is generated from the code it
describes and can only use the states that code distinguishes. Two states must
never blur wherever they both appear: a container that can expose or reframe a
payload it cannot decode is not a decoder, and an identifier that has been
mapped to a name is not an implementation.

The authoritative runtime inventory is `FormatRegistry` itself, queryable with:

```text
cwb formats
```

A ledger is a **gap map** checked against that registry, not a second one.

## Canonicalization rule

Do not count identifiers as codecs. `DIVX`, `DX50`, `XVID`, `FMP4`, and other compatible FourCCs can map to one underlying MPEG-4 Part 2 family; the GSpot audio table similarly contains multiple vendor registrations for G.711, G.723, G.726, AAC, AMR, Vorbis, and others. ISO-BMFF `ftyp` values are container/profile brands and are not track codec identifiers at all.

The intended model is therefore:

1. recognize the historical identifier;
2. canonicalize it to a codec/container/profile family;
3. validate the surrounding container framing/extradata;
4. report decoder/encoder support independently;
5. keep unsupported profiles explicit.

## Analysis parity

Matching GSpot also means matching its Visual GOP Structure: I→I, P→P and B→B spacing, GOP lengths and patterns, maximum B-runs, frame-size statistics, bitrate windows, timestamp cadence, reorder depth, random-access/keyframe truth, and decode-order versus presentation-order visualization.

That analyzer lives in `Compression.Analysis` and consumes codec-neutral frame metadata from elementary-stream parsers, so full pixel decoding is never required merely to inspect GOP structure. Its state and its remaining gaps are in [`VIDEO-FRAME-ANALYSIS.md`](VIDEO-FRAME-ANALYSIS.md).
