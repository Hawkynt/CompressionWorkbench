# Hawkynt.FileFormats.Audio

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.FileFormats.Audio.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.Audio/)
[![NuGet downloads](https://img.shields.io/nuget/dt/Hawkynt.FileFormats.Audio.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.Audio/)
[![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE)
[![CI](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml)
![Target](https://img.shields.io/badge/target-net10.0-blue)

> Pure-managed audio handling for .NET, on top of `Hawkynt.Compression.Core`. The package claims the
> WHOLE domain — every codec, container, tracker/chiptune format and game-audio format — not a
> selection of it. Where a format is missing or only partly supported that is a tracked gap,
> recorded in the support matrix below and in
> [`docs/AUDIO-CODEC-COVERAGE.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/AUDIO-CODEC-COVERAGE.md).

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

State meanings used below:

| State | Meaning |
| --- | --- |
| **R** | Read/decode only. |
| **WORM** | Read plus create a fresh container/output; no in-place mutation. |
| **R/W** | Encode/decode or supported read/write semantics. |
| **⚠️** | Deliberate subset; see the row notes. |

### Codec support

| Codec | State | Scope | Reference |
| --- | :---: | --- | --- |
| [PCM](https://en.wikipedia.org/wiki/Pulse-code_modulation) | R/W | Integer PCM, interleaved/planar helpers | [WAVE PCM background](https://learn.microsoft.com/windows/win32/directshow/audio-subtypes) |
| [G.711 A-law / μ-law](https://en.wikipedia.org/wiki/G.711) | R/W | Companded PCM encode/decode | [ITU-T G.711](https://www.itu.int/rec/T-REC-G.711) |
| [IMA ADPCM](https://en.wikipedia.org/wiki/Adaptive_differential_pulse-code_modulation) | R | IMA/Intel plus QuickTime `ima4` decode | [Apple IMA4](https://developer.apple.com/library/archive/documentation/QuickTime/QTFF/QTFFChap3/qtff3.html) |
| [Microsoft ADPCM](https://wiki.multimedia.cx/index.php/Microsoft_ADPCM) | R | Decode | [Microsoft WAVE formats](https://learn.microsoft.com/windows/win32/xaudio2/resource-interchange-file-format--riff-) |
| [GSM 06.10](https://en.wikipedia.org/wiki/Full_Rate) | R | Speech decode | [ETSI GSM 06.10](https://www.etsi.org/deliver/etsi_gts/06/0610/05.00.00_60/gsmts_0610v050000p.pdf) |
| [FLAC](https://en.wikipedia.org/wiki/FLAC) | R | Frame-level lossless decode | [FLAC format](https://xiph.org/flac/format.html) |
| [Apple Lossless / ALAC](https://en.wikipedia.org/wiki/Apple_Lossless_Audio_Codec) | R/W | Lossless encode/decode | [Apple ALAC](https://github.com/macosforge/alac) |
| [WavPack](https://en.wikipedia.org/wiki/WavPack) | R/W | Lossless v4/v5 paths including floating-point handling | [WavPack](https://www.wavpack.com/) |
| [True Audio / TTA](https://en.wikipedia.org/wiki/True_Audio) | R/W | TTA1 encode/decode | [TTA overview](https://tausoft.org/wiki/True_Audio_Codec_Overview) |
| [Shorten](https://en.wikipedia.org/wiki/Shorten_(file_format)) | R/W | SHN decode plus supported encoder modes | [Shorten history/spec notes](https://web.archive.org/web/20070202083341/http://www.etree.org/shnutils/shorten/) |
| [Monkey's Audio](https://en.wikipedia.org/wiki/Monkey%27s_Audio) | R/W ⚠️ | Supported lossless levels; see exhaustive table | [Monkey's Audio](https://www.monkeysaudio.com/) |
| [MP3](https://en.wikipedia.org/wiki/MP3) | R | MPEG Audio decode path | [ISO/IEC 11172-3](https://www.iso.org/standard/22412.html) |
| [AAC](https://en.wikipedia.org/wiki/Advanced_Audio_Coding) | R ⚠️ | AAC-LC path; advanced profiles have documented boundaries | [ISO/IEC 14496-3](https://www.iso.org/standard/76383.html) |
| [Vorbis](https://en.wikipedia.org/wiki/Vorbis) | R | Vorbis I decode | [Vorbis I specification](https://xiph.org/vorbis/doc/Vorbis_I_spec.html) |
| [Opus](https://en.wikipedia.org/wiki/Opus_(audio_format)) | R ⚠️ | Opus path with profile/implementation limits documented below | [RFC 6716](https://www.rfc-editor.org/rfc/rfc6716) |
| [AC-3 / E-AC-3](https://en.wikipedia.org/wiki/Dolby_Digital) | R ⚠️ | Decode with documented advanced-feature boundaries | [ATSC A/52](https://www.atsc.org/atsc-documents/a52-20121217/) |
| [DTS](https://en.wikipedia.org/wiki/DTS_(company)#DTS_Digital_Surround) | R | Coherent Acoustics core decode | [ETSI TS 102 114](https://www.etsi.org/deliver/etsi_ts/102100_102199/102114/01.06.01_60/ts_102114v010601p.pdf) |
| [Speex](https://en.wikipedia.org/wiki/Speex) | R | Narrowband/wideband decode paths | [Speex manual](https://www.speex.org/docs/manual/speex-manual/) |
| [AMR-NB](https://en.wikipedia.org/wiki/Adaptive_Multi-Rate_audio_codec) | R | All narrowband modes | [3GPP TS 26.090](https://www.3gpp.org/ftp/Specs/archive/26_series/26.090/) |
| [AMR-WB / G.722.2](https://en.wikipedia.org/wiki/Adaptive_Multi-Rate_Wideband) | R | Wideband modes | [ITU-T G.722.2](https://www.itu.int/rec/T-REC-G.722.2) |
| [Sony BRR](https://en.wikipedia.org/wiki/Bit_Rate_Reduction) | R/W | SNES BRR filters and hardware-style reconstruction | [snesdev BRR](https://snes.nesdev.org/wiki/BRR_samples) |
| [CRI ADX](https://en.wikipedia.org/wiki/ADX_(file_format)) | R/W | ADX ADPCM encode/decode | [MultimediaWiki ADX](https://wiki.multimedia.cx/index.php/CRI_ADX_ADPCM) |
| [Nintendo DSP ADPCM](https://wiki.multimedia.cx/index.php/GameCube_DSP) | R/W | GC/Wii DSP-ADPCM | [MultimediaWiki GameCube DSP](https://wiki.multimedia.cx/index.php/GameCube_DSP) |

### Container / music-format support

| Format | State | Carried data / note | Reference |
| --- | :---: | --- | --- |
| [WAV / RIFF WAVE](https://en.wikipedia.org/wiki/WAV) | WORM | PCM/audio payload plus RIFF metadata; channel extraction | [Microsoft RIFF/WAVE](https://learn.microsoft.com/windows/win32/xaudio2/resource-interchange-file-format--riff-) |
| [AIFF](https://en.wikipedia.org/wiki/Audio_Interchange_File_Format) | WORM | AIFF audio container | [AIFF format description](https://www.loc.gov/preservation/digital/formats/fdd/fdd000005.shtml) |
| [AU/SND](https://en.wikipedia.org/wiki/Au_file_format) | WORM | Sun/NeXT audio | [AU format description](https://www.loc.gov/preservation/digital/formats/fdd/fdd000115.shtml) |
| [FLAC](https://en.wikipedia.org/wiki/FLAC) | WORM | FLAC container/metadata surface | [FLAC format](https://xiph.org/flac/format.html) |
| [Ogg](https://en.wikipedia.org/wiki/Ogg) | R | Logical streams, comments, coded packets | [RFC 3533](https://www.rfc-editor.org/rfc/rfc3533) |
| [MP3](https://en.wikipedia.org/wiki/MP3) | WORM | Audio frames plus ID3-carried metadata | [ID3v2](https://id3.org/id3v2.4.0-structure) |
| [MIDI SMF](https://en.wikipedia.org/wiki/MIDI#Standard_files) | R/W | Standard MIDI File parsing/re-emission | [MIDI Association SMF](https://midi.org/standard-midi-files) |
| [WavPack](https://en.wikipedia.org/wiki/WavPack) | WORM | WavPack file/container surface | [WavPack](https://www.wavpack.com/) |
| [MOD](https://en.wikipedia.org/wiki/MOD_(file_format)) | R | Tracker module playback/introspection | [ProTracker format notes](https://wiki.multimedia.cx/index.php/Protracker_Module) |
| [S3M](https://en.wikipedia.org/wiki/S3M_(file_format)) | R | Scream Tracker 3 modules | [S3M format](https://wiki.multimedia.cx/index.php/Scream_Tracker_3) |
| [XM](https://en.wikipedia.org/wiki/XM_(file_format)) | R | FastTracker II modules | [XM format](https://wiki.multimedia.cx/index.php/XM) |
| [IT](https://en.wikipedia.org/wiki/Impulse_Tracker) | R | Impulse Tracker modules, including compressed sample paths | [IT format](https://wiki.multimedia.cx/index.php/Impulse_Tracker) |
| [PSF](https://en.wikipedia.org/wiki/Portable_Sound_Format) | R | Chiptune/program-image workflows | [PSF format overview](https://en.wikipedia.org/wiki/Portable_Sound_Format) |
| [Wwise BNK](https://en.wikipedia.org/wiki/Audiokinetic_Wwise) | R | Game-audio bank parsing | [Audiokinetic Wwise](https://www.audiokinetic.com/en/library/) |
| [FMOD](https://en.wikipedia.org/wiki/FMOD) | R | FMOD game-audio containers | [FMOD](https://www.fmod.com/) |

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

## 📚 Complete codec inventory

The following is the exhaustive package reference retained from the original package README. The state column describes the checked-in codec surface; partial modern codecs must also be read together with the limitations section below.

| Codec | Family | State | Description |
| --- | --- | --- | --- |
| `Codec.Pcm` | PCM | R/W | Raw integer PCM (8/16/24/32-bit), interleaved + planar, channel splitting |
| `Codec.ALaw` | PCM (companded) | R/W | ITU-T G.711 A-law encode + decode |
| `Codec.MuLaw` | PCM (companded) | R/W | ITU-T G.711 μ-law encode + decode |
| `Codec.Midi` | Symbolic | R/W | SMF parsing + per-track re-emit (`BuildSingleTrackFile`) |
| `Codec.ImaAdpcm` | ADPCM | R | IMA / Intel ADPCM + QuickTime `ima4` packet variant |
| `Codec.MsAdpcm` | ADPCM | R | Microsoft ADPCM decode |
| `Codec.Gsm610` | Speech | R | GSM 6.10 RPE-LTP decode |
| `Codec.Mp3` | Lossy | R ⚠️ | MPEG Audio decode path; see implementation scope below |
| `Codec.Aac` | Lossy | R ⚠️ | AAC-LC path with advanced-profile limits |
| `Codec.Vorbis` | Lossy | R | Vorbis I decode |
| `Codec.Opus` | Lossy | R ⚠️ | Opus path with documented CELT/SILK/hybrid limits |
| `Codec.Flac` | Lossless | R | FLAC frame-level decode |
| `Codec.OkiAdpcm` | ADPCM | R/W | OKI / Dialogic VOX 4-bit ADPCM |
| `Codec.SpuAdpcm` | ADPCM | R/W | Sony PS1/PS2 SPU ADPCM |
| `Codec.DspAdpcm` | ADPCM | R/W | Nintendo GC/Wii DSP-ADPCM + predictor-fit encoder |
| `Codec.G72x` | ADPCM | R/W | ITU-T G.726 16/24/32/40 kbit encode + decode |
| `Codec.Tta` | Lossless | R/W | True Audio TTA1 encode + decode |
| `Codec.Shorten` | Lossless | R/W | Shorten v2 decode including best-effort QLPC + DIFF0-3 encoder |
| `Codec.Alac` | Lossless | R/W | Apple Lossless 16/24-bit paths |
| `Codec.WavPack` | Lossless | R/W | WavPack v4/v5 lossless including IEEE-float handling |
| `Codec.CriAdx` | ADPCM | R/W | CRI ADX high-pass-derived coefficients |
| `Codec.Brr` | ADPCM | R/W | SNES S-DSP BRR filters 0-3 and hardware-style wrap |
| `Codec.XaAdpcm` | ADPCM | R/W | CD-ROM XA / PlayStation streaming ADPCM |
| `Codec.EaXa` | ADPCM | R/W | Electronic Arts EA-XA coefficient/shift frames |
| `Codec.WwiseIma` | ADPCM | R/W | Audiokinetic Wwise IMA / MS-IMA block layout |
| `Codec.AicaAdpcm` | ADPCM | R/W | Yamaha AICA / ADPCM-B (Dreamcast) |
| `Codec.G722` | Speech | R/W | ITU-T G.722 sub-band ADPCM @ 64 kbit |
| `Codec.Cvsd` | Speech | R/W | CVSD delta modulation |
| `Codec.Mace` | Lossy | R | Apple MACE 3:1 / 6:1 decode |
| `Codec.MonkeysAudio` | Lossless | R/W ⚠️ | Monkey's Audio supported compression levels |
| `Codec.Ra144` | Speech | R | RealAudio 14.4 / lpcJ |
| `Codec.TrueSpeech` | Speech | R | DSP Group TrueSpeech WAV tag 0x0022 |
| `Codec.InterplayAcm` | Lossy | R | Interplay ACM |
| `Codec.Nellymoser` | Lossy | R | Nellymoser / Flash |
| `Codec.WsAdpcm` | ADPCM | R | Westwood WS ADPCM + continuous-IMA paths |
| `Codec.RoqDpcm` | DPCM | R/W | id RoQ square-table DPCM |
| `Codec.SolDpcm` | DPCM | R | Sierra SOL old/new DPCM tables |
| `Codec.Lpc10` | Speech | R/W ⚠️ | FS-1015 LPC-10e 2400 bit/s, simplified pitch tracker documented in source |
| `Codec.Cook` | Lossy | R | RealAudio G2 `cook` |
| `Codec.Atrac3` | Lossy | R | Sony ATRAC3 |
| `Codec.Ac3` | Lossy | R ⚠️ | AC-3 / E-AC-3 with advanced-feature boundaries |
| `Codec.Wma` | Lossy | R | WMA v1/v2 |
| `Codec.Musepack` | Lossy | R | Musepack SV7/SV8 |
| `Codec.WmaPro` | Lossy | R | WMA 9 Professional |
| `Codec.Sipr` | Speech | R ⚠️ | RealAudio ACELP.NET; unsupported mode boundaries documented in source |
| `Codec.Speex` | Speech | R | Speex narrowband + wideband paths |
| `Codec.G7231` | Speech | R | ITU G.723.1 dual-rate |
| `Codec.Dts` | Lossy | R | DTS Coherent Acoustics core |
| `Codec.Mos6502` | CPU core | — | Reusable NMOS 6502 including stable illegal opcodes, BCD and cycle counting |
| `Codec.Z80` | CPU core | — | Z80 core with CB/ED/DD/FD, block ops and interrupt modes |
| `Codec.Sid` | Synthesis | R | MOS 6581/8580/6582 + PSID player with multi-SID routing |
| `Codec.Spc700` | Synthesis | R | SNES SPC700 CPU + S-DSP |
| `Codec.Nes2a03` | Synthesis | R | NES APU + NSF expansion audio |
| `Codec.GameBoyApu` | Synthesis | R | SM83 CPU + Game Boy APU + GBS player |
| `Codec.Ay8910` | Synthesis | R | AY-3-8910/YM2149 |
| `Codec.Sn76489` | Synthesis | R | SEGA PSG |
| `Codec.Ym2612` | Synthesis | R | OPN2 FM |
| `Codec.Ym2413` | Synthesis | R | OPLL FM |
| `Codec.Ym2151` | Synthesis | R | OPM FM |
| `Codec.Ym2203` | Synthesis | R | OPN FM + SSG |
| `Codec.Ym2608` | Synthesis | R ⚠️ | OPNA FM + SSG; rhythm/ADPCM-B boundaries documented in source |
| `Codec.Opl` | Synthesis | R ⚠️ | OPL/OPL2/OPL3/Y8950 family; Y8950 ADPCM boundary documented in source |
| `Codec.HuC6280` | CPU + synthesis | R | PC Engine HuC6280 + 6-channel wavetable PSG |
| `Codec.AmrNb` | Speech | R | 3GPP AMR narrowband, all 8 modes |
| `Codec.AmrWb` | Speech | R | 3GPP AMR wideband / G.722.2, all 9 modes |
| `Codec.Tracker` | Tracker | R | ProTracker MOD + Scream Tracker 3 S3M playback |
| `Codec.TrackerXmIt` | Tracker | R | FastTracker II XM + Impulse Tracker IT playback, IT214/215 samples |
| `Codec.AdpcmX` | ADPCM | R | IMA DK3/DK4/EACS/SEAD, EA R1-R3, THP/AFC, SWF, 4XM, Xan, Interplay, SDX2, DERF, Gremlin |
| `Codec.Atrac1` | Lossy | R | Sony ATRAC1 / MiniDisc |
| `Codec.Ra288` | Speech | R | RealAudio 28.8 / G.728-style path |
| `Codec.Ralf` | Lossless | R | RealAudio Lossless |
| `Codec.CriHca` | Lossy | R ⚠️ | CRI HCA; keyed-cipher limitations documented in source |
| `Codec.Sbc` | Lossy | R | Bluetooth SBC + mSBC |
| `Codec.Siren` | Lossy | R ⚠️ | Siren7 / G.722.1; Annex C boundary documented in source |
| `Codec.S302M` | PCM (mapped) | R/W | SMPTE 302M AES3 subframes |
| `Codec.BinkAudio` | Lossy | R | Bink audio RDFT + DCT flavours |
| `Codec.SmackerAudio` | Lossy | R | Smacker SMKA |
| `Codec.WmaLossless` | Lossless | R | WMA Lossless 0x0163 |
| `Codec.Xma` | Lossy | R ⚠️ | XMA1/XMA2 packet/extradata layer over WMAPro; full-decode boundaries documented in source |
| `Codec.Qoa` | Lossy (DPCM) | R/W | Quite OK Audio sign-LMS slices |
| `Codec.Dfpwm` | Lossy (1-bit) | R | DFPWM1a |
| `Codec.Bonk` | Lossless/lossy | R | Bonk |
| `Codec.WavArc` | Lossless | R ⚠️ | WavArc `.wa`; adaptive-LPC block boundaries documented in source |

Most modern lossy/lossless codecs are decoders rather than encoders. PCM, G.711 and a number of legacy/game codecs have practical encoders; absence of an encoder is not hidden behind the generic word “support.”

## 📚 Complete container and music-format inventory

| Container / format | State | Description |
| --- | --- | --- |
| `FileFormat.Wav` | WORM | RIFF WAV — INFO/LIST/bext metadata, multi-channel layout |
| `FileFormat.Mp3` | WORM | MP3 — ID3v1/v2 tags + decoded channels; fresh MP3 stream construction surface |
| `FileFormat.Flac` | WORM | FLAC stream + STREAMINFO/VORBIS_COMMENT/PICTURE metadata |
| `FileFormat.Akb` | WORM | Square Enix audio bank |
| `FileFormat.Awb` | WORM | CRI Audio Wave Bank |
| `FileFormat.Psf` | WORM | PlayStation Sound Format family container |
| `FileFormat.Aiff` | WORM | AIFF/AIFC; multi-channel assembly from channel WAVs |
| `FileFormat.Au` | WORM | Sun/NeXT `.au` / `.snd` |
| `FileFormat.Caf` | WORM | Apple Core Audio Format, LPCM `desc`/`data` chunks |
| `FileFormat.Wave64` | WORM | Sony Wave64 GUID-keyed chunks, 64-bit sizes |
| `FileFormat.Rf64` | WORM | RF64/BWF, `ds64` sizes + `bext` |
| `FileFormat.Voc` | WORM | Creative Voice, including Creative 4-bit ADPCM paths |
| `FileFormat.Svx` | WORM | Amiga IFF/8SVX, Fibonacci-delta, planar stereo |
| `FileFormat.Avr` | WORM | AVR / Atari ST big-endian PCM |
| `FileFormat.Sphere` | WORM | NIST SPHERE, μ-law/A-law/PCM; Shorten-carried variants handled conservatively |
| `FileFormat.Ircam` | WORM | IRCAM/BICSF integer + float channels |
| `FileFormat.Vox` | WORM | Dialogic VOX headerless OKI ADPCM |
| `FileFormat.Gsm` | R | Raw GSM 06.10 frames |
| `FileFormat.Dsf` | WORM | Sony DSD, per-channel raw DSD + decimated PCM views |
| `FileFormat.Dff` | WORM | Philips DSDIFF, CHNL speaker IDs; DST boundary documented in source |
| `FileFormat.Tta` | WORM | True Audio lossless split + assemble |
| `FileFormat.Shn` | WORM | Shorten split + assemble |
| `FileFormat.Vag` | WORM | Sony VAG / SPU-ADPCM |
| `FileFormat.Brstm` | WORM | Nintendo BRSTM, DSP-ADPCM/PCM channels |
| `FileFormat.Sf2` | R | SoundFont 2 samples + INFO tags |
| `FileFormat.Dls` | R | Downloadable Sounds, `wvpl` waves |
| `FileFormat.G711` | WORM | Raw A-law/μ-law @ 8 kHz |
| `FileFormat.Mpc` | R | Musepack SV7/SV8 |
| `FileFormat.Adx` | WORM | CRI ADX/AHX paths |
| `FileFormat.Brr` | WORM | SNES BRR sample |
| `FileFormat.Spc` | R | SNES SPC tune render + BRR instruments + ID666 |
| `FileFormat.Xa` | WORM | CD-XA / PlayStation streaming audio |
| `FileFormat.Bcstm` | WORM | Nintendo 3DS CSTM over DSP-ADPCM |
| `FileFormat.Bfstm` | WORM | Wii U/Switch FSTM over DSP-ADPCM |
| `FileFormat.Ast` | WORM | GameCube/Wii AST |
| `FileFormat.Hps` | WORM | GameCube HALPST linked DSP-ADPCM blocks |
| `FileFormat.Bwav` | WORM | Switch BWAV |
| `FileFormat.Swav` | WORM | Nintendo DS SWAV |
| `FileFormat.Sdat` | R | Nintendo DS sound archive; SWAV/SWAR plus SSEQ/SBNK/STRM surfaces |
| `FileFormat.Xwb` | R | Microsoft XACT wave bank; PCM/MS-ADPCM and XMA routing |
| `FileFormat.EaSchl` | WORM | EA SCHl streams |
| `FileFormat.Wem` | R ⚠️ | Wwise media; PCM/Wwise-IMA decode with codec-specific fallbacks |
| `FileFormat.Aica` | WORM | Yamaha AICA raw |
| `FileFormat.Cvsd` | WORM | Raw CVSD bitstream |
| `FileFormat.Maud` | WORM | Amiga IFF MAUD |
| `FileFormat.Smp` | WORM | Turtle Beach SampleVision |
| `FileFormat.Paf` | WORM | Ensoniq PARIS |
| `FileFormat.Pvf` | WORM | Portable Voice Format binary + ASCII |
| `FileFormat.MacSnd` | R | Classic Mac `snd ` resource |
| `FileFormat.Sndr` | WORM | PC Sounder |
| `FileFormat.Sndt` | WORM | SoundTool |
| `FileFormat.EspsSd` | R | Entropic ESPS `.sd` |
| `FileFormat.Txw` | WORM | Yamaha TX16W 12-bit packed |
| `FileFormat.Hcom` | WORM | Macintosh HCOM Huffman-delta |
| `FileFormat.Xi` | R | FastTracker II instrument |
| `FileFormat.Sds` | R | MIDI Sample Dump Standard |
| `FileFormat.Med` | R | OctaMED sample archive |
| `FileFormat.Okt` | R | Oktalyzer sample/pattern archive |
| `FileFormat.Ult` | R | UltraTracker sample archive |
| `FileFormat.F669` | R | Composer 669 sample archive |
| `FileFormat.Its` | R ⚠️ | Impulse Tracker sample; compression boundaries documented in source |
| `FileFormat.Iti` | R | Impulse Tracker instrument |
| `FileFormat.Mtm` | R | MultiTracker sample archive |
| `FileFormat.Far` | R | Farandole Composer sample archive |
| `FileFormat.Stm` | R | Scream Tracker 2 sample archive |
| `FileFormat.Ptm` | R | PolyTracker sample archive |
| `FileFormat.Amf` | R | DSMI AMF sample archive |
| `FileFormat.Psm` | R | Epic MASI PSM sample archive |
| `FileFormat.Asf` | R | Microsoft ASF, WMA-family audio depayload/decode routes + tags |
| `FileFormat.RealMedia` | R | RealMedia, codec-routed audio channels |
| `FileFormat.Acm` | R | Interplay ACM |
| `FileFormat.Nelly` | R | Raw Nellymoser block stream |
| `FileFormat.Aud` | WORM | Westwood AUD / WS-ADPCM + IMA |
| `FileFormat.Roq` | WORM | id RoQ DPCM sound chunks; video chunks counted/surfaced separately |
| `FileFormat.Sol` | WORM | Sierra SOL DPCM/PCM |
| `FileFormat.Apc` | R | CRYO APC seeded IMA |
| `FileFormat.Lpc10` | WORM | Raw LPC-10e @ 8 kHz |
| `FileFormat.G7231` | R | Raw G.723.1 including SID/CNG paths |
| `FileFormat.Dts` | R ⚠️ | Raw DTS core; HD extensions are not silently treated as core support |
| `FileFormat.Ac3` | R | Raw AC-3/E-AC-3 independent substreams |
| `FileFormat.Oma` | R ⚠️ | Sony OpenMG / ATRAC3 route with non-ATRAC payloads surfaced according to type |
| `FileFormat.Vgm` | R | VGM/VGZ renders through supported PSG/FM chips; GD3 tags |
| `FileFormat.Mus` | R | Doom MUS converted to MIDI |
| `FileFormat.Xmi` | R | Miles XMIDI songs converted to MIDI |
| `FileFormat.Cmf` | R | Creative CMF OPL patches + MIDI surface |
| `FileFormat.Nsf` | R | NES NSF/NSFE render via 6502+2A03 plus supported expansions |
| `FileFormat.Gbs` | R | Game Boy GBS render via SM83+APU |
| `FileFormat.Sid` | R | C64 PSID render via 6502+SID, multi-SID routing |
| `FileFormat.Kss` | R | MSX KSS render via Z80+AY; extension-chip limits documented in source |
| `FileFormat.Hes` | R | PC Engine HES render via HuC6280+PSG |
| `FileFormat.Gym` | R | Genesis GYM via YM2612+PSG |
| `FileFormat.Ay` | R | ZX Spectrum AY via Z80+AY-3-8910 |
| `FileFormat.Alac` | R | Apple Lossless inside MP4 atoms |
| `FileFormat.Ape` | R | Monkey's Audio container |
| `FileFormat.WavPack` | R | WavPack lossless/hybrid container |
| `FileFormat.Ogg` | R | Ogg packet blobs, comments, Vorbis/Opus routes |
| `FileFormat.Opus` | R ⚠️ | Opus-in-Ogg route; codec limits apply |
| `FileFormat.Aac` | R ⚠️ | AAC ADTS route; codec profile limits apply |
| `FileFormat.Midi` | R | SMF 0/1/2 container |
| `FileFormat.Mod` | R | ProTracker/SoundTracker MOD render + samples |
| `FileFormat.S3m` | R | Scream Tracker 3 render + samples |
| `FileFormat.Xm` | R | FastTracker II XM render + samples |
| `FileFormat.It` | R | Impulse Tracker IT render + IT214/215 samples |
| `FileFormat.WwiseBnk` | R | Audiokinetic Wwise SoundBank |
| `FileFormat.Fmod` | R | FMOD bank container |
| `FileFormat.Aea` | R | Sony MiniDisc AEA / ATRAC1 |
| `FileFormat.Hca` | R ⚠️ | CRI HCA; keyed-cipher/MS-stereo boundaries documented in source |
| `FileFormat.Sbc` | R | Raw Bluetooth SBC/mSBC |
| `FileFormat.Siren` | R ⚠️ | Raw Siren7/G.722.1; Annex-C boundary documented in source |
| `FileFormat.Bik` | R | Bink container: raw video + decoded supported audio tracks |
| `FileFormat.Smk` | R | Smacker container: raw video + SMKA/PCM audio tracks |
| `FileFormat.Mp4` | R | MP4/MOV audio tracks routed to AAC/ALAC/MP3/AC-3/FLAC/Opus/PCM; video stays a carried track |
| `FileFormat.Matroska` | R | Matroska/WebM audio-track routing, attachments/lacing as implemented |
| `FileFormat.Avi` | R | AVI `auds` stream routing including PCM/ADPCM/MPEG Audio/AC-3/G.711 variants |
| `FileFormat.Amr` | R | 3GPP AMR NB/WB, including MC1.0 multichannel surface |
| `FileFormat.Qoa` | R | Quite OK Audio container |
| `FileFormat.Dfpwm` | R | Headerless DFPWM1a convention |
| `FileFormat.Bonk` | R | Bonk container |
| `FileFormat.WavArc` | R ⚠️ | WavArc 0CPY/1DIF paths; adaptive-LPC variants documented as limited |

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

Every public and protected member of all 843 types, generated from the built assembly and its XML documentation, is in [REFERENCE.md](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.FileFormats.Audio/REFERENCE.md).

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
