# GSpot parity roadmap

The three media ledgers record **what is true today**, one row at a time. This page is the other half: the **order the remaining rows should be closed in**, and what "closed" has to mean before a row may be re-marked.

- [Media containers and ISO-BMFF brands](MEDIA-CONTAINER-COVERAGE.md)
- Audio codecs: the support ledger is the [audio package README](../Hawkynt.FileFormats.Audio/README.md); [how its identifiers are sourced and mapped](AUDIO-IDENTIFIER-REGISTRY.md)
- [Video codecs, FourCC identifiers, and frame/GOP analysis](VIDEO-CODEC-COVERAGE.md)

The ledgers stay authoritative for per-row state. Nothing here duplicates a marker; if the two ever disagree, the ledger is right and this page is stale.

## What parity does and does not mean

GSpot v2.70a identifies media. It does not decode most of what it names. Matching it is therefore an **identification and structural-inspection** goal, and the catalogue sizes are misleading as targets:

| Catalogue | Rows | What the number actually is |
| --- | ---: | --- |
| GSpot video codecs | 719 | FourCC *identifiers*, including raw pixel formats, reserved values, vendor aliases and dummy codecs. Far fewer independent codecs. |
| GSpot audio codecs | 245 | WAVE/ACM *format tags*, with several vendor registrations per underlying codec. |
| GSpot file types | — | Content-based container/raw-stream identification. |

So the roadmap is sequenced by **envelope first, identifier second, decoder last**. Recognising a stream is cheap and compounding; decoding it is expensive and narrow. The canonicalization rule in [the hub](MEDIA-CODEC-COVERAGE.md#canonicalization-rule) applies throughout: an identifier is not a codec, and a row may only be marked ✅ against a canonical family, never against an alias count.

## Definition of done per phase

A phase is finished when, for every row it claims:

1. the identifier is recognised and canonicalized to a family, not merely string-matched;
2. the surrounding container framing and extradata are validated rather than assumed;
3. decoder/encoder availability is reported **separately** from identification;
4. unsupported profiles fail explicitly instead of silently mis-reporting;
5. the corresponding ledger row carries its new marker, with the implementing project named.

A row that only gained a name mapping is 👁, not ✅. A container that can expose a payload it cannot decode is 📦. These distinctions are the entire point of the split ledgers and must survive the roadmap.

## Phase 1 — close the envelope gaps

Containers gate everything else: an unparsed envelope hides every stream inside it, so each of these unlocks identifier work that is currently unreachable.

1. **MPEG Program Stream / VOB / PES.** The largest remaining GSpot envelope. The PES layer is shared with the existing `FileFormat.MpegTs`, so this is mostly reuse rather than new parsing.
2. **FLV.** Tag and timestamp parsing plus demux. Also the precondition for the VP6-era video identifiers, which cannot be exercised without it.
3. **Legacy game/streaming envelopes** — Sierra VMD, Westwood VQA, Vivo `.viv`, Nullsoft NSV. Container-first: identify and demux before considering their codecs.
4. **ASF writing.** The read side landed with `FileFormat.Asf`; only the writer and editor are missing, which makes this a smaller job than its ledger row suggests.

## Phase 2 — make identifiers data, not code

The audio ledger already argues for this and the video ledger needs the same thing. Both should stop growing switch statements.

Build one identifier registry carrying, per entry: canonical family; WAVE/ACM numeric tags; RIFF/AVI FourCC aliases; ISO-BMFF sample entries and object types; Matroska codec IDs; Ogg mappings; RealMedia identifiers; profile/variant metadata; and decoder/encoder availability tracked independently of recognition.

That turns the 245-tag audio audit and the 719-row video audit into a **data and interop problem** instead of hundreds of unrelated implementations, and it is what lets the remaining identifier rows be closed in bulk rather than one at a time.

Alongside it, add the explicit ISO-BMFF brand registry from MP4RA plus the ftyps historical alias set, so brands stop being interpreted as track codecs.

## Phase 3 — structural inspection without decoding

GSpot's Visual GOP Structure is the behavioural target here, and it is reachable long before a decoder is.

The analyzer belongs in `Compression.Analysis`, stays codec-neutral, and consumes frame metadata supplied by elementary-stream parsers — full pixel decoding must not be required merely to inspect GOP structure.

**The codec-neutral half already exists**: `Compression.Analysis/Video/VideoFrameStructureAnalyzer.cs` turns a sequence of `VideoFrameSample` records into spacing statistics, GOP patterns, B-run and reorder depth, and the intra-versus-random-access disagreement counts.

**MPEG-1/2 is done and is the first producer.** `Compression.Analysis/Video/Mpeg12VideoFrameParser.cs` walks a video elementary stream, reads `sequence_header_code`, `group_start_code` and the picture header's `temporal_reference`/`picture_coding_type`, and emits a `VideoFrameSample` per coded frame with derived display order, coded size, byte offset, reference flag and a random-access flag kept strictly separate from "intra coded". It pairs complementary field pictures into frames; per-field detail, the sequence-header body and everything slice-level are out.

Remaining parser work in this phase:

- **AVC and HEVC**, still unwritten, and the reason each is done when it emits `VideoFrameSample` for a real file rather than when it merely recognises the stream.
- **A PES depacketizer**, so a container can feed the MPEG-1/2 parser. `FileFormat.MpegTs` reassembles per-PID PES, not elementary streams; the same layer would supply the PTS/DTS that an elementary stream cannot carry, which is why the parser leaves both timestamps null today.

The detailed target — spacing histograms, GOP patterns, maximum B-runs, frame-size statistics, bitrate windows, timestamp cadence, reorder depth, random-access truth, and decode- versus presentation-order views — is specified in the video ledger and is not restated here.

## Phase 4 — bounded decoders, in cost order

Only now, and deliberately smallest-first so each lands complete rather than leaving another partial family:

1. AVI RLE, Microsoft Video 1, Motion JPEG, Cinepak — bounded, well-documented legacy decoders.
2. MPEG-4 Part 2 / H.263 families, then the FLV/VP6-era codecs unlocked by Phase 1.
3. AVC and HEVC parsers feeding the Phase 3 analyzer *before* any decoder work.
4. Lossless AVI families and the game/legacy long tail.

## Explicitly out of scope

- Treating historical placeholder FourCCs as normative aliases without verification.
- Inferring a video codec from an unrelated audio registration.
- Counting alias rows toward coverage.
- Using GSpot or ftyps as an implementation specification. They are compatibility catalogues; behaviour comes from normative specifications, published vectors, licence-compatible references, or clean-room analysis.
