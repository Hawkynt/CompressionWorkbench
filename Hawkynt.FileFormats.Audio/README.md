# Hawkynt.FileFormats.Audio

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.FileFormats.Audio.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.Audio/)
[![License](https://img.shields.io/badge/license-LGPL--3.0--or--later-blue)](https://www.gnu.org/licenses/lgpl-3.0.html)

> Pure-managed audio codecs and container readers / writers extracted from
[CompressionWorkbench](https://github.com/Hawkynt/CompressionWorkbench). Sister package to
`Hawkynt.FileFormats.Images` / `Hawkynt.FileFormats.Archives` / `Hawkynt.FileFormats.FileSystems`,
all built on top of `Hawkynt.Compression.Core`.

The package bundles every audio-domain assembly (codecs + containers + tracker / chiptune / game
audio bundles) into `lib/`, so consumers add a single dependency and get the full audio surface.
`Hawkynt.Compression.Core` ships separately and is referenced as a NuGet `<dependency>` rather
than bundled, so installing both packages doesn't create duplicate DLLs.

## When to use this package

- You need a **pure-managed audio codec** without dragging in `libsamplerate` / `libflac` / FFmpeg
- You're building a tool that needs to read or write WAV / AIFF / AU / FLAC / ALAC / OGG / MP3 /
  MIDI files from C# without Windows Media Foundation or platform-specific APIs
- You want to inspect tracker (MOD / S3M / XM / IT) or chiptune (PSF) files programmatically
- You're processing **game audio bundles** (Wwise BNK, FMOD, AKB, AWB) and need their layout

Skip it when:

- You only need raw PCM I/O — `Hawkynt.Compression.Core` already exposes
  `Codec.Pcm.PcmCodec.SplitInterleavedPcm` / `ToWavBlob` indirectly, and PCM is in the smaller
  `Hawkynt.Compression.Core` graph through its dependency chain
- You need codec-quality features (resampling, normalisation, EBU R128 loudness) — those live
  in dedicated audio-DSP libraries; this package targets format I/O, not signal processing

## Quick start

```csharp
using Codec.Pcm;
using FileFormat.Wav;

// Read a stereo 16-bit WAV and split it into mono left/right WAVs.
var blob   = File.ReadAllBytes("input.wav");
var parsed = new WavReader().Read(blob);
foreach (var (name, monoWav) in PcmCodec.SplitInterleavedPcm(
             parsed.InterleavedPcm,
             parsed.NumChannels,
             parsed.SampleRate,
             parsed.BitsPerSample))
  File.WriteAllBytes($"{name}.wav", monoWav);
```

## The container / carried-data model

Every audio file is surfaced as a **pseudo-archive**: the *container* is the archive,
and everything it carries is listed as *pseudo-files*. The entry `Kind` makes the
distinction explicit:

| Kind        | Meaning                                                                              |
| ----------- | ------------------------------------------------------------------------------------ |
| `Container` | The byte-exact original container (`FULL.<ext>`) — extracting it round-trips the file |
| `Stream`    | A carried elementary bitstream (e.g. one Ogg logical stream's packets), still coded   |
| `Track`     | A carried audio/video track inside a multi-track container                            |
| `Channel`   | One decoded speaker as a playable **mono PCM WAV**                                    |
| `Tag`       | Carried metadata (Vorbis comments, ID3, bext, markers, …)                             |
| `Sample` / `Pattern` / `Instrument` | Tracker-module payloads (MOD / S3M / XM / IT)                  |

Channel naming follows FFmpeg's `libavutil/channel_layout`: when the container carries
an explicit speaker bitmap (`WAVE_FORMAT_EXTENSIBLE.dwChannelMask`, CAF channel bitmap)
each mono WAV is named for its real speaker; otherwise the FFmpeg default layout for the
channel count applies — mono, stereo, 2.1, 4.0, 5.0, 5.1, 6.1, 7.1, 5.1.4, 7.1.4, 9.1.4,
9.1.6 and NHK **22.2** (24 channels: `FRONT_LEFT` … `TOP_SIDE_LEFT` … `BOTTOM_FRONT_RIGHT`).
Channel counts beyond the table degrade to `CH_n`, so *any* multi-channel stream stays
decodable to per-speaker mono WAVs and reassembles losslessly from them
(`Codec.Pcm.ChannelLayout` + `PcmCodec.SplitInterleavedPcm` / `Interleave`).

## Contents

State legend:
- **R** — read / decode only.
- **WORM** — Write-Once-Read-Many: can list / extract / decode AND can synthesise / encode a fresh
  output from scratch, but cannot modify an existing file in place.
- **R/W** — full read + true encoder; codec or container can produce the format from PCM /
  source data.

### Codecs (`Codec.*`)

| Codec            | Family          | State | Description                                                                     |
| ---------------- | --------------- | ----- | ------------------------------------------------------------------------------- |
| `Codec.Pcm`      | PCM             | R/W   | Raw integer PCM (8 / 16 / 24 / 32-bit), interleaved + planar, channel splitting |
| `Codec.ALaw`     | PCM (companded) | R/W   | ITU-T G.711 A-law — true encode + decode                                        |
| `Codec.MuLaw`    | PCM (companded) | R/W   | ITU-T G.711 mu-law — true encode + decode                                       |
| `Codec.Midi`     | Symbolic        | R/W   | SMF parsing + per-track re-emit (BuildSingleTrackFile)                          |
| `Codec.ImaAdpcm` | ADPCM           | R     | IMA / Intel ADPCM — decode only (no encoder)                                    |
| `Codec.MsAdpcm`  | ADPCM           | R     | Microsoft ADPCM — decode only (no encoder)                                      |
| `Codec.Gsm610`   | Speech          | R     | GSM 6.10 RPE-LTP — decode only (no encoder)                                     |
| `Codec.Mp3`      | Lossy           | R     | MPEG-1/2 Audio Layer III — decompress only (no encoder)                         |
| `Codec.Aac`      | Lossy           | R     | Advanced Audio Coding (ADTS) — decompress only                                  |
| `Codec.Vorbis`   | Lossy           | R     | Vorbis I — decompress only (no encoder)                                         |
| `Codec.Opus`     | Lossy           | R     | Opus (RFC 6716) — decompress only (no encoder)                                  |
| `Codec.Flac`     | Lossless        | R     | FLAC frame-level — decompress only (no encoder)                                 |

> **Honest scope note.** Most lossy / lossless codecs in this package decode but don't encode.
> Writing a high-quality MP3 / AAC / Vorbis / Opus / FLAC encoder is a significant undertaking
> beyond the workbench's "no native deps" line in the sand and is out of scope for now. A-law /
> mu-law / PCM are simple enough that we ship full encoders. If you need to *write* lossy audio,
> reach for a dedicated encoder library; if you only need to *read* the format, this package
> covers it.

### File-format containers (`FileFormat.*`)

| Container             | State | Description                                                         |
| --------------------- | ----- | ------------------------------------------------------------------- |
| `FileFormat.Wav`      | WORM  | RIFF WAV — INFO / LIST / bext metadata, multi-channel layout        |
| `FileFormat.Mp3`      | WORM  | MP3 — ID3v1/v2 tags + per-channel WAVs (decoded via `Codec.Mp3`); creates new MP3 streams |
| `FileFormat.Flac`     | WORM  | FLAC stream + metadata blocks (STREAMINFO, VORBIS_COMMENT, PICTURE) |
| `FileFormat.Akb`      | WORM  | Square Enix audio bank                                              |
| `FileFormat.Awb`      | WORM  | CRI Audio Wave Bank                                                 |
| `FileFormat.Psf`      | WORM  | PlayStation Sound Format (chiptune)                                 |
| `FileFormat.Aiff`     | WORM  | Apple AIFF / AIFC — big-endian PCM container; assembles a multi-channel AIFF from per-channel WAVs |
| `FileFormat.Au`       | WORM  | Sun / NeXT `.au` / `.snd` — 24-byte header + PCM; assembles from per-channel WAVs |
| `FileFormat.Caf`      | WORM  | Apple Core Audio Format — LPCM `desc`/`data` chunks, per-channel split + assemble |
| `FileFormat.Wave64`   | WORM  | Sony Wave64 (`.w64`) — GUID-keyed chunks, 64-bit sizes, per-channel split + assemble |
| `FileFormat.Rf64`     | WORM  | RF64 / BWF (Broadcast Wave) — `ds64` 64-bit sizes + `bext`, per-channel split + assemble |
| `FileFormat.Voc`      | WORM  | Creative Voice (`.voc`) — block-walking reader, per-channel split + assemble |
| `FileFormat.Alac`     | R     | Apple Lossless inside MP4 atoms                                     |
| `FileFormat.Ape`      | R     | Monkey's Audio (`.ape`) lossless                                    |
| `FileFormat.WavPack`  | R     | WavPack lossless / hybrid                                           |
| `FileFormat.Ogg`      | R     | OGG container — packet blobs + comments + per-channel WAVs (Vorbis/Opus decoded via `Codec.Vorbis`/`Codec.Opus`) |
| `FileFormat.Opus`     | R     | Opus (Ogg) — per-channel WAVs decoded via `Codec.Opus` (graceful fallback when unsupported) |
| `FileFormat.Aac`      | R     | AAC (ADTS) — per-channel WAVs decoded via `Codec.Aac` (AAC-LC; graceful fallback) |
| `FileFormat.Midi`     | R     | Standard MIDI File (SMF 0 / 1 / 2) container                        |
| `FileFormat.Mod`      | R     | ProTracker / SoundTracker MOD                                       |
| `FileFormat.S3m`      | R     | Scream Tracker 3                                                    |
| `FileFormat.Xm`       | R     | FastTracker II XM                                                   |
| `FileFormat.It`       | R     | Impulse Tracker IT                                                  |
| `FileFormat.WwiseBnk` | R     | Audiokinetic Wwise SoundBank (game audio)                           |
| `FileFormat.Fmod`     | R     | FMOD bank container                                                 |

## Codec implementation reference

Each codec has a `Decompress(Stream input, Stream output)` entry point producing interleaved
little-endian PCM and a `ReadStreamInfo` for metadata-only access. Encoders for the lossy /
lossless modern codecs are out of scope (see the scope note above) — only the legacy / simple
codecs ship with encoders.

| Codec                                                                                      | Project          | Encoder | Decoder state                                                                                                                                                                                                                                  | Reference                                                                                                  |
| ------------------------------------------------------------------------------------------ | ---------------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------- |
| [PCM](https://en.wikipedia.org/wiki/Pulse-code_modulation)                                 | `Codec.Pcm`      | Yes     | Production — raw integer PCM up to 32-bit                                                                                                                                                                                                      | —                                                                                                          |
| [FLAC](https://en.wikipedia.org/wiki/FLAC)                                                 | `Codec.Flac`     | -       | Production decode — FIXED + LPC subframes, all sample rates / bit depths                                                                                                                                                                       | [xiph.org/flac](https://xiph.org/flac/format.html)                                                         |
| [A-law](https://en.wikipedia.org/wiki/A-law_algorithm)                                     | `Codec.ALaw`     | Yes     | Production — G.711                                                                                                                                                                                                                              | [ITU-T G.711](https://www.itu.int/rec/T-REC-G.711)                                                         |
| [μ-law](https://en.wikipedia.org/wiki/%CE%9C-law_algorithm)                                | `Codec.MuLaw`    | Yes     | Production — G.711                                                                                                                                                                                                                              | [ITU-T G.711](https://www.itu.int/rec/T-REC-G.711)                                                         |
| [GSM 06.10](https://en.wikipedia.org/wiki/Full_Rate)                                       | `Codec.Gsm610`   | -       | Production decode — full RPE-LTP                                                                                                                                                                                                                | [ETSI GSM 06.10](https://www.etsi.org/deliver/etsi_gts/06/0610/03.02.00_60/gsmts_0610sv030200p.pdf)         |
| [IMA ADPCM](https://en.wikipedia.org/wiki/Interactive_Multimedia_Association)              | `Codec.ImaAdpcm` | -       | Production decode — Microsoft + Apple variants                                                                                                                                                                                                  | [IMA ADPCM spec](http://www.cs.columbia.edu/~hgs/audio/dvi/IMA_ADPCM.pdf)                                  |
| MS ADPCM                                                                                   | `Codec.MsAdpcm`  | -       | Production decode — WAV format 0x0002                                                                                                                                                                                                          | [MS ADPCM spec](https://wiki.multimedia.cx/index.php/Microsoft_ADPCM)                                      |
| [MIDI](https://en.wikipedia.org/wiki/MIDI)                                                 | `Codec.Midi`     | Yes     | Production — SMF 0/1/2 with all standard meta + channel events                                                                                                                                                                                  | [MIDI 1.0 spec](https://www.midi.org/specifications-old/item/the-midi-1-0-specification)                   |
| **[MP3](https://en.wikipedia.org/wiki/MP3)**                                               | `Codec.Mp3`      | -       | **Header + framing complete; bit-exact decode unverified.** minimp3 port (1469 LOC, scalar) covering MPEG-1/2/2.5 Layer III, MS+intensity stereo, ID3v2 skip, Xing VBR. Layer I/II rejection passes. End-to-end PCM decode against a reference clip is deferred until an MP3 test vector lands in `test-corpus/`. | [ISO/IEC 11172-3](https://www.iso.org/standard/22412.html) / [minimp3](https://github.com/lieff/minimp3)   |
| **[Vorbis](https://en.wikipedia.org/wiki/Vorbis)**                                         | `Codec.Vorbis`   | -       | **Partial.** stb_vorbis structural port (1295 LOC) covering Ogg page reassembly, codebooks (lookup 0/1/2), floor 1, residue 0/1/2, channel coupling, IMDCT. Floor 0 throws `NotSupportedException`. End-to-end test marked `Inconclusive` until a test vector lands in `test-corpus/`. | [Vorbis I spec](https://xiph.org/vorbis/doc/Vorbis_I_spec.html)                                            |
| **[Opus](https://en.wikipedia.org/wiki/Opus_(audio_format))**                              | `Codec.Opus`     | -       | **Framing only.** Ogg page walker + OpusHead/OpusTags + TOC byte + frame packing modes 0/1/2/3 + range decoder (`ec_dec`) all real. CELT and SILK pipelines are stubs that emit silence at the correct sample count. Hybrid mode throws `NotSupportedException`. | [RFC 6716](https://www.rfc-editor.org/rfc/rfc6716)                                                         |
| **[AAC-LC](https://en.wikipedia.org/wiki/Advanced_Audio_Coding)**                          | `Codec.Aac`      | -       | **Framing only.** ADTS frame parser + AudioSpecificConfig + element dispatcher + profile gating real. Spectral pipeline + Huffman tables + IMDCT + filterbank scaffolded but spectral data tables are TODO. HE-AAC v1/v2 + Main/SSR/LTP/ER all throw `NotSupportedException`. | [ISO/IEC 14496-3](https://www.iso.org/standard/76383.html)                                                 |

**Implementation philosophy.** The four modern lossy codecs (MP3 / Vorbis / Opus / AAC-LC)
ship under the project's "no toy implementations" rule — partial state is documented openly
(in class summaries, in `Assert.Ignore` messages, and in the table above) rather than silently
producing wrong PCM. Future work: bit-pack debugging for MP3, real CELT/SILK for Opus,
spectral table population for AAC, reference test-vector validation across all four.

## Versioning

This package version-locks 1:1 with `Hawkynt.Compression.Core`. Pin the same version across the
two packages — independent versioning would risk binary-incompatibility windows where a member
DLL bundled here was built against a different `Compression.Core` than the one a consumer
installs.

## License

LGPL-3.0-or-later. See the source repository for the full license text and per-algorithm
references.
