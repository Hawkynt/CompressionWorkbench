# Media capability coverage

CompressionWorkbench deliberately separates **containers**, **audio codecs**, and **video codecs**. A file being recognized or demuxed does not imply that its carried streams can be decoded, and a codec implementation does not imply that every historical container tag is wired to it.

The detailed ledgers are split accordingly. Support tables belong to the package that ships
the code, so the audio ledger is the audio package README rather than a page here:

- [Media containers](../Hawkynt.FileFormats.Archives/README.md) — *Media containers* table in the archive package README
- Audio codecs: the support ledger is the [audio package README](../Hawkynt.FileFormats.Audio/README.md); [how its identifiers are sourced and mapped](AUDIO-IDENTIFIER-REGISTRY.md)
- [Video codecs, FourCC identifiers, and frame/GOP analysis](VIDEO-CODEC-COVERAGE.md)

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

## What the marks mean

| Mark | Meaning |
| --- | --- |
| ✅ | Concrete checked-in implementation evidence exists for the claimed capability. |
| 🟨 | Partial/profile-limited support. |
| 📦 | Container can expose/reframe the payload but the payload is not decoded. |
| 🔌 | Codec exists but this identifier/container route is not wired yet. |
| 👁 | Identification/name mapping only. |
| ⬜ | Gap found by the audit. |

The authoritative runtime format inventory remains `FormatRegistry`, queryable with:

```text
cwb formats
```

The Markdown ledgers are a **gap map**, not a second registry.

## Canonicalization rule

Do not count identifiers as codecs. `DIVX`, `DX50`, `XVID`, `FMP4`, and other compatible FourCCs can map to one underlying MPEG-4 Part 2 family; the GSpot audio table similarly contains multiple vendor registrations for G.711, G.723, G.726, AAC, AMR, Vorbis, and others. ISO-BMFF `ftyp` values are container/profile brands and are not track codec identifiers at all.

The intended model is therefore:

1. recognize the historical identifier;
2. canonicalize it to a codec/container/profile family;
3. validate the surrounding container framing/extradata;
4. report decoder/encoder support independently;
5. keep unsupported profiles explicit.

## Analysis parity

The video ledger also defines a GSpot-inspired frame/GOP analyzer target: I→I, P→P and B→B spacing, GOP lengths/patterns, maximum B-runs, frame-size statistics, bitrate windows, timestamp cadence, reorder depth, random-access/keyframe truth, and decode-order versus presentation-order visualization.

That analyzer belongs in `Compression.Analysis` and should consume codec-neutral frame metadata supplied by elementary-stream parsers. Full pixel decoding should not be required merely to inspect GOP structure.
