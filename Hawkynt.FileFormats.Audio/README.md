# Hawkynt.FileFormats.Audio

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.FileFormats.Audio.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.Audio/)
[![NuGet downloads](https://img.shields.io/nuget/dt/Hawkynt.FileFormats.Audio.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.Audio/)
[![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE)
[![CI](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml)
![Target](https://img.shields.io/badge/target-net10.0-blue)

> Pure-managed audio handling for .NET, on top of `Hawkynt.Compression.Core`. The package claims the
> WHOLE domain — every codec, container, tracker/chiptune format and game-audio format — not a
> selection of it. Where a format is missing or only partly supported that is a tracked gap, and the
> support matrix below is the single place it is tracked.

## 📦 Installation

```bash
dotnet add package Hawkynt.FileFormats.Audio
```

The package bundles the audio-domain `Codec.*` and `FileFormat.*` assemblies while taking `Hawkynt.Compression.Core` as the shared NuGet dependency.

## ✨ Features

- PCM, companded PCM, ADPCM, speech, lossless, and lossy codec implementations in managed C#.
- Common audio containers including WAV, AIFF, AU, FLAC, Ogg, MP3, MIDI, and WavPack.
- Tracker/module and chiptune support, including MOD/S3M/XM/IT and PSF-family workflows.
- Game-audio container support such as Wwise, FMOD, AWB, and AKB families.
- Pseudo-archive view of carried data: streams, tracks, channels, tags, samples, patterns, and instruments can be surfaced as independently addressable entries where the format permits it.
- Multi-channel PCM helpers with speaker-aware channel splitting and re-interleaving.

## 🧩 Support matrix

This is the whole support ledger for the audio domain — every codec, container, tracker/chiptune
and game-audio format the package bundles, with what it can actually do. There is no second copy
of it anywhere in the repository; `Compression.Tests/Documentation/AudioReadmeStateTests.cs`
re-derives the container rows from the built registry, so a capability cannot be claimed here
without the code backing it.

| State | Meaning |
| --- | --- |
| **R** | Read/decode only. |
| **WORM** | Read plus create a fresh container/output; no in-place mutation. |
| **R/W** | Encode/decode, or read/write container semantics. |
| **⚠️** | Deliberate subset; the row says which. |
| ✅ / — | Capability present / absent, re-derived from the registry. |

### Codec support

One row per bundled `Codec.*` assembly. **Historical identifiers** lists the WAVE/ACM format
tags and other registrations that name this codec; where a tag is recognised but not yet wired
to the WAVE reader the note says so rather than leaving it implied.

| Id | Family | State | Historical identifiers | Notes |
| --- | --- | :---: | --- | --- |
| `Codec.Pcm` | PCM | R/W | `0x0001`, `0x0003` | Raw integer PCM (8/16/24/32-bit), interleaved + planar, channel splitting |
| `Codec.ALaw` | PCM (companded) | R/W | `0x0006`, `0x0102` | ITU-T G.711 A-law encode + decode; IBM alias `0x0102` is not routed |
| `Codec.MuLaw` | PCM (companded) | R/W | `0x0007` | ITU-T G.711 μ-law encode + decode |
| `Codec.Midi` | Symbolic | R/W | — | SMF parsing + per-track re-emit (`BuildSingleTrackFile`) |
| `Codec.ImaAdpcm` | ADPCM | R/W | `0x0011`, `0x0039` | IMA / Intel ADPCM + QuickTime `ima4` packet variant; vendor aliases beyond `0x0011` are not routed |
| `Codec.MsAdpcm` | ADPCM | R/W | `0x0002` | Microsoft ADPCM encode + decode |
| `Codec.Gsm610` | Speech | R/W | `0x0031`, `0x0086`, `0x00A1`, `0x0155` | GSM 06.10 RPE-LTP, bit-exact with the ETSI reference in both directions; vendor aliases beyond `0x0031` are not routed |
| `Codec.Mp3` | Lossy | R/W ⚠️ | `0x0050`, `0x0055`, `0x0700` | MPEG Audio decode plus an encoder; see implementation scope below |
| `Codec.Aac` | Lossy | R/W ⚠️ | `0x00B0`, `0x00FF`, `0x0180`, `0x0AAC`, `0x4143`, `0x706D`, `0xA106`, `0x2006`, `0x2007` | AAC-LC path with advanced-profile limits; only the ADTS/ADIF entry points are routed |
| `Codec.Vorbis` | Lossy | R/W | `0x564C`, `0x674F`, `0x6750`, `0x6751`, `0x676F`, `0x6770`, `0x6771` | Vorbis I encode + decode; the legacy ACM framings are not routed |
| `Codec.Opus` | Lossy | R/W ⚠️ | — | Opus path with documented CELT/SILK/hybrid limits |
| `Codec.Flac` | Lossless | R/W | `0xF1AC` | FLAC frame-level encode + decode; the WAVE tag is not routed; native `.flac` is |
| `Codec.OkiAdpcm` | ADPCM | R/W | `0x0010`, `0x0017` | OKI / Dialogic VOX 4-bit ADPCM |
| `Codec.SpuAdpcm` | ADPCM | R/W | — | Sony PS1/PS2 SPU ADPCM |
| `Codec.DspAdpcm` | ADPCM | R/W | — | Nintendo GC/Wii DSP-ADPCM + predictor-fit encoder |
| `Codec.G72x` | ADPCM | R/W | `0x0040`, `0x0045`, `0x0064`, `0x0085`, `0x008B`, `0x0140`, `0x4243`, `0xA105`, `0xA107` | ITU-T G.726 16/24/32/40 kbit encode + decode |
| `Codec.Tta` | Lossless | R/W | — | True Audio TTA1 encode + decode |
| `Codec.Shorten` | Lossless | R/W | — | Shorten v2 decode including best-effort QLPC + DIFF0-3 encoder |
| `Codec.Alac` | Lossless | R/W | — | Apple Lossless 16/24-bit paths |
| `Codec.WavPack` | Lossless | R/W | `0x5756` | WavPack v4/v5 lossless including IEEE-float handling; the WAVE tag is not routed; native `.wv` is |
| `Codec.CriAdx` | ADPCM | R/W | — | CRI ADX high-pass-derived coefficients |
| `Codec.Brr` | ADPCM | R/W | — | SNES S-DSP BRR filters 0-3 and hardware-style wrap |
| `Codec.XaAdpcm` | ADPCM | R/W | — | CD-ROM XA / PlayStation streaming ADPCM |
| `Codec.EaXa` | ADPCM | R/W | — | Electronic Arts EA-XA coefficient/shift frames |
| `Codec.WwiseIma` | ADPCM | R/W | — | Audiokinetic Wwise IMA / MS-IMA block layout |
| `Codec.AicaAdpcm` | ADPCM | R/W | — | Yamaha AICA / ADPCM-B (Dreamcast) |
| `Codec.G722` | Speech | R/W | `0x0065`, `0x028F` | ITU-T G.722 sub-band ADPCM @ 64 kbit |
| `Codec.Cvsd` | Speech | R/W | `0x0005` | CVSD delta modulation |
| `Codec.Mace` | Lossy | R | QuickTime `MAC3`, `MAC6` | Apple MACE 3:1 / 6:1 decode |
| `Codec.MonkeysAudio` | Lossless | R/W ⚠️ | — | Monkey's Audio supported compression levels |
| `Codec.Ra144` | Speech | R | `0x2002` | RealAudio 14.4 / lpcJ |
| `Codec.TrueSpeech` | Speech | R | `0x0022` | DSP Group TrueSpeech WAV tag 0x0022 |
| `Codec.InterplayAcm` | Lossy | R | — | Interplay ACM |
| `Codec.Nellymoser` | Lossy | R | — | Nellymoser / Flash |
| `Codec.WsAdpcm` | ADPCM | R/W | — | Westwood WS ADPCM + continuous-IMA paths, encode + decode |
| `Codec.RoqDpcm` | DPCM | R/W | — | id RoQ square-table DPCM |
| `Codec.SolDpcm` | DPCM | R | — | Sierra SOL old/new DPCM tables |
| `Codec.Lpc10` | Speech | R/W ⚠️ | — | FS-1015 LPC-10e 2400 bit/s, simplified pitch tracker documented in source |
| `Codec.Cook` | Lossy | R | `0x2004` | RealAudio G2 `cook` |
| `Codec.Atrac3` | Lossy | R | `0x0272` | Sony ATRAC3 |
| `Codec.Ac3` | Lossy | R/W ⚠️ | `0x0092`, `0x0241`, `0x2000` | AC-3 / E-AC-3 encode + decode with advanced-feature boundaries; S/PDIF-encapsulated variants are not routed |
| `Codec.Wma` | Lossy | R | `0x0160`, `0x0161` | WMA v1/v2; ASF carriage is read through `FileFormat.Asf` |
| `Codec.Musepack` | Lossy | R | — | Musepack SV7/SV8 |
| `Codec.WmaPro` | Lossy | R | `0x0162`, `0x0164` | WMA 9 Professional; the S/PDIF registration `0x0164` stays a separate profile |
| `Codec.Sipr` | Speech | R ⚠️ | `0x0130`-`0x0135` | RealAudio ACELP.NET; unsupported mode boundaries documented in source |
| `Codec.Speex` | Speech | R | `0xA109` | Speex narrowband + wideband paths; the ACM tag is not routed |
| `Codec.G7231` | Speech | R | `0x0059`, `0x0093`, `0x00A3`, `0x0123`, `0x1C0C`, `0xA100` | ITU G.723.1 dual-rate; historical WAVE aliases are not routed |
| `Codec.Dts` | Lossy | R/W | `0x0008`, `0x0190`, `0x2001` | DTS Coherent Acoustics core encode + decode; wrapper tags are not routed |
| `Codec.Mos6502` | CPU core | — | — | Reusable NMOS 6502 including stable illegal opcodes, BCD and cycle counting |
| `Codec.Z80` | CPU core | — | — | Z80 core with CB/ED/DD/FD, block ops and interrupt modes |
| `Codec.Sid` | Synthesis | R | — | MOS 6581/8580/6582 + PSID player with multi-SID routing |
| `Codec.Spc700` | Synthesis | R | — | SNES SPC700 CPU + S-DSP |
| `Codec.Nes2a03` | Synthesis | R | — | NES APU + NSF expansion audio |
| `Codec.GameBoyApu` | Synthesis | R | — | SM83 CPU + Game Boy APU + GBS player |
| `Codec.Ay8910` | Synthesis | R | — | AY-3-8910/YM2149 |
| `Codec.Sn76489` | Synthesis | R | — | SEGA PSG |
| `Codec.Ym2612` | Synthesis | R | — | OPN2 FM |
| `Codec.Ym2413` | Synthesis | R | — | OPLL FM |
| `Codec.Ym2151` | Synthesis | R | — | OPM FM |
| `Codec.Ym2203` | Synthesis | R | — | OPN FM + SSG |
| `Codec.Ym2608` | Synthesis | R ⚠️ | — | OPNA FM + SSG; rhythm/ADPCM-B boundaries documented in source |
| `Codec.Opl` | Synthesis | R ⚠️ | — | OPL/OPL2/OPL3/Y8950 family; Y8950 ADPCM boundary documented in source |
| `Codec.HuC6280` | CPU + synthesis | R | — | PC Engine HuC6280 + 6-channel wavetable PSG |
| `Codec.AmrNb` | Speech | R/W | `0x0136`, `0x4201`, `0x7A21`, `0x7A22` | 3GPP AMR narrowband, all 8 modes; the WAVE aliases are not routed; native `.amr` is |
| `Codec.AmrWb` | Speech | R/W | `0xA104` | 3GPP AMR wideband / G.722.2, all 9 modes; the WAVE alias is not routed; native `.awb` is |
| `Codec.Tracker` | Tracker | R | — | ProTracker MOD + Scream Tracker 3 S3M playback |
| `Codec.TrackerXmIt` | Tracker | R | — | FastTracker II XM + Impulse Tracker IT playback, IT214/215 samples |
| `Codec.AdpcmX` | ADPCM | R | — | IMA DK3/DK4/EACS/SEAD, EA R1-R3, THP/AFC, SWF, 4XM, Xan, Interplay, SDX2, DERF, Gremlin |
| `Codec.Atrac1` | Lossy | R | `0x0270` | Sony ATRAC1 / MiniDisc |
| `Codec.Ra288` | Speech | R | `0x2003` | RealAudio 28.8 / G.728-style path |
| `Codec.Ralf` | Lossless | R | — | RealAudio Lossless |
| `Codec.CriHca` | Lossy | R ⚠️ | — | CRI HCA; keyed-cipher limitations documented in source |
| `Codec.Sbc` | Lossy | R | — | Bluetooth SBC + mSBC |
| `Codec.Siren` | Lossy | R ⚠️ | — | Siren7 / G.722.1; Annex C boundary documented in source |
| `Codec.S302M` | PCM (mapped) | R/W | — | SMPTE 302M AES3 subframes |
| `Codec.BinkAudio` | Lossy | R | — | Bink audio RDFT + DCT flavours |
| `Codec.SmackerAudio` | Lossy | R | — | Smacker SMKA |
| `Codec.WmaLossless` | Lossless | R | `0x0163` | WMA Lossless 0x0163 |
| `Codec.Xma` | Lossy | R ⚠️ | — | XMA1/XMA2 packet/extradata layer over WMAPro; full-decode boundaries documented in source |
| `Codec.Qoa` | Lossy (DPCM) | R/W | — | Quite OK Audio sign-LMS slices |
| `Codec.Dfpwm` | Lossy (1-bit) | R/W | — | DFPWM1a encode + decode |
| `Codec.Bonk` | Lossless/lossy | R/W | — | Bonk encode + decode |
| `Codec.WavArc` | Lossless | R ⚠️ | — | WavArc `.wa`; adaptive-LPC block boundaries documented in source |

Absence of an encoder is not hidden behind the word "support": a codec is `R` unless it
actually has an encode path.

### Container and music-format support

One row per registered audio-domain format. **State**, **Decode**, **Encode**, **Demux** and
**Mux** are re-derived from `FormatRegistry` and `AudioConversionInventory`, so they describe
the live code. *Decode*/*Encode* mean PCM in and out of the container; *Demux*/*Mux* mean
carrying an already-coded stream without re-encoding it. A format with none of the four is
still readable as a pseudo-archive — see the carried-data model below.

| Id | Format | State | Decode | Encode | Demux | Mux | Notes |
| --- | --- | :---: | :---: | :---: | :---: | :---: | --- |
| `Aac` | AAC (ADTS) | WORM | ✅ | ✅ | ✅ | ✅ | AAC ADTS route; ADTS remux in both directions; codec profile limits apply |
| `Ac3` | AC-3 / E-AC-3 | R | ✅ | ✅ | — | — | Raw AC-3/E-AC-3 independent substreams |
| `Acm` | Interplay ACM (Fallout / Baldur's Gate audio) | R | — | — | — | — | Interplay ACM |
| `Adx` | CRI ADX | WORM | — | — | — | — | CRI ADX/AHX paths |
| `Aea` | Sony ATRAC1 / MiniDisc (.aea) | R | — | — | — | — | Sony MiniDisc AEA / ATRAC1 |
| `Ahx` | AHX / THX Synth-Tracker | R | — | — | — | — | AHX / THX Synth-Tracker |
| `Aica` | Yamaha AICA ADPCM (Dreamcast) | WORM | — | — | — | — | Yamaha AICA raw |
| `Aiff` | AIFF / AIFC (Apple audio) | WORM | ✅ | ✅ | ✅ | ✅ | AIFF/AIFC; multi-channel assembly from channel WAVs |
| `Akb` | Square Enix AKB | WORM | — | — | — | — | Square Enix audio bank |
| `Alac` | ALAC (Apple Lossless) | R | — | — | — | — | Apple Lossless inside MP4 atoms |
| `Amf` | AMF (DSMI Advanced Module Format) | R | — | — | — | — | DSMI AMF sample archive |
| `Amr` | 3GPP AMR | R | — | — | — | — | 3GPP AMR NB/WB, including MC1.0 multichannel surface |
| `AmrNb` | AMR-NB | R | ✅ | ✅ | ✅ | ✅ | AMR-NB |
| `AmrWb` | AMR-WB | R | ✅ | ✅ | ✅ | ✅ | AMR-WB |
| `Apc` | CRYO APC | R | — | — | — | — | CRYO APC seeded IMA |
| `Ape` | Monkey's Audio (.ape) | R | — | — | — | — | Monkey's Audio container |
| `Asf` | ASF (Advanced Systems Format) | R | — | — | — | — | Microsoft ASF, WMA-family audio depayload/decode routes + tags |
| `Ast` | AST (GameCube/Wii stream) | WORM | — | — | — | — | GameCube/Wii AST |
| `Au` | Sun/NeXT .au (.snd) | WORM | ✅ | ✅ | ✅ | ✅ | Sun/NeXT `.au` / `.snd` |
| `Aud` | Westwood AUD (Command & Conquer) | WORM | — | — | — | — | Westwood AUD / WS-ADPCM + IMA |
| `Avi` | AVI (RIFF video) | R | — | — | — | — | AVI `auds` stream routing including PCM/ADPCM/MPEG Audio/AC-3/G.711 variants |
| `Avr` | AVR (Audio Visual Research) | WORM | — | — | — | — | AVR / Atari ST big-endian PCM |
| `Awb` | CRI Audio Wave Bank | WORM | — | — | — | — | CRI Audio Wave Bank |
| `Ay` | ZX Spectrum AY | R | — | — | — | — | ZX Spectrum AY via Z80+AY-3-8910 |
| `Bcstm` | BCSTM (3DS stream) | WORM | — | — | — | — | Nintendo 3DS CSTM over DSP-ADPCM |
| `Bfstm` | BFSTM (WiiU/Switch stream) | WORM | — | — | — | — | Wii U/Switch FSTM over DSP-ADPCM |
| `Bik` | Bink Video | R | — | — | — | — | Bink container: raw video + decoded supported audio tracks |
| `Bonk` | Bonk Audio | WORM | — | — | — | — | Bonk container |
| `Brr` | SNES BRR sample | WORM | — | — | — | — | SNES BRR sample |
| `Brstm` | BRSTM (Wii stream) | WORM | — | — | — | — | Nintendo BRSTM, DSP-ADPCM/PCM channels |
| `Bwav` | BWAV (Switch stream) | WORM | — | — | — | — | Switch BWAV |
| `Caf` | CAF (Core Audio Format) | WORM | ✅ | ✅ | ✅ | ✅ | Apple Core Audio Format, LPCM `desc`/`data` chunks |
| `Cmf` | Creative Music File (OPL) | R | — | — | — | — | Creative CMF OPL patches + MIDI surface |
| `Cvsd` | Raw CVSD | WORM | — | — | — | — | Raw CVSD bitstream |
| `Dbm` | DBM (DigiBooster Pro) | R | — | — | — | — | DBM (DigiBooster Pro) |
| `Dff` | DSDIFF (.dff) | WORM | — | — | — | — | Philips DSDIFF, CHNL speaker IDs; DST boundary documented in source |
| `Dfpwm` | DFPWM1a (ComputerCraft) | WORM | — | — | — | — | Headerless DFPWM1a convention |
| `Dls` | DLS (Downloadable Sounds) | R | — | — | — | — | Downloadable Sounds, `wvpl` waves |
| `Dsf` | DSF (DSD Stream File) | WORM | — | — | — | — | Sony DSD, per-channel raw DSD + decimated PCM views |
| `Dts` | DTS (Coherent Acoustics) | R | ✅ | ✅ | — | — | Raw DTS core; HD extensions are not silently treated as core support |
| `EaSchl` | Electronic Arts SCHl Stream | WORM | — | — | — | — | EA SCHl streams |
| `EspsSd` | ESPS sampled data (.sd) | R | — | — | — | — | Entropic ESPS `.sd` |
| `F669` | Composer 669 | R | — | — | — | — | Composer 669 sample archive |
| `Far` | FAR (Farandole Composer) | R | — | — | — | — | Farandole Composer sample archive |
| `Flac` | FLAC | WORM | ✅ | ✅ | — | — | FLAC stream plus STREAMINFO/VORBIS_COMMENT/PICTURE metadata |
| `FlacArchive` | FLAC (archive view) | R | — | — | — | — | FLAC seen as a pseudo-archive of its metadata blocks and frames |
| `Fmod` | FMOD Sample Bank | R | — | — | — | — | FMOD bank container |
| `G711Alaw` | Raw A-law (G.711) | WORM | — | — | — | — | Raw G.711 A-law packets |
| `G711Ulaw` | Raw µ-law (G.711) | WORM | — | — | — | — | Raw G.711 μ-law packets |
| `G7231` | ITU-T G.723.1 | R | — | — | — | — | Raw G.723.1 including SID/CNG paths |
| `Gbs` | Game Boy Sound | R | — | — | — | — | Game Boy GBS render via SM83+APU |
| `GsmRaw` | Raw GSM 06.10 (.gsm) | WORM | — | — | — | — | Raw GSM 06.10 frames |
| `Gym` | Genesis GYM log | R | — | — | — | — | Genesis GYM via YM2612+PSG |
| `Hca` | CRI HCA | R | — | — | — | — | CRI HCA; keyed-cipher/MS-stereo boundaries documented in source |
| `Hcom` | HCOM (Macintosh) | WORM | — | — | — | — | Macintosh HCOM Huffman-delta |
| `Hes` | PC Engine HES | R | — | — | — | — | PC Engine HES render via HuC6280+PSG |
| `Hps` | HPS (GameCube HALPST stream) | WORM | — | — | — | — | GameCube HALPST linked DSP-ADPCM blocks |
| `Ircam` | IRCAM / BICSF (.sf) | WORM | — | — | — | — | IRCAM/BICSF integer + float channels |
| `It` | IT (Impulse Tracker) | R | — | — | — | — | Impulse Tracker IT render + IT214/215 samples |
| `Iti` | Impulse Tracker Instrument | R | — | — | — | — | Impulse Tracker instrument |
| `Its` | Impulse Tracker Sample | R | — | — | — | — | Impulse Tracker sample; compression boundaries documented in source |
| `Kss` | KSS (MSX/SMS music) | R | — | — | — | — | MSX KSS render via Z80+AY; extension-chip limits documented in source |
| `Lpc10` | FS-1015 LPC-10 | WORM | — | — | — | — | Raw LPC-10e @ 8 kHz |
| `MacSnd` | Mac 'snd ' resource | R | — | — | — | — | Classic Mac `snd ` resource |
| `Maud` | IFF/MAUD (MacroSystem) | WORM | — | — | — | — | Amiga IFF MAUD |
| `Med` | OctaMED (MMD0 / MMD1) | R | — | — | — | — | OctaMED sample archive |
| `Midi` | MIDI (Standard MIDI File) | WORM | — | — | — | — | SMF 0/1/2 container |
| `Mkv` | MKV / WebM (demuxed) | R | — | — | — | — | Matroska/WebM audio-track routing, attachments/lacing as implemented |
| `Mod` | MOD (ProTracker / SoundTracker) | R | — | — | — | — | ProTracker/SoundTracker MOD render + samples |
| `Mp3` | MP3 (MPEG audio) | WORM | ✅ | ✅ | ✅ | ✅ | MP3 — ID3v1/v2 tags + decoded channels; fresh MP3 stream construction surface |
| `Mp4` | MP4 / MOV (demuxed) | WORM | — | ✅ | — | ✅ | MP4/MOV audio tracks routed to AAC/ALAC/MP3/AC-3/FLAC/Opus/PCM; video stays a carried track |
| `Mpc` | Musepack (MPC) | R | — | — | — | — | Musepack SV7/SV8 |
| `Mtm` | MTM (MultiTracker) | R | — | — | — | — | MultiTracker sample archive |
| `Mus` | DMX/Doom MUS | R | — | — | — | — | Doom MUS converted to MIDI |
| `Nelly` | Nellymoser Asao (Flash audio stream) | R | — | — | — | — | Raw Nellymoser block stream |
| `Nsf` | NES Sound Format | R | — | — | — | — | NES NSF/NSFE render via 6502+2A03 plus supported expansions |
| `Ogg` | OGG (Xiph container) | WORM | ✅ | ✅ | — | — | Ogg packet blobs, comments, Vorbis/Opus routes |
| `Okt` | Oktalyzer | R | — | — | — | — | Oktalyzer sample/pattern archive |
| `Oma` | Sony OpenMG (OMA/AA3) | R | — | — | — | — | Sony OpenMG / ATRAC3 route with non-ATRAC payloads surfaced according to type |
| `Opus` | Opus (Ogg) | R | — | — | — | — | Opus-in-Ogg route; codec limits apply |
| `Paf` | PARIS Audio File (Ensoniq) | WORM | — | — | — | — | Ensoniq PARIS |
| `Psf` | Portable Sound Format | WORM | — | — | — | — | PlayStation Sound Format family container |
| `Psm` | PSM (Epic MegaGames MASI) | R | — | — | — | — | Epic MASI PSM sample archive |
| `Ptm` | PTM (PolyTracker) | R | — | — | — | — | PolyTracker sample archive |
| `Pvf` | Portable Voice Format (mgetty) | WORM | — | — | — | — | Portable Voice Format binary + ASCII |
| `Qoa` | Quite OK Audio (QOA) | WORM | — | — | — | — | Quite OK Audio container |
| `RealMedia` | RealMedia / RealAudio | R | — | — | — | — | RealMedia, codec-routed audio channels |
| `Rf64` | RF64 / BWF (Broadcast Wave) | WORM | — | — | — | — | RF64/BWF, `ds64` sizes + `bext` |
| `Roq` | id Software RoQ | WORM | — | — | — | — | id RoQ DPCM sound chunks; video chunks counted/surfaced separately |
| `S3m` | S3M (Scream Tracker 3) | R | — | — | — | — | Scream Tracker 3 render + samples |
| `Sbc` | Bluetooth SBC / mSBC | R | — | — | — | — | Raw Bluetooth SBC/mSBC |
| `Sdat` | SDAT (Nintendo DS sound archive) | R | — | — | — | — | Nintendo DS sound archive; SWAV/SWAR plus SSEQ/SBNK/STRM surfaces |
| `Sds` | SDS (MIDI Sample Dump) | R | — | — | — | — | MIDI Sample Dump Standard |
| `Sf2` | SoundFont 2 | R | — | — | — | — | SoundFont 2 samples + INFO tags |
| `Shn` | Shorten (SHN) | WORM | — | — | — | — | Shorten split + assemble |
| `Sid` | Commodore 64 SID | R | — | — | — | — | C64 PSID render via 6502+SID, multi-SID routing |
| `Siren` | Siren7 / ITU-T G.722.1 | R | — | — | — | — | Raw Siren7/G.722.1; Annex-C boundary documented in source |
| `Smk` | Smacker Video | R | — | — | — | — | Smacker container: raw video + SMKA/PCM audio tracks |
| `Smp` | SampleVision (Turtle Beach) | WORM | — | — | — | — | Turtle Beach SampleVision |
| `Sndr` | Sounder (PC) | WORM | — | — | — | — | PC Sounder |
| `Sndt` | SoundTool | WORM | — | — | — | — | SoundTool |
| `Sol` | Sierra SOL | WORM | — | — | — | — | Sierra SOL DPCM/PCM |
| `Spc` | SNES SPC700 | R | — | — | — | — | SNES SPC tune render + BRR instruments + ID666 |
| `Sphere` | NIST SPHERE | WORM | — | — | — | — | NIST SPHERE, μ-law/A-law/PCM; Shorten-carried variants handled conservatively |
| `Stm` | STM (Scream Tracker 2) | R | — | — | — | — | Scream Tracker 2 sample archive |
| `Svx8` | IFF/8SVX (Amiga) | WORM | — | — | — | — | Amiga IFF/8SVX, Fibonacci-delta, planar stereo |
| `Swav` | SWAV (Nintendo DS sample) | WORM | — | — | — | — | Nintendo DS SWAV |
| `Tta` | True Audio (TTA) | WORM | — | — | — | — | True Audio lossless split + assemble |
| `Txw` | TXW (Yamaha TX16W) | WORM | — | — | — | — | Yamaha TX16W 12-bit packed |
| `Ult` | UltraTracker | R | — | — | — | — | UltraTracker sample archive |
| `Vag` | Sony VAG (PS1/PS2 SPU-ADPCM) | WORM | — | — | — | — | Sony VAG / SPU-ADPCM |
| `Vgm` | Video Game Music Log | R | — | — | — | — | VGM/VGZ renders through supported PSG/FM chips; GD3 tags |
| `Voc` | Creative Voice (.voc) | WORM | — | — | — | — | Creative Voice, including Creative 4-bit ADPCM paths |
| `Vox` | Dialogic VOX (OKI ADPCM) | WORM | — | — | — | — | Dialogic VOX headerless OKI ADPCM |
| `Wav` | WAV (RIFF audio) | WORM | ✅ | ✅ | ✅ | ✅ | RIFF WAV — INFO/LIST/bext metadata, multi-channel layout |
| `WavArc` | WavArc | R | — | — | — | — | WavArc 0CPY/1DIF paths; adaptive-LPC variants documented as limited |
| `Wave64` | Wave64 (.w64) | WORM | — | — | — | — | Sony Wave64 GUID-keyed chunks, 64-bit sizes |
| `WavPack` | WavPack (.wv) | R | ✅ | ✅ | ✅ | ✅ | WavPack lossless/hybrid container |
| `Wem` | Wwise Encoded Media | R | — | — | — | — | Wwise media; PCM/Wwise-IMA decode with codec-specific fallbacks |
| `WwiseBnk` | Wwise SoundBank | R | — | — | — | — | Audiokinetic Wwise SoundBank |
| `Xa` | CD-XA audio (.xa) | WORM | — | — | — | — | CD-XA / PlayStation streaming audio |
| `Xi` | XI (FastTracker II Instrument) | R | — | — | — | — | FastTracker II instrument |
| `Xm` | XM (FastTracker II) | R | — | — | — | — | FastTracker II XM render + samples |
| `Xmi` | Miles XMIDI | R | — | — | — | — | Miles XMIDI songs converted to MIDI |
| `Xwb` | XACT Wave Bank | R | — | — | — | — | Microsoft XACT wave bank; PCM/MS-ADPCM and XMA routing |

### Out of scope

The historical WAVE/ACM registry holds roughly 245 tags. Most are aliases, vendor
registrations or transport wrappers for a family already implemented above, and the tables
above map those onto the one codec that handles them. The families below are different: no
decoder for them exists here and none is planned, because each is a single-vendor codec with
no published bitstream and no surviving corpus to verify against. They are listed so the
absence is stated rather than left to be inferred from a missing row.

| Historical family | Representative tags |
| --- | --- |
| VSELP / Microsoft speech | `0x0004`, `0x0032`, `0x0066`, `0x0067`, `0x0082` |
| Voxware speech/music family | `0x0062`, `0x0069`-`0x007B`, `0x0081`, `0x181C` |
| Sonarc / PAC / proprietary music coders | `0x0021`, `0x0053`, `0x181E`, `0x1500` |
| APT-X / legacy studio codecs | `0x0025`, `0x0099` |
| Philips speech | `0x0098`, `0x0120`, `0x0121` |
| Qualcomm PureVoice / HalfRate | `0x0150`, `0x0151` |
| Intel Music Coder / Indeo Audio | `0x0401`, `0x0402` |
| QDesign Music | `0x0450` |
| On2 audio registrations | `0x0500`, `0x0501` |
| Olivetti / Lernout & Hauspie speech | `0x1000`-`0x1004`, `0x1100`-`0x1104` |
| Sonic Foundry / NCT lossless | `0x1971`, `0x1FC4` |
| Reserved / development registrations | `0x0000`, `0x008D`, `0x0301`-`0x0308`, `0x2500` range, `0xE708`, `0xFFFF` |

The G.729 family (`0x0044`, `0x0083`, `0x008C`, `0x0133`, `0x0134`, `0xA103`) is a gap rather
than a decision: the algorithm is published and implementable, there is simply no
implementation here yet.

## 🚀 Quick start

```csharp
using Codec.Pcm;
using FileFormat.Wav;

var blob = File.ReadAllBytes("input.wav");
var parsed = new WavReader().Read(blob);

foreach (var (name, monoWav) in PcmCodec.SplitInterleavedPcm(
             parsed.InterleavedPcm,
             parsed.NumChannels,
             parsed.SampleRate,
             parsed.BitsPerSample))
  File.WriteAllBytes($"{name}.wav", monoWav);
```

## 📚 Carried-data model

Every audio file can be surfaced as a pseudo-archive: the container is the archive, and what it carries becomes pseudo-files.

| Entry kind | Meaning |
| --- | --- |
| `Container` | Byte-exact original container (`FULL.<ext>`). |
| `Stream` | Carried coded elementary/logical stream. |
| `Track` | Track inside a multi-track container. |
| `Channel` | Decoded speaker channel, commonly surfaced as mono PCM WAV. |
| `Tag` | Metadata such as ID3, Vorbis comments, RIFF chunks, cover art, markers. |
| `Sample` / `Pattern` / `Instrument` | Tracker-module payloads. |

Channel naming follows explicit speaker bitmaps when present (`WAVE_FORMAT_EXTENSIBLE.dwChannelMask`, CAF channel bitmap). Otherwise the channel-count mapping covers mono, stereo, 2.1, 4.0, 5.0, 5.1, 6.1, 7.1, immersive 5.1.4/7.1.4/9.1.4/9.1.6 layouts, and NHK 22.2. Unknown layouts degrade to `CH_n`, so channels are not discarded. `Codec.Pcm.ChannelLayout`, `PcmCodec.SplitInterleavedPcm`, and `Interleave` provide the corresponding split/reassembly helpers.

## 🔬 Codec implementation scope

Each codec is expected to expose a decompression path appropriate to its format and metadata access where implemented; encoders exist only where the checked-in codec actually provides them. The package deliberately avoids claiming that a header parser, packet/framing layer, or stubbed spectral path equals complete decoding.

Modern codecs in particular should be read according to their source/tests:

- **MP3**: MPEG framing and decode code exist; reference-corpus and bit-exact claims must follow current tests rather than old README prose.
- **Vorbis**: the package contains the Vorbis decode path and Ogg integration; real-corpus interoperability evidence belongs in tests.
- **Opus**: any CELT/SILK/hybrid boundary in the checked-in decoder is a material limitation and must not be hidden by a generic green “R”.
- **AAC**: AAC-LC and any HE-AAC/SBR/profile boundary is documented as a subset rather than inferred as full ISO/IEC 14496-3 coverage.

The same rule applies to every long-tail codec: unsupported branches should fail explicitly or surface coded/raw data rather than fabricate PCM.

## 🧪 Validation and interoperability

The source repository uses codec/container round trips, synthetic vectors, format-specific regressions, and external-tool/reference comparisons where suitable. Exact numeric quality results belong to tests or generated reports rather than being frozen into package marketing copy, because implementations and test corpora evolve independently of NuGet README prose.

## 🔖 Versioning

The audio package is built against the repository's shared Core version and should be consumed with a compatible `Hawkynt.Compression.Core` version. Release tooling determines the concrete package version; this README does not predict future release states.

## 📚 API reference

<!-- API:BEGIN generated by Hawkynt/RepositoryTemplate/package-readme — edit the XML docs in source, not here -->

Every public and protected member of all 1257 types, generated from the built assembly and its XML documentation, is in [REFERENCE.md](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.FileFormats.Audio/REFERENCE.md).

<!-- API:END -->

## 🔌 Dependencies

| Dependency | Role |
| --- | --- |
| [`Hawkynt.Compression.Core`](https://www.nuget.org/packages/Hawkynt.Compression.Core/) | Shared compression, bit I/O, entropy, and registry primitives. |
| Native codecs / FFmpeg | **None required by the package.** External tools may be used by tests for validation, not at runtime. |

## ⚠️ Limitations

- `R` codecs are decoders, not hidden encoders. The tables deliberately distinguish those from `R/W` implementations.
- Advanced codecs can implement named profiles/subsets; source contracts and tests are authoritative for those edges.
- The package focuses on codec/container I/O, not DSP features such as resampling, loudness normalisation, or mastering.
- Tracker/chiptune/game-audio coverage includes emulation/synthesis-oriented components whose behavior is more nuanced than a single “supports format X” flag.
- External validation or an internal round trip is evidence for the tested path, not a blanket claim over every producer/profile/version of a codec.

## ❤️ Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see the repository [LICENSE](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE).
