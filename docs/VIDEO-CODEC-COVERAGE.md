# Video codec and frame-analysis support

This ledger tracks **video elementary-stream codecs, pixel formats, codec identifiers, and frame-structure analysis** independently of the container that carries them.

CompressionWorkbench currently has substantially better media-container and audio-codec coverage than actual video decoding. MP4/MOV, Matroska/WebM, AVI, MPEG-TS, RealMedia and Smacker can expose video payloads, but extracting a coded elementary stream is not the same as decoding frames.

## Coverage sources

- [GSpot v2.70a video codecs](https://gspot.headbands.com/videocodecs.html) — 719 historical AVI/VfW FourCC identifiers.
- [GSpot mirror](https://www.headbands.com/gspot/videocodecs.html) — same catalogue where the subdomain is unavailable.
- [GSpot Visual GOP Structure](https://gspot.headbands.com/) — behavioral inspiration for frame/GOP inspection, not implementation source.
- Normative codec specifications and published conformance vectors remain the implementation source. For example, ITU-T H.262 defines I, P and B picture types and explicitly distinguishes coded/decode order from display order.

The 719 GSpot rows are **identifiers, not 719 independent codecs**. Raw pixel formats, reserved values, vendor aliases, dummy codecs, multiple FourCCs for one standard, and genuine independent codecs all appear in the same list. We should canonicalize identifiers before counting coverage.

## States

| Mark | Meaning |
| --- | --- |
| ✅ | Native frame decoding exists for the claimed family/profile. |
| 🟨 | Partial processing exists, but not full decode/profile coverage. |
| 📦 | Container can expose/reframe this payload but cannot decode it. |
| 👁 | Identifier only. |
| ⬜ | Missing. |

## GSpot FourCC family mapping

| Canonical family | Representative GSpot FourCCs | State | Current handling / target |
| --- | --- | :---: | --- |
| Raw RGB / bitfields | `_RAW`, `_RGB`, `_BIT`, `RAW `, `RGB `, `RGBA`, `RGB1`, `RGBO`, `RGBP`, `RGBQ`, `RGBR`, `BGR `, `BIT `, `DIB ` | ⬜ | Add shared pixel-format/frame abstraction and AVI BI_RGB/BI_BITFIELDS decoding. |
| Raw planar/packed YUV | `AYUV`, `I420`, `IYUV`, `NV12`, `NV21`, `YUY2`-family, `2VUY`, `IUYV`, `IYU1`, `IYU2`, `IMC1`-`IMC4`, `GREY` | ⬜ | Canonical pixel-format registry; validate dimensions, planes and strides without pretending a pixel format is a codec. |
| AVI RLE | `_RL4`, `_RL8`, `RL4 `, `RL8 `, `RLE4`, `RLE8`, `MRLE`, `RLE ` | ⬜ | Small, bounded first decoder target. |
| Microsoft Video 1 | `CRAM`, `MSVC`, `MSV1` | ⬜ | Small legacy decoder; high alias/checkmark payoff. |
| Motion JPEG / JPEG-in-video | `_JPG`, `JPEG`, `JPG `, `MJPG`, `MJPA`, `MJPB`, `AVI1`, `AVI2`, `AVID`, `AVRN`, `ADVJ`, `DJPG`, `DMB1`, `DPS0`, `DPSC`, `GEPJ`, `GPEG`, `LJPG`, `MC24`, `MJPX`, `FLJP`, `FMJP` | ⬜ | Route compatible aliases to one JPEG-frame decoder; keep vendor field/alpha variants separate where wire format differs. |
| PNG-in-video | `_PNG`, `PNG `, `PNG1`, `MPNG` | ⬜ | Reuse image PNG core for frame decoding where the payload really is PNG. |
| Cinepak | `CVID` | ⬜ | Legacy AVI/QuickTime decoder target. |
| DV / DVCPRO | `DVC `, `DVCP`, `DVSD`, `DVSL`, `DVPN`, `DVPP`, `AVDV`, `AVD1`, `CDVC`, `DV25`, `DV50`, `DVHD`, `DVH1`, `MDVF`, `PDVC`, `IPDV`, `DSVD` | 📦 | AVI/MP4 can carry/extract payloads; native DV frame/audio decode is missing. |
| MPEG-1 Video | `MPEG`, `MPG1`, `PIM1`, vendor aliases | 📦 | TS/other containers can expose elementary data; decoder/parser missing. |
| MPEG-2 / H.262 | `H262`, `MPG2`, `LMP2`, `EM2V`, `PIM2`, `MMES`, `MMIF` | 📦 | MPEG-TS demux exists; picture parser/decoder and GOP analyzer integration missing. |
| MPEG-4 Part 2 | `DIVX`, `DX50`, `XVID`, `3IV0`, `3IV1`, `3IV2`, `3IVX`, `BLZ0`, `FMP4`, `FVFW`, `M4S2`, `MP4S`, `MP4V`, `DM4V`, `DP02`, `NDIG`, `PVMM`, `RMP4`, `HDX4` | 📦 | MP4/AVI/MKV can identify/carry it; implement actual MPEG-4 Part 2 family once, then map compatible aliases. |
| Microsoft MPEG-4 v1/v2/v3 / DivX 3 | `DIV1`, `DIV2`, `DIV3`, `DIV4`, `DIV6`, `MP41`, `MP42`, `MP43`, `MPG3`, `MPG4`, `COL0`, `COL1`, `NAVI`, `MDVD`, `AP42` | ⬜ | Keep Microsoft generations distinct from ISO MPEG-4 Part 2 where bitstreams differ. |
| H.261 | `H261`, `M261`, `D261`, `L261`, `BITM` | ⬜ | Implement one normative decoder, then map aliases. |
| H.263 | `H263`, `I263`, `D263`, `L263`, `M263`, `S263`, `ILVR`, `LX63` | ⬜ | Normative H.263 family; keep Sorenson/Flash derivatives separate. |
| Sorenson / Flash H.263 | `FLV1` | ⬜ | FLV container first, then codec variant. |
| H.264 / AVC | `H264`, `AVC1`, `DAVC`, `L264` and container sample entries `avc1`/`avc3` | 📦 | MP4/MKV reframe to Annex-B; decoder and slice/GOP parser missing. |
| HEVC / H.265 | container entries `hvc1`/`hev1`; historical GSpot `H265` label | 📦 | MP4 demux understands configuration framing; decoder missing. Do not treat historical placeholder FourCCs as normative aliases without verification. |
| VC-1 / WMV | `WVC1`, plus WMV/Microsoft screen families such as `MSS1`, `MSS2` | ⬜ | ASF container first, then VC-1/WMV generations independently. |
| Intel Indeo 2/3/4/5 | `IR21`, `RT21`, `IV30`-`IV39`, `IV40`-`IV49`, `IV50`, `AEIK` | ⬜ | Separate bitstream generations; large alias sweep after each decoder. |
| Huffyuv / FFVH | `HFYU`, `FFVH`, `MHFY` | ⬜ | Lossless AVI target. |
| FFV1 | `FFV1` | ⬜ | Open lossless target with exact round-trip/interoperability tests. |
| Lagarith | `LAGS` | ⬜ | Lossless AVI target. |
| CamStudio | `CSCD` | ⬜ | Screen/lossless target. |
| CineForm | `CFHD`, `AHDV` | ⬜ | Wavelet family; profile/licensing/spec audit first. |
| JPEG 2000 / Motion JPEG 2000 | `IPJ2`, `LJ2K`, `MJ2C`, `MJP2`, `PVW2`, `PVWV` | ⬜ | Reuse one JPEG 2000 image core for individual frame decode. |
| VP5 / VP6 / VP7 | raw VP5 plus `FLV4`, GSpot audio-table On2 registrations | ⬜ | Needed for FLV/NSV-era files; do not infer video codec from unrelated audio registrations. |
| Smacker video | SMK2/SMK4 family; container `.smk` | 📦 | `FileFormat.Smk` exposes `VIDEO.bin`; implement native video decode. |
| Bink video | `BINK` and later Bink aliases | ⬜ | `Codec.BinkAudio` exists, but video is independent and missing. |
| FLIC | `FLIC` plus FLI/FLC container variants | 🟨 | Existing frame path; audit FLI/FLC/FLX variants, timing and malformed input. |
| Apple RPZA / QuickDraw / Pixlet | `RPZA`, `QDRW`, `PXLT` | ⬜ | QuickTime legacy codec backlog. |
| Screen/capture codecs | `FSV1`, `MSS1`, `MSS2`, `LSCR`, `RASC`, `G2M2`, `G2M3` | ⬜ | Implement by actual bitstream family, not by vendor bucket. |
| Game/legacy codecs | `DXGM`, `ROQV`, `KMVC`, plus VMD/VQA/Vivo/NSV carried codecs | ⬜ | Container-first long tail; use behavioral oracles + independent tests. |
| Dummy/frameserver/transport identifiers | `AVIS`, `RAVI`, `RAV_`, `RTV0`, `DFSC`, `DXRE`, `DXSB`, `MP2T`, `MP4T`, `OSIL` | 👁 | Identification metadata only; these are not normal video decoders. |
| DirectX texture formats | `DXT1`-`DXTZ`, `DXTC`, `DXTN` | 👁 | Route to image/texture domain rather than video-codec coverage. |
| Reserved/unknown/vendor texture tags | assorted `NVS*`, `NVT*`, `MTX*`, reserved DXT values | 👁 | Preserve factual alias names without claiming codec support. |

The full 719-row GSpot page remains the factual alias baseline. A future machine-readable alias registry should import/canonicalize those tags and let tests calculate coverage automatically rather than hand-counting Markdown rows.

## GSpot-style frame/GOP analyzer

GSpot's Visual GOP Structure is worth copying as a **capability**, not as code. CompressionWorkbench already treats analysis as a first-class surface, so video parsers should expose enough metadata for a codec-independent `Compression.Analysis` layer to compute structure without requiring full pixel reconstruction.

**The analyzer half of that is done.** `Compression.Analysis/Video/VideoFrameStructureAnalyzer.cs` takes a sequence of `VideoFrameSample` metadata and returns I→I, P→P, B→B and random-access spacing statistics, the longest consecutive-B run, reorder depth, GOP patterns, and the two disagreement counts that catch a mislabelled stream — intra pictures that are not random-access points, and random-access points that are not intra. It reconstructs no pixels.

**The first producer exists.** `Compression.Analysis/Video/Mpeg12VideoFrameParser.cs` walks an MPEG-1 (ISO/IEC 11172-2) or MPEG-2 (ISO/IEC 13818-2 / ITU-T H.262) **video elementary stream** and emits one `VideoFrameSample` per coded frame, reconstructing no pixels. It fills `DecodeIndex`, `PresentationIndex` (from `temporal_reference` plus the group's base), `Kind` (from `picture_coding_type`: 1→I, 2→P, 3→B, 4→S for MPEG-1 D pictures), `SizeBytes`, `Offset`, `IsRandomAccess`, `IsReference` and `IsCorrupt`, and reports `closed_gop`/`broken_link` alongside the frames. Complementary field-picture pairs are combined into the frame they encode, so an interlaced MPEG-2 stream does not hand the analyzer two pictures sharing one `temporal_reference`.

Where the bitstream cannot answer honestly, the parser leaves the default rather than inventing a value:

- `DecodeTimestamp` and `PresentationTimestamp` are always null. A video elementary stream carries no timing; PTS/DTS live in the PES layer. `vbv_delay` is a buffer-occupancy hint and is not reinterpreted as a presentation time.
- `PresentationIndex` falls back to `DecodeIndex` for a whole group whenever that group's temporal references are repeated, sparse or wrapped past 1023, and the number of frames this happened to is reported as `DecodeOrderPresentationCount`.

What is still missing: **AVC and HEVC parsers**, which remain unwritten. Within MPEG-1/2 the gaps are per-field detail (`top_field_first`, `repeat_first_field`, `progressive_frame`, individual field sizes, 3:2 pulldown), the sequence-header body (resolution, aspect ratio, frame rate, bit rate) and anything slice-level, including a quantiser summary and detection of truncated or missing slices. Nothing wires the parser to a container yet either: `FileFormat.MpegTs` reassembles per-PID **PES**, not elementary streams, so feeding it a `.ts` file needs a PES depacketizer first — which is also what would supply the timestamps.

The per-frame model below remains the specification every parser has to meet.

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

## Implementation order

1. Shared pixel-format/raw-frame abstraction.
2. Codec-neutral frame/GOP analysis model and statistics.
3. MPEG-1/2 picture-header parser first, because I/P/B and GOP semantics are simple and normative and MPEG-TS already exists.
4. AVI RLE, Microsoft Video 1, Motion JPEG and Cinepak as bounded legacy decoders.
5. MPEG-4 Part 2/H.263 families and FLV/VP6-era codecs.
6. AVC/HEVC parsers feeding the same analyzer before full decode; then decoders.
7. Lossless AVI families and the game/legacy long tail.
8. Table-driven import/canonicalization of the complete GSpot FourCC catalogue.
