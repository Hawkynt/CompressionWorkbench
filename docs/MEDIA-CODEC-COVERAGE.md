# Media / codec coverage ledger

CompressionWorkbench already has a broad audio codec library and several media-container
readers, but those capabilities are spread across independent `Codec.*` and
`FileFormat.*` projects. This page turns the historical **GSpot v2.70a** coverage into a
reviewable implementation backlog and makes the distinction between **recognising a
format**, **parsing/demuxing it**, and **decoding its payload** explicit.

This is a gap ledger, not the authoritative runtime registry. The current registry remains
queryable with:

```text
cwb formats
```

`cwb formats` is backed by `FormatRegistry`; a checked item below means there is concrete
checked-in implementation evidence, not merely that a name appears in documentation.

## Sources and clean-room boundary

GSpot is used here as a **historical compatibility catalogue / behavioral oracle**, not as
an implementation source:

- [GSpot v2.70a home](https://www.headbands.com/gspot/)
- [file types identified](https://www.headbands.com/gspot/filetypes.html)
- [AVI audio codec-tag catalogue](https://www.headbands.com/gspot/audiocodecs.htm)
- [AVI video FourCC catalogue](https://www.headbands.com/gspot/videocodecs.html)

GSpot reports 719 AVI video identifiers, 245 AVI audio identifiers, and basic
identification of more than 60 additional file types. Those tag databases are valuable as
**alias/identification checklists**, but most rows are not distinct codecs. `DIVX`, `DX50`,
`XVID`, `FMP4`, and many other tags can describe the same underlying codec family.
CompressionWorkbench should therefore implement the codec family once and separately map
all compatible FourCC/WAVEFORMAT aliases to it.

When a missing codec or container is implemented, use the normative specification,
standards-body text, published test vectors, or a licence-compatible reference
implementation. Incompatible implementations may be used only as behavioral oracles.
Do not port GSpot implementation code.

## What a checkmark means

| Mark | Meaning |
| --- | --- |
| ✅ | Concrete implementation exists for this capability and has repository evidence. |
| 🟨 | Partial/subset support; the notes state the boundary. |
| 👁 | Identification only; no useful parse/demux/decode path yet. |
| ⬜ | Gap found by this audit. |
| — | Capability is not meaningful for that row. |

A format is not considered “supported” merely because its extension is known.

- **Detect** — content/magic-based recognition through a format descriptor or equivalent.
- **Parse / demux** — structural metadata and carried streams/entries can be recovered.
- **Decode video/audio** — coded payload becomes raw frames/PCM, not merely a byte stream.
- **Write / mux** — a fresh interoperable file/container can be emitted.
- **Edit** — an existing container can be modified through the repository's mutation model.

Every new checkmark should link the implementation and tests. For codecs, add official or
independently generated known-answer/interoperability vectors where practical.

## High-level media-container audit

These are the GSpot headline formats plus closely related formats already present in
CompressionWorkbench.

| Format / family | Detect | Parse / demux | Video decode | Audio decode | Write / mux | Current implementation / gap |
| --- | :---: | :---: | :---: | :---: | :---: | --- |
| AVI / RIFF AVI | ✅ | ✅ | ⬜ | 🟨 | 🟨 | [`FileFormat.Avi`](../FileFormats/FileFormat.Avi/) demuxes tracks and preserves codec FourCC/WAVEFORMAT data. PCM can be surfaced as WAV; compressed tracks remain codec payloads. Writer is classic RIFF AVI; OpenDML is a separate capability. |
| MP4 / MOV | ✅ | ✅ | ⬜ | ⬜ | ⬜ | [`Mp4Demuxer`](../FileFormats/FileFormat.Mp4/Mp4Demuxer.cs) extracts tracks; AVC/HEVC are converted to Annex-B but are **not decoded**. |
| Matroska / WebM | ✅ | ✅ | ⬜ | ⬜ | ⬜ | [`FileFormat.Matroska`](../FileFormats/FileFormat.Matroska/) demuxes tracks and normalises AVC framing. Payload decoding remains separate. |
| MPEG-2 Transport Stream | ✅ | ✅ | ⬜ | ⬜ | ⬜ | [`FileFormat.MpegTs`](../FileFormats/FileFormat.MpegTs/) exposes per-PID elementary streams. |
| MPEG Program Stream / VOB | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | GSpot baseline gap. Important because existing MPEG/AC-3/DTS audio work can be reused once PES/program-stream demux exists. |
| ASF / WMV | ⬜ | ⬜ | ⬜ | 🟨 | ⬜ | Container gap. WMA v1/v2, WMA Pro and WMA Lossless decoders already exist in `Codec.Wma*`; ASF wiring is missing. |
| FLV | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | GSpot baseline gap. Add FLV container parsing before treating Sorenson/VP6/AAC/MP3 payloads as separate codec tasks. |
| Raw DV / DV-in-AVI | 🟨 | 🟨 | ⬜ | ⬜ | ⬜ | AVI can carry/preserve DV payloads, but this audit found no native DV elementary-stream decoder. |
| Raw H.263 / H.264 / MPEG video | ⬜ | ⬜ | ⬜ | — | ⬜ | MP4/MKV can demux AVC to Annex-B; that is not H.264 decode support. Raw elementary-stream descriptors and video decoders remain gaps. |
| RealMedia / RealAudio | ✅ | ✅ | ⬜ | 🟨 | ⬜ | [`FileFormat.RealMedia`](../FileFormats/FileFormat.RealMedia/) parses the container; Cook/SIPR and other RealAudio-era codec projects cover part of the audio side. |
| Ogg / OGM | ✅ | ✅ | ⬜ | 🟨 | ⬜ | [`FileFormat.Ogg`](../FileFormats/FileFormat.Ogg/) exposes logical streams. Vorbis/Opus decoding exists separately; OGM video decoding depends on its carried codec. |
| Smacker `.smk` | ✅ | ✅ | ⬜ | 🟨 | ⬜ | [`FileFormat.Smk`](../FileFormats/FileFormat.Smk/) parses the container. `Codec.SmackerAudio` decodes SMKA and PCM paths; the video block is intentionally exposed raw and Bink-audio-in-SMK is not fully decoded here. |
| FLIC `.fli/.flc/.flx` | ✅ | ✅ | 🟨 | — | ⬜ | Existing FLI/FLC pseudo-archive/frame path covers the animation family; keep variant/profile tests tied to the GSpot aliases. |
| Sierra VMD | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | GSpot baseline gap. |
| Westwood VQA | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | GSpot baseline gap. |
| Vivo `.viv` | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | GSpot baseline gap. |
| Nullsoft NSV | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | GSpot baseline gap. |
| Shockwave Flash `.swf` | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ | GSpot baseline gap; container/tag parsing should be separated from individual embedded codecs. |
| HLS / extended M3U | ✅ | ✅ | — | — | ⬜ | [`FileFormat.M3u8`](../FileFormats/FileFormat.M3u8/) handles HLS playlists/segments. Generic legacy playlist semantics should not be inferred beyond that implementation. |

## GSpot file-type baseline

The original GSpot file-type page deliberately mixed media with archives, images,
executables, playlists, and documents. Keep that breadth: unknown-file identification is a
CompressionWorkbench feature too. The table below maps each GSpot-era file type to the
most relevant current area. A row marked **gap** means no matching implementation was
located during the 2026-09-01 audit; it is intentionally conservative.

### Audio / video / playlist rows

| GSpot-era type | Status | CompressionWorkbench mapping / next implementation step |
| --- | :---: | --- |
| AMR | 🟨 | `Codec.AmrNb` / `Codec.AmrWb` decode exists; add/verify raw `.amr` container recognition and framing. |
| AIFF | ✅ | [`FileFormat.Aiff`](../FileFormats/FileFormat.Aiff/) |
| AIFF-C | 🟨 | Audit compressed AIFC variants independently; do not inherit the AIFF checkmark automatically. |
| ASX | ⬜ | Add playlist/container descriptor. |
| Brando NXV | ⬜ | Add signature/container research and parser. |
| CD-XA | ✅ | [`FileFormat.Xa`](../FileFormats/FileFormat.Xa/) + `Codec.XaAdpcm`. |
| Creative VOC | ✅ | [`FileFormat.Voc`](../FileFormats/FileFormat.Voc/) reads/writes VOC and Creative ADPCM paths. |
| DGIndex / DVD2AVI D2V | ⬜ | Add project-file parser if retained as a detection target. |
| DVD IFO/BUP | ⬜ | Add DVD navigation-structure parser; keep VOB/PES work in MPEG Program Stream. |
| Extended M3U | 🟨 | HLS/M3U8 exists; audit generic M3U playlist behavior separately. |
| FLAC | ✅ | [`FileFormat.Flac`](../FileFormats/FileFormat.Flac/) + `Codec.Flac`. |
| FLV | ⬜ | Add FLV container parser/demuxer. |
| FLIC | ✅ | Existing FLI/FLC frame/pseudo-archive path. |
| Matroska | ✅ | [`FileFormat.Matroska`](../FileFormats/FileFormat.Matroska/) |
| MCF | ⬜ | Research the historical Media Container Format before implementing. |
| MIDI | ✅ | `Codec.Midi` / MIDI SMF support in the audio package. |
| Monkey's Audio APE | ✅ | `Codec.MonkeysAudio`. |
| Musepack MPC | ✅ | [`Codec.Musepack`](../Codecs/Codec.Musepack/) covers SV7/SV8 decode/container work. |
| MPEG-2 TS | ✅ | [`FileFormat.MpegTs`](../FileFormats/FileFormat.MpegTs/) |
| MPEG / VOB | ⬜ | Implement MPEG Program Stream / PES demux; then route payloads to codec families. |
| AU / SND | ✅ | [`FileFormat.Au`](../FileFormats/FileFormat.Au/) |
| Nullsoft NSV | ⬜ | Add container parser/demuxer. |
| Ogg / OGM | ✅ | [`FileFormat.Ogg`](../FileFormats/FileFormat.Ogg/); carried video codecs remain independent. |
| raw VP5 file | ⬜ | Add VP5 elementary-stream identification/decoder. |
| OptimFROG | ⬜ | Add codec/container research. |
| PLS playlist | ⬜ | Add playlist parser if retained as an identification target. |
| WavPack | ✅ | `Codec.WavPack` plus file/container support in the audio package. |
| RealAudio | 🟨 | RealMedia parser plus RealAudio-era codec projects; audit raw `.ra` versions/profiles separately. |
| RealMedia | ✅ | [`FileFormat.RealMedia`](../FileFormats/FileFormat.RealMedia/) |
| RIFF family | ✅ | Multiple native RIFF users exist (`WAV`, `AVI`, `ANI`, WebP surgery); keep subtype recognition explicit. |
| RKAU | ⬜ | Add codec/container research. |
| SWF | ⬜ | Add SWF parser and embedded stream extraction. |
| Shorten SHN | ✅ | `Codec.Shorten` and Shorten file support. |
| Sierra VMD | ⬜ | Add container + audio/video codec research. |
| Smacker SMK | 🟨 | Container/audio support exists; video decode remains a gap. |
| SMIL | ⬜ | Add markup/playlist parser only if useful to recursive media resolution. |
| TrueAudio TTA | ✅ | `Codec.Tta` / TTA file support. |
| Vivo | ⬜ | Add container + H.263/G.723-family payload research. |
| Westwood VQA | ⬜ | Add container + VQA video/audio decoding. |
| Yamaha TwinVQ VQF | ⬜ | Add TwinVQ codec/container implementation. |

### Non-media rows retained for identification parity

These are not audio/video codec work, but they were part of GSpot's “what is this blob?”
coverage and should remain visible to avoid accidentally narrowing the compatibility goal.

| GSpot-era type | CompressionWorkbench responsibility |
| --- | --- |
| 7z, BZip2, GZip, RAR, ZIP | Archive/compression registry; already native repository domains. |
| ISO9660 image | Filesystem/disk-image registry. |
| EXE | PE/resource/packer analysis rather than a media codec. |
| PDF, HTML, XML | Document/identification targets; not media decoders. |
| GIF, JPEG, PNG, BMP, WMF | Image/pseudo-archive/detection targets; not video-codec rows. |
| Kazaa KPS playlist | Playlist/detection backlog if still useful. |

## AVI/WAVE codec-tag audit

GSpot's 719 video FourCC and 245 audio WAVE-format entries should not become 964 codec
projects. They should become **alias coverage tests** over a much smaller codec-family
registry.

For every GSpot tag we care about, track four independent facts:

1. **Known tag** — the identifier is recognised and has a canonical family/name.
2. **Container mapping** — AVI/WAV/ASF/etc. can route that tag to the correct codec.
3. **Decode/encode implementation** — the family has the actual codec operation.
4. **Profile boundary** — unsupported profiles/features are explicit rather than silently
   accepted.

That model lets dozens of aliases be checked at once when they genuinely mean the same
wire format, while still allowing vendor-specific variants to remain separate.

### Audio families already worth mapping against GSpot WAVE tags

The audio package already provides enough codec depth that the next useful step is often
**tag routing**, not another decoder.

| Family | Current codec capability | Typical GSpot-era tag group to map |
| --- | --- | --- |
| PCM / companded PCM | ✅ PCM, G.711 A-law, μ-law | `0x0001`, `0x0006`, `0x0007`, extensible-WAVE aliases |
| Microsoft / IMA / OKI ADPCM | ✅ decode; several families also encode | `0x0002`, `0x0010`, `0x0011`, Dialogic/IMA aliases |
| GSM 6.10 | ✅ decode | `0x0031` and compatible aliases |
| G.722 / G.726 | ✅ codec implementations | APICOM/ITU/vendor WAVE-tag aliases |
| MPEG Audio / MP3 | ✅ decode | MPEG/MP3 WAVE tags and compatible aliases |
| AAC | 🟨 AAC-LC-centered implementation | ISO/NEC/Fraunhofer/FAAD-era AAC tags; profiles must remain explicit |
| AC-3 / E-AC-3 | 🟨 decode | AC-3 tags; distinguish raw AC-3 from S/PDIF encapsulation |
| DTS | ✅ core decode | DTS/DTS-over-container tags; distinguish transport encapsulation |
| ATRAC / ATRAC3 | 🟨 codec projects exist | Sony/Canopus ATRAC-family tags; audit exact variant routing |
| WMA v1/v2 | ✅ decode | `0x0160`, `0x0161` |
| WMA Pro | ✅ decode | `0x0162` |
| WMA Lossless | ✅ decode | `0x0163` |
| RealAudio Cook / SIPR family | 🟨 decode paths exist | RealAudio-era tags; verify version/extradata boundaries |
| WavPack | ✅ encode/decode | `0x5756` and container-native signalling |
| Vorbis | ✅ decode | Ogg/Vorbis ACM aliases (`0x674F` etc.) should map to one Vorbis family |
| Speex | ✅ decode | Speex ACM tag(s) |
| FLAC | ✅ decode | `0xF1AC` plus native FLAC container signalling |
| AMR-NB / AMR-WB | ✅ decode | 3GPP/VoiceAge/Nokia aliases; raw-file framing is a separate concern |

### Video families exposed by the GSpot FourCC list

This is currently the larger implementation frontier. Container support must not be used as
a proxy for video decoding support.

| Codec family / group | Current state | Implementation target |
| --- | :---: | --- |
| Raw RGB / packed & planar YUV | ⬜ | Pixel-format registry + frame-size/stride validation; reuse across AVI, raw video and decoded outputs. |
| AVI RLE4 / RLE8 / Microsoft Video 1 | ⬜ | Small legacy decoders are good first interoperability targets. |
| Motion JPEG / JPEG-in-AVI | ⬜ | Route MJPG/JPEG vendor aliases to one JPEG-frame path where bitstream-compatible. |
| DV / DVCPRO | ⬜ | DV frame parser + audio extraction + video decode; then wire AVI/raw-DV aliases. |
| MPEG-1 / MPEG-2 Video | ⬜ | Elementary-stream parser/decoder, then PS/TS/AVI routing. |
| MPEG-4 Part 2 / DivX / Xvid / 3ivx / MSMPEG4 | ⬜ | Implement by actual bitstream family; do not create one codec per FourCC. |
| H.261 | ⬜ | Legacy ITU-T decoder and AVI alias mapping. |
| H.263 / Sorenson H.263-derived | ⬜ | Keep normative H.263 separate from FLV/Sorenson variants. |
| H.264 / AVC | 🟨 | MP4/MKV demux converts framing to Annex-B; **decoder is missing**. |
| HEVC | 🟨 | MP4 demux handles `hvcC` framing; **decoder is missing**. |
| Cinepak | ⬜ | Legacy decoder; useful for AVI/QuickTime coverage. |
| Intel Indeo 2/3/4/5 | ⬜ | Separate bitstream generations; map the many `IVxx` aliases after each decoder exists. |
| Huffyuv / Lagarith / FFV1 / lossless AVI families | ⬜ | Prioritise independently specified/open lossless formats and exact round-trip tests. |
| VP5 / VP6 | ⬜ | Needed for GSpot raw VP5 and FLV/NSV-era content. |
| WMV / VC-1 | ⬜ | ASF container comes first; then WMScreen/WMV/VC-1 families independently. |
| Smacker video | ⬜ | Container is already parsed; replace `VIDEO.bin` fallback with decoded frames. |
| Bink video | ⬜ | Audio codec exists; video remains a separate codec project. |
| FLIC | 🟨 | Existing frame path; audit FLI/FLC/FLX variants and malformed-input behavior. |
| JPEG 2000 / Motion JPEG 2000 | ⬜ | Reuse an eventual JPEG 2000 image core for frame decoding rather than duplicate logic. |
| Screen/capture codecs (CamStudio, MS Screen, Flash Screen, etc.) | ⬜ | Implement per actual bitstream family after container coverage. |
| Game/video long tail (VMD, VQA, Vivo, NSV-specific payloads) | ⬜ | Container-first, then codec-family projects with external interoperability vectors. |

## Suggested implementation order

The matrix points to a better order than simply walking the GSpot pages top-to-bottom.

1. **Connect already-implemented codecs to missing containers.** ASF/WMV is the clearest
   example because WMA decoders already exist. FLV and MPEG Program Stream/VOB similarly
   unlock existing AAC/MP3/AC-3/DTS work.
2. **Add a video pixel-format/frame abstraction.** Raw RGB/YUV, AVI BI_RGB/BI_BITFIELDS and
   decoded video all need the same safe representation of dimensions, strides, planes,
   chroma subsampling and bit depth.
3. **Take the small legacy decoders first.** AVI RLE, Microsoft Video 1, Cinepak, FLIC
   variants and Motion JPEG provide many GSpot checkmarks for comparatively bounded code.
4. **Then add large standards codecs.** MPEG-1/2 video, H.263, MPEG-4 Part 2, H.264/AVC,
   HEVC, and VC-1 should be separate focused changes with normative vectors.
5. **Sweep aliases last.** Once a family is correct, map GSpot's FourCC/WAVEFORMAT aliases
   and add table-driven registry tests proving that every claimed alias resolves to the
   intended family.
6. **Finish the game/legacy long tail.** VMD, VQA, Vivo, NSV, VP5/6, Smacker video, Bink
   video, TwinVQ, OptimFROG, RKAU, and similarly bounded historical formats become explicit
   backlog rows rather than disappearing from memory.

## Checklist for checking a row

Before changing a `⬜`/`🟨` to `✅`:

- identify the normative specification or clean-room behavioral contract;
- record licensing of any reference implementation used as an oracle;
- add content-based detection tests, including false-positive cases;
- add malformed/truncated input tests;
- add parse/demux tests that prove stream boundaries and metadata;
- add official or independently cross-checked codec vectors where available;
- for encoders/muxers, round-trip through at least one independent implementation where
  practical;
- wire aliases/tags to a canonical codec family without duplicating the decoder;
- ensure `cwb formats` / `FormatRegistry` exposes the real capability;
- update this ledger with the concrete implementation and test paths.

The goal is not to reproduce GSpot. The goal is to use its unusually broad historical
coverage as a regression-resistant list of things CompressionWorkbench should eventually
be able to recognise, inspect, demux, decode, and—where the format permits—write.