# Video payloads and frame-structure analysis

This repository ships **containers**, not video codecs. MP4/MOV, Matroska/WebM,
AVI, MPEG-TS, RealMedia and Smacker can expose a coded video payload, and
`Compression.Analysis` can describe that payload's frame and GOP structure — but
nothing here reconstructs a pixel. Exposing an elementary stream is not decoding
it, and this page never claims otherwise.

**The codec ledger is not here.** Video decoders and encoders live in the
sibling PNGCrushCS project, whose video package README is the authority on which
codecs are decoded, to what profile, and how far that reaches against ffmpeg's
own decoder census:

- [`Hawkynt.FileFormats.Video` package README](https://github.com/Hawkynt/PNGCrushCS/blob/main/Hawkynt.FileFormats.Video/README.md)
  — containers, codec highlights and the four decode/encode states.
- [`codec-coverage.md`](https://github.com/Hawkynt/PNGCrushCS/blob/main/Hawkynt.FileFormats.Video/codec-coverage.md)
  — the codec-by-codec accounting, including how the denominator is arrived at
  and which codecs were established as not implementable from files alone.

A second copy of that ledger here would be stale the day the other one moves, so
what follows is only the half this repository owns. The historical-identifier
catalogues both projects measure against, and the rule that an identifier is not
a codec, are in [`MEDIA-LEDGERS.md`](MEDIA-LEDGERS.md).

## GSpot-style frame/GOP analyzer

GSpot's Visual GOP Structure is worth copying as a **capability**, not as code. CompressionWorkbench already treats analysis as a first-class surface, so video parsers should expose enough metadata for a codec-independent `Compression.Analysis` layer to compute structure without requiring full pixel reconstruction.

**The analyzer half of that is done.** `Compression.Analysis/Video/VideoFrameStructureAnalyzer.cs` takes a sequence of `VideoFrameSample` metadata and returns I→I, P→P, B→B and random-access spacing statistics, the longest consecutive-B run, reorder depth, GOP patterns, and the two disagreement counts that catch a mislabelled stream — intra pictures that are not random-access points, and random-access points that are not intra. It reconstructs no pixels.

**The first producer exists.** `Compression.Analysis/Video/Mpeg12VideoFrameParser.cs` walks an MPEG-1 (ISO/IEC 11172-2) or MPEG-2 (ISO/IEC 13818-2 / ITU-T H.262) **video elementary stream** and emits one `VideoFrameSample` per coded frame, reconstructing no pixels. It fills `DecodeIndex`, `PresentationIndex` (from `temporal_reference` plus the group's base), `Kind` (from `picture_coding_type`: 1→I, 2→P, 3→B, 4→S for MPEG-1 D pictures), `SizeBytes`, `Offset`, `IsRandomAccess`, `IsReference` and `IsCorrupt`, and reports `closed_gop`/`broken_link` alongside the frames. Complementary field-picture pairs are combined into the frame they encode, so an interlaced MPEG-2 stream does not hand the analyzer two pictures sharing one `temporal_reference`.

Where the bitstream cannot answer honestly, the parser leaves the default rather than inventing a value:

- `DecodeTimestamp` and `PresentationTimestamp` are always null. A video elementary stream carries no timing; PTS/DTS live in the PES layer. `vbv_delay` is a buffer-occupancy hint and is not reinterpreted as a presentation time.
- `PresentationIndex` falls back to `DecodeIndex` for a whole group whenever that group's temporal references are repeated, sparse or wrapped past 1023, and the number of frames this happened to is reported as `DecodeOrderPresentationCount`.

What is still missing is at the end of this page. The per-frame model below remains the specification every parser has to meet.

### Per-frame data model

Every elementary-stream parser should expose, where the codec permits it:

- coded/decode index and presentation index;
- byte offset and coded size;
- DTS and PTS (or codec-native temporal order when timestamps are absent);
- canonical picture kind: I / P / B plus codec-specific IDR/CRA/BLA/SI/SP distinctions;
- key/random-access flag separately from “intra coded”;
- reference/non-reference flag;
- field/frame/interlace/progressive information;
- temporal layer / dependency level where applicable;
- quantizer/QP summary when cheaply available;
- corruption/truncation/discontinuity flags;
- codec-specific flags such as MPEG-4 NVOP/packed-bitstream and container drop/dup markers.

Decode order and presentation order must be stored separately. H.262 explicitly demonstrates that an `I B B P` display sequence can be coded as `I P B B`; collapsing those orders would make frame-distance analysis wrong.

### Statistics to compute

| Metric | Meaning |
| --- | --- |
| I→I | Distance between consecutive I/random-access pictures, in frames and time. |
| P→P | Distance between consecutive P pictures. |
| B→B | Distance between consecutive B pictures. |
| I→P / P→I | Reference cadence around GOP boundaries. |
| Max B-run | Longest consecutive B-picture run in presentation order. |
| GOP length | Frames from one random-access/GOP start to the next; min/max/mean/median/histogram. |
| GOP pattern | Compact strings such as `IBBPBBPBBP`, grouped by occurrence count. |
| Open/closed GOP | Whether pictures depend across the random-access/GOP boundary, where the codec exposes it. |
| Frame mix | Counts and percentages of I/P/B/other pictures. |
| Frame size by type | Min/max/mean/median bytes for I/P/B and key/non-key frames. |
| Bitrate windows | Instant/rolling bitrate, peak windows and timestamp location. |
| Timestamp cadence | Min/max/mean frame duration, jitter, duplicate timestamps and gaps. |
| Reorder depth | Maximum decode-vs-presentation displacement / buffered pictures. |
| Keyframe truth | Container keyframe flag versus elementary-stream random-access semantics. |
| Packed/drop/dup anomalies | NVOP, packed bitstream, duplicated/dropped frames, discontinuities where identifiable. |

Distances should be available in both **presentation-frame positions** and **time**. For constant-rate MPEG this gives the familiar integer I2I/P2P/B2B spacing; for VFR content the time-domain view is equally important.

### Visualizations / exports

The UI/CLI should eventually expose:

1. a compact GOP timeline (`I B B P B B ...`) with random-access markers;
2. frame-size bars aligned to that timeline;
3. an I2I/P2P/B2B histogram and summary table;
4. bitrate-over-time and frame-duration/jitter plots;
5. decode-order versus presentation-order view;
6. text/JSON/CSV export so results can be diffed in tests and automation.

The analyzer itself should remain codec-neutral. MPEG-2, MPEG-4 Part 2, AVC, HEVC, VC-1 and future parsers should feed the same frame model rather than each implementing its own statistics.

## What is left to build here

1. A **PES depacketizer**. `FileFormat.MpegTs` reassembles per-PID PES, not
   elementary streams, so no container yet feeds the parser — and PES is also
   where the timestamps the frame model reserves would come from.
2. **AVC and HEVC frame parsers**, feeding the same codec-neutral model rather
   than each computing its own statistics.
3. Within MPEG-1/2: per-field detail (`top_field_first`, `repeat_first_field`,
   `progressive_frame`, individual field sizes, 3:2 pulldown), the
   sequence-header body (resolution, aspect ratio, frame rate, bit rate), and
   slice-level work — a quantiser summary and detection of truncated or missing
   slices.
4. The UI and CLI surfaces listed above, with text/JSON/CSV export so results can
   be diffed in tests and automation.

Decoding is deliberately absent from that list. A parser that reads picture
headers is what frame-structure analysis needs; a decoder is the other project's
work.
