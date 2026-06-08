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
| `Codec.ImaAdpcm` | ADPCM           | R     | IMA / Intel ADPCM + QuickTime `ima4` packet variant — decode only               |
| `Codec.MsAdpcm`  | ADPCM           | R     | Microsoft ADPCM — decode only (no encoder)                                      |
| `Codec.Gsm610`   | Speech          | R     | GSM 6.10 RPE-LTP — decode only (raw 33-byte frames + WAV payloads)              |
| `Codec.Mp3`      | Lossy           | R     | MPEG-1/2 Audio Layer I / II / III — decompress only (no encoder)                |
| `Codec.Aac`      | Lossy           | R     | AAC-LC full decode + HE-AAC SBR parse/HF chain (QMF reconstruction gated)       |
| `Codec.Vorbis`   | Lossy           | R     | Vorbis I — decompress only (no encoder)                                         |
| `Codec.Opus`     | Lossy           | R     | Opus (RFC 6716) — decompress only (no encoder)                                  |
| `Codec.Flac`     | Lossless        | R     | FLAC frame-level — decompress only (no encoder)                                 |
| `Codec.OkiAdpcm` | ADPCM           | R/W   | OKI / Dialogic VOX 4-bit ADPCM — encode + decode                                |
| `Codec.SpuAdpcm` | ADPCM           | R/W   | Sony PS1/PS2 SPU ADPCM — encode + decode                                        |
| `Codec.DspAdpcm` | ADPCM           | R/W   | Nintendo GC/Wii DSP-ADPCM — decode + predictor-fit encoder                      |
| `Codec.G72x`     | ADPCM           | R/W   | ITU-T G.726 full rate set (16/24/32/40 kbit) — CCITT reference port, enc + dec  |
| `Codec.Tta`      | Lossless        | R/W   | True Audio TTA1 — reference-faithful decode + inverted encoder, byte-exact      |
| `Codec.Shorten`  | Lossless        | R/W   | Shorten v2 (SHN) — decode (incl. best-effort QLPC) + DIFF0-3 encoder            |
| `Codec.Alac`     | Lossless        | R/W   | Apple Lossless — decode + spec-shaped encoder (16/24-bit byte-exact)            |
| `Codec.WavPack`  | Lossless        | R/W   | WavPack v4/v5 lossless incl. IEEE-float (denormals/±0/NaN/Inf) — reference entropy |
| `Codec.CriAdx`   | ADPCM           | R/W   | CRI ADX (Sega) — highpass-derived coefficients, encode + decode                 |
| `Codec.Brr`      | ADPCM           | R/W   | SNES S-DSP BRR — filters 0-3, 15-bit hardware wrap, encode + decode             |
| `Codec.XaAdpcm`  | ADPCM           | R/W   | CD-ROM XA / PlayStation streaming ADPCM — sound groups, 4-bit enc+dec           |
| `Codec.EaXa`     | ADPCM           | R/W   | Electronic Arts EA-XA — coef/shift frames incl. raw-frame escape                |
| `Codec.WwiseIma` | ADPCM           | R/W   | Audiokinetic Wwise IMA (MS-IMA block layout)                                    |
| `Codec.AicaAdpcm`| ADPCM           | R/W   | Yamaha AICA / ADPCM-B (Dreamcast) — encode + decode                             |
| `Codec.G722`     | Speech          | R/W   | ITU-T G.722 sub-band ADPCM @64 kbit (QMF analysis/synthesis)                    |
| `Codec.Cvsd`     | Speech          | R/W   | CVSD delta modulation (Bluetooth SCO / MIL-STD style)                           |
| `Codec.Mace`     | Lossy           | R     | Apple MACE 3:1 / 6:1 — ffmpeg-faithful decode                                   |
| `Codec.MonkeysAudio` | Lossless    | R/W   | Monkey's Audio — reference-faithful, byte-exact vs ffmpeg; levels 1000-5000 dec |
| `Codec.Ra144`    | Speech          | R     | RealAudio 14.4 (lpcJ) — verbatim ffmpeg table port                              |
| `Codec.TrueSpeech` | Speech        | R     | DSP Group TrueSpeech (WAV tag 0x0022) — verbatim table port                     |
| `Codec.InterplayAcm` | Lossy       | R     | Interplay ACM (Fallout/BG) — verbatim filler/juggle port                        |
| `Codec.Nellymoser` | Lossy         | R     | Nellymoser (Flash) — verbatim tables, direct IMDCT                              |
| `Codec.WsAdpcm`  | ADPCM           | R     | Westwood WS ADPCM + shared continuous-IMA enc/dec                               |
| `Codec.RoqDpcm`  | DPCM            | R/W   | id RoQ square-table DPCM — encode + decode                                      |
| `Codec.SolDpcm`  | DPCM            | R     | Sierra SOL (old/new 8-bit tables + 16-bit integrate)                            |
| `Codec.Lpc10`    | Speech          | R/W   | FS-1015 LPC-10e 2400 bit/s vocoder (simplified pitch tracker, documented)       |
| `Codec.Cook`     | Lossy           | R     | RealAudio G2 "cook" (gain envelopes, MLT, Int0/Int4/genr deinterleave)          |
| `Codec.Atrac3`   | Lossy           | R     | Sony ATRAC3 (QMF tree, tonal components, joint stereo, RM descrambling)        |
| `Codec.Ac3`      | Lossy           | R     | ATSC A/52 AC-3 + E-AC-3 (AHT/GAQ, variable blocks, half rates; SPX parsed)      |
| `Codec.Wma`      | Lossy           | R     | WMA v1/v2 (LSP + VLC exponents, bit reservoir, noise coding)                    |
| `Codec.Musepack` | Lossy           | R     | Musepack SV7 + SV8 (mpc7/mpc8 huffman sets, shared 32-band polyphase)           |
| `Codec.WmaPro`   | Lossy           | R     | WMA 9 Professional (tile trees, 8-channel transforms, 24-bit, VBR)              |
| `Codec.Sipr`     | Speech          | R     | RealAudio ACELP.NET (8k5/6k5/5k0 + postfilter; 16k unsupported)                 |
| `Codec.Speex`    | Speech          | R     | Speex narrowband (all submodes) + wideband SB-CELP + intensity stereo           |
| `Codec.G7231`    | Speech          | R     | ITU G.723.1 dual-rate (MP-MLQ + ACELP, postfilter, CNG, concealment)            |
| `Codec.Dts`      | Lossy           | R     | DTS Coherent Acoustics core (ADPCM subbands, high-freq VQ, LFE FIR, 32-band QMF)|
| `Codec.Mos6502`  | CPU core        | —     | reusable NMOS 6502 (official + stable illegal opcodes, BCD, cycle counting)     |
| `Codec.Z80`      | CPU core        | —     | reusable full Z80 (CB/ED/DD/FD, block ops, IM 0/1/2)                            |
| `Codec.Sid`      | Synthesis       | R     | MOS 6581/8580/6582 (model-specific filters, reSID-style ADSR) + PSID player with 2SID/3SID bus routing |
| `Codec.Spc700`   | Synthesis       | R     | SNES SPC700 CPU + S-DSP (gaussian, ADSR/GAIN, pitch-mod, FIR echo)              |
| `Codec.Nes2a03`  | Synthesis       | R     | NES APU (duty/triangle/noise/DMC, nonlinear mixer) + NSF player                 |
| `Codec.GameBoyApu`| Synthesis      | R     | SM83 CPU + GB APU (sweep, wave RAM, 7/15-bit noise) + GBS player                |
| `Codec.Ay8910`   | Synthesis       | R     | AY-3-8910/YM2149 (10 envelope shapes, log DAC, ABC panning)                     |
| `Codec.Sn76489`  | Synthesis       | R     | SEGA PSG (tone/noise LFSR, 2 dB attenuation, GG stereo)                         |
| `Codec.Ym2612`   | Synthesis       | R     | OPN2 FM (genuine die log-sin/exp ROMs, 8 algorithms, DAC mode)                  |
| `Codec.Ym2413`   | Synthesis       | R     | OPLL FM (built-in 15-patch ROM + user patch, 9ch / 6+5 rhythm, KSL/KSR, AM/PM)  |
| `Codec.Tracker`  | Tracker         | R     | ProTracker MOD + Scream Tracker 3 S3M song playback (full effect sets, song-length traversal) |
| `Codec.TrackerXmIt`| Tracker       | R     | FastTracker II XM + Impulse Tracker IT playback (envelopes, NNA, resonant filter) + IT214/215 sample decompression |
| `Codec.AdpcmX`   | ADPCM           | R     | ffmpeg ADPCM/DPCM pile: IMA DK3/DK4/EACS/SEAD, EA R1-R3, THP/AFC, SWF, 4XM, Xan, Interplay, SDX2, DERF, Gremlin |
| `Codec.Atrac1`   | Lossy           | R     | Sony ATRAC1 / MiniDisc (BFU spectral decode, IMDCT, two-stage inverse QMF)      |
| `Codec.Ra288`    | Speech          | R     | RealAudio 28.8 (G.728 hybrid-window backward LPC)                               |
| `Codec.Ralf`     | Lossless        | R     | RealAudio Lossless (adaptive LPC + Golomb, 5 decorrelation modes)               |
| `Codec.CriHca`   | Lossy           | R     | CRI HCA (ATH, ciphers 0/1, HFR, 128-band IMDCT; keyed cipher unsupported)       |
| `Codec.Sbc`      | Lossy           | R     | Bluetooth SBC + mSBC (CRC-8 EBU, fixed-point 4/8-subband polyphase)             |
| `Codec.Siren`    | Lossy           | R     | Polycom Siren7 / ITU G.722.1 (region categorization, SQVH, IMLT); Annex C absent|
| `Codec.S302M`    | PCM (mapped)    | R/W   | SMPTE 302M AES3 subframes (bit-reversed 16/20/24-bit)                           |
| `Codec.BinkAudio`| Lossy           | R     | Bink audio, RDFT + DCT flavours (band RLE, overlap-add)                         |
| `Codec.SmackerAudio`| Lossy        | R     | Smacker SMKA (bitstream-built huffman trees, 8/16-bit delta)                    |
| `Codec.WmaLossless`| Lossless      | R     | WMA Lossless 0x0163 (CDLMS/MCLMS adaptive prediction, AC filter, rawpcm)        |
| `Codec.Xma`      | Lossy           | R     | XMA1/XMA2 packet/extradata layer over per-stream WMAPro (full decode gated)     |
| `Codec.Qoa`      | Lossy (DPCM)    | R/W   | Quite OK Audio (sign-LMS slices) — decoder + faithful encoder                   |
| `Codec.Dfpwm`    | Lossy (1-bit)   | R     | DFPWM1a (charge/strength integrator, anti-jerk, low-pass)                       |
| `Codec.Bonk`     | Lossless/lossy  | R     | Bonk (Rice intlist, lattice predictor, mid-side)                                |
| `Codec.WavArc`   | Lossless        | R     | WavArc .wa (0CPY/1DIF; adaptive-LPC block types gated)                          |

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
| `FileFormat.Voc`      | WORM  | Creative Voice (`.voc`) — block-walking reader incl. Creative 4-bit ADPCM, per-channel split + assemble |
| `FileFormat.Svx`      | WORM  | IFF/8SVX (Amiga) — Fibonacci-delta decode, planar stereo, IFF tags; assembles |
| `FileFormat.Avr`      | WORM  | AVR (Audio Visual Research, Atari ST) — BE PCM; assembles                   |
| `FileFormat.Sphere`   | WORM  | NIST SPHERE (`.sph`) — ulaw/alaw/PCM; shorten-embedded falls back; assembles |
| `FileFormat.Ircam`    | WORM  | IRCAM/BICSF (`.sf`) — byte order by magic, int + float channels; assembles  |
| `FileFormat.Vox`      | WORM  | Dialogic VOX — headerless OKI ADPCM @ 8 kHz; assembles from a mono WAV      |
| `FileFormat.Gsm`      | R     | Raw GSM 06.10 (`.gsm`) — 33-byte frames decoded to MONO.wav                 |
| `FileFormat.Dsf`      | WORM  | Sony DSD (`.dsf`) — per-channel raw DSD streams + decimated PCM WAVs; assembles bit-exact |
| `FileFormat.Dff`      | WORM  | Philips DSDIFF (`.dff`) — CHNL speaker IDs, DST falls back; assembles bit-exact |
| `FileFormat.Tta`      | WORM  | True Audio (`.tta`) — lossless per-channel split + assemble via `Codec.Tta` |
| `FileFormat.Shn`      | WORM  | Shorten (`.shn`) — lossless split + assemble via `Codec.Shorten`            |
| `FileFormat.Vag`      | WORM  | Sony VAG (`.vag`) — SPU-ADPCM decode; assembles from a mono WAV             |
| `FileFormat.Brstm`    | WORM  | Nintendo BRSTM — DSP-ADPCM/PCM channels with coef tables; assembles         |
| `FileFormat.Sf2`      | R     | SoundFont 2 — every sample as a mono WAV at its own rate + INFO tags        |
| `FileFormat.Dls`      | R     | Downloadable Sounds — `wvpl` waves rewrapped as standalone WAVs             |
| `FileFormat.G711`     | WORM  | Raw G.711 (`.al` / `.ul`) — headerless A-law/µ-law @ 8 kHz; assembles       |
| `FileFormat.Mpc`      | R     | Musepack SV7 ('MP+') AND SV8 ('MPCK') — decoded per-channel WAVs             |
| `FileFormat.Adx`      | WORM  | CRI ADX (Sega) — AHX (MPEG-2 LSF L2) decodes to MONO.wav; encrypted falls back; assembles |
| `FileFormat.Brr`      | WORM  | SNES BRR sample (loop-header tolerant); assembles                            |
| `FileFormat.Spc`      | R     | SNES SPC — full tune rendered to stereo 32 kHz WAV (SPC700+S-DSP) + every BRR instrument + ID666 |
| `FileFormat.Xa`       | WORM  | CD-XA / PlayStation streaming audio — RIFF/CDXA + raw sectors; assembles     |
| `FileFormat.Bcstm`    | WORM  | Nintendo 3DS stream (LE CSTM) over DSP-ADPCM; assembles                      |
| `FileFormat.Bfstm`    | WORM  | WiiU/Switch stream (BE/LE by BOM) over DSP-ADPCM; assembles                  |
| `FileFormat.Ast`      | WORM  | GameCube/Wii AST — PCM16BE exact; AFC decodes per-channel; assembles         |
| `FileFormat.Hps`      | WORM  | GameCube HALPST — linked DSP-ADPCM blocks; assembles                         |
| `FileFormat.Bwav`     | WORM  | Switch BWAV — DSP-ADPCM/PCM16 per-channel; assembles                         |
| `FileFormat.Swav`     | WORM  | Nintendo DS SWAV — PCM8/16 + IMA; assembles (PCM16)                          |
| `FileFormat.Sdat`     | R     | Nintendo DS sound archive — SWAV/SWAR decoded, SSEQ/SBNK/STRM surfaced       |
| `FileFormat.Xwb`      | R     | Microsoft XACT wave bank (v43+) — PCM + MS-ADPCM samples; XMA routed through `Codec.Xma` (graceful raw fallback) |
| `FileFormat.EaSchl`   | WORM  | EA SCHl streams — PT-table parse, EA-XA blocks; assembles                    |
| `FileFormat.Wem`      | R     | Audiokinetic Wwise media — Wwise-IMA/PCM decode; Wwise-Vorbis falls back     |
| `FileFormat.Aica`     | WORM  | Yamaha AICA raw (Dreamcast); assembles                                       |
| `FileFormat.Cvsd`     | WORM  | Raw CVSD bitstream @64 kHz; assembles                                        |
| `FileFormat.Maud`     | WORM  | Amiga IFF MAUD (PCM + A-law/µ-law); assembles                                |
| `FileFormat.Smp`      | WORM  | Turtle Beach SampleVision; assembles                                         |
| `FileFormat.Paf`      | WORM  | Ensoniq PARIS (BE/LE, 16/24-bit); assembles                                  |
| `FileFormat.Pvf`      | WORM  | mgetty Portable Voice Format (binary + ASCII); assembles                     |
| `FileFormat.MacSnd`   | R     | classic Mac 'snd ' resource — standard/extended/MACE headers                 |
| `FileFormat.Sndr`     | WORM  | PC Sounder; assembles                                                        |
| `FileFormat.Sndt`     | WORM  | SoundTool (`SOUND\x1A`); assembles                                          |
| `FileFormat.EspsSd`   | R     | Entropic ESPS `.sd` (record_freq generic, both endiannesses)                 |
| `FileFormat.Txw`      | WORM  | Yamaha TX16W (12-bit packed); assembles                                      |
| `FileFormat.Hcom`     | WORM  | Macintosh HCOM (Huffman-delta per sox); assembles                            |
| `FileFormat.Xi`       | R     | FastTracker II instrument — delta samples at per-note rates                  |
| `FileFormat.Sds`      | R     | MIDI Sample Dump Standard — septet-packed dumps                              |
| `FileFormat.Med`      | R     | OctaMED (MMD0/MMD1) sample archive                                           |
| `FileFormat.Okt`      | R     | Oktalyzer sample + pattern archive                                           |
| `FileFormat.Ult`      | R     | UltraTracker (V004) sample archive                                           |
| `FileFormat.F669`     | R     | Composer 669 sample archive                                                  |
| `FileFormat.Its`      | R     | Impulse Tracker sample (IT215-compressed falls back)                         |
| `FileFormat.Iti`      | R     | Impulse Tracker instrument (embedded IMPS samples)                           |
| `FileFormat.Mtm`      | R     | MultiTracker sample archive                                                  |
| `FileFormat.Far`      | R     | Farandole Composer sample archive                                            |
| `FileFormat.Stm`      | R     | Scream Tracker 2 sample archive                                              |
| `FileFormat.Ptm`      | R     | PolyTracker (8-bit delta) sample archive                                     |
| `FileFormat.Amf`      | R     | DSMI AMF (v10-14) sample archive                                             |
| `FileFormat.Psm`      | R     | Epic MASI PSM (DSMP delta) sample archive                                    |
| `FileFormat.Dsf`+`Dff`| WORM  | (see above) DSD pair                                                         |
| `FileFormat.Asf`      | R     | Microsoft ASF — depayloader; WMA v1/v2 + WMA Pro + WMA Lossless decode to channels; tags |
| `FileFormat.RealMedia`| R     | RealMedia — lpcJ, cook, atrc, sipr, 28_8 AND ralf decode to WAV channels     |
| `FileFormat.Acm`      | R     | Interplay ACM — decoded channels                                             |
| `FileFormat.Nelly`    | R     | raw Nellymoser block stream — decoded MONO                                   |
| `FileFormat.Aud`      | WORM  | Westwood AUD (C&C) — WS-ADPCM + IMA chunks; assembles                        |
| `FileFormat.Roq`      | WORM  | id RoQ — DPCM sound chunks (video chunks counted); assembles                 |
| `FileFormat.Sol`      | WORM  | Sierra SOL (3 magics, DPCM/PCM); assembles 16-bit                            |
| `FileFormat.Apc`      | R     | CRYO APC — seeded IMA                                                        |
| `FileFormat.Lpc10`    | WORM  | raw LPC-10e bitstream @8 kHz; assembles                                      |
| `FileFormat.G7231`    | R     | raw G.723.1 (.g723) — decoded MONO incl. SID/CNG frames                      |
| `FileFormat.Dts`      | R     | raw DTS — core decodes to per-channel WAVs; DTS-HD extensions info-only      |
| `FileFormat.Ac3`      | R     | raw AC-3 AND E-AC-3 independent substreams decode to per-channel WAVs        |
| `FileFormat.Oma`      | R     | Sony OpenMG (.oma/.aa3) — ATRAC3 decodes to channels; 3plus/MP3/LPCM surfaced |
| `FileFormat.Vgm`      | R     | VGM/VGZ — renders via SN76489+YM2612+YM2413(OPLL/SMS FM) when covered; GG stereo (0x4F); GD3 tags, gzip transparent |
| `FileFormat.Mus`      | R     | DMX/Doom MUS — converted.mid (classic mapping)                               |
| `FileFormat.Xmi`      | R     | Miles XMIDI — songs/NN.mid (duration-scheduled note-offs)                    |
| `FileFormat.Cmf`      | R     | Creative CMF — OPL patches + music.mid SMF wrap                              |
| `FileFormat.Nsf`      | R     | NES NSF/NSFE — renders to WAV via 6502+2A03 APU (incl. DMC); per-subtune lazy `TRACK_nn.wav` (NSFE plst/time/tlbl honored); expansion chips noted |
| `FileFormat.Gbs`      | R     | Game Boy GBS — renders to stereo WAV via SM83+APU (timer rates, banking); per-subtune lazy `TRACK_nn_LEFT/RIGHT.wav` |
| `FileFormat.Sid`      | R     | C64 PSID — renders via 6502+SID: per-chip 6581/8580/6582 model flags, 2SID/3SID → LEFT/RIGHT/CENTER, unknown model renders both `_6581`/`_8580` legs; per-subtune lazy `TRACK_nn.wav`; RSID surfaced |
| `FileFormat.Kss`      | R     | MSX KSS — renders PSG to WAV via Z80+AY; per-subtune lazy tracks for KSSX (plain KSCC has no track list); SCC/FMPAC noted |
| `FileFormat.Hes`      | R     | PC Engine HES — MPR table + DATA blocks                                      |
| `FileFormat.Gym`      | R     | Genesis GYM — renders via YM2612+PSG (zlib-packed logs supported)            |
| `FileFormat.Ay`       | R     | ZX Spectrum AY — renders to WAV via Z80+AY-3-8910; per-subtune lazy `TRACK_nn_LEFT/RIGHT.wav` |
| `FileFormat.Alac`     | R     | Apple Lossless inside MP4 atoms — decoded per-channel WAVs via `Codec.Alac` |
| `FileFormat.Ape`      | R     | Monkey's Audio (`.ape`) — decoded channels, all levels 1000-5000             |
| `FileFormat.WavPack`  | R     | WavPack lossless / hybrid                                           |
| `FileFormat.Ogg`      | R     | OGG container — packet blobs + comments + per-channel WAVs (Vorbis/Opus decoded via `Codec.Vorbis`/`Codec.Opus`) |
| `FileFormat.Opus`     | R     | Opus (Ogg) — per-channel WAVs decoded via `Codec.Opus` (graceful fallback when unsupported) |
| `FileFormat.Aac`      | R     | AAC (ADTS) — per-channel WAVs decoded via `Codec.Aac` (AAC-LC; graceful fallback) |
| `FileFormat.Midi`     | R     | Standard MIDI File (SMF 0 / 1 / 2) container                        |
| `FileFormat.Mod`      | R     | ProTracker / SoundTracker MOD — renders `SONG.wav` (+ `SONG_LEFT/RIGHT.wav` channels) via `Codec.Tracker`; samples as mono WAVs |
| `FileFormat.S3m`      | R     | Scream Tracker 3 — renders `SONG.wav` (+ per-channel WAVs) via `Codec.Tracker`; samples as mono WAVs |
| `FileFormat.Xm`       | R     | FastTracker II XM — renders `SONG.wav` (+ per-channel WAVs) via `Codec.TrackerXmIt`; samples as mono WAVs |
| `FileFormat.It`       | R     | Impulse Tracker IT — renders `SONG.wav` (+ per-channel WAVs) via `Codec.TrackerXmIt`; IT214/215 samples decompressed to WAVs |
| `FileFormat.WwiseBnk` | R     | Audiokinetic Wwise SoundBank (game audio)                           |
| `FileFormat.Fmod`     | R     | FMOD bank container                                                 |
| `FileFormat.Aea`      | R     | Sony MiniDisc AEA — ATRAC1 decoded to per-channel WAVs              |
| `FileFormat.Hca`      | R     | CRI HCA — decoded channels (keyed cipher / MS-stereo degrade to FULL + note) |
| `FileFormat.Sbc`      | R     | Raw Bluetooth SBC / mSBC stream — per-channel WAVs                  |
| `FileFormat.Siren`    | R     | Raw Siren7 / G.722.1 (`.sir`/`.g7221`) — MONO.wav (Annex C noted)   |
| `FileFormat.Bik`      | R     | Bink video — FULL.bik + raw VIDEO track + per-track decoded channel WAVs (Bink2 audio blob-only) |
| `FileFormat.Smk`      | R     | Smacker video — FULL.smk + raw VIDEO track + per-track channel WAVs (SMKA + uncompressed PCM) |
| `FileFormat.Mp4`      | R     | MP4/MOV — each audio track decoded to `TRACKn_<CHANNEL>.wav` (AAC/ALAC/MP3/AC-3/FLAC/Opus/PCM); video stays raw |
| `FileFormat.Matroska` | R     | Matroska/WebM — per-track decode (AAC/MP3/AC-3/Vorbis/Opus/FLAC/PCM/MS-ACM); Xiph/EBML/fixed block-lacing |
| `FileFormat.Avi`      | R     | AVI — per `auds` stream decoded (PCM/MS-ADPCM/IMA/MP2-3/AC-3/A-law/µ-law/EXTENSIBLE) |
| `FileFormat.Qoa`      | R     | Quite OK Audio (`.qoa`) — decoded channels                          |
| `FileFormat.Dfpwm`    | R     | DFPWM1a (`.dfpwm`, headerless, 48 kHz mono convention) — MONO.wav   |
| `FileFormat.Bonk`     | R     | Bonk (`.bonk`, tag-scanned header) — decoded channels               |
| `FileFormat.WavArc`   | R     | WavArc (`.wa`) — 0CPY/1DIF decode to channels; adaptive-LPC types FULL-only |

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
| **[Vorbis](https://en.wikipedia.org/wiki/Vorbis)**                                         | `Codec.Vorbis`   | R       | **Decode complete.** stb_vorbis structural port covering Ogg page reassembly, codebooks (lookup 0/1/2), floor 0 (LSP) and floor 1, residue 0/1/2, channel coupling, IMDCT. End-to-end coverage via hand-assembled synthetic streams (both floor types); the real-corpus end-to-end test stays `Inconclusive` until a sample lands in `test-corpus/`. | [Vorbis I spec](https://xiph.org/vorbis/doc/Vorbis_I_spec.html)                                            |
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
