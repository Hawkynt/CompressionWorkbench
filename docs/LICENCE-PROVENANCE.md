# Licence provenance of converted code

This ledger lists every place in product code (everything except tests and `Vendored/**`)
whose comments say the code was ported, converted or transcribed from somebody else's source,
and what that means for this repository's **LGPL-3.0-or-later** licence. It applies the
"Sourcing an implementation" ladder in [`AGENTS.md`](../AGENTS.md).

`Compression.Tests/Architecture/ProvenanceClaimsAreRecordedTests.cs` keeps it complete: a
source file with a port claim ("faithful port of", "ported from", "converted from",
"transcribed from" …) fails the build unless its path, or a directory above it written with a
trailing slash, appears in back-quotes below. A path listed here that no longer exists fails
it too.

Audit of 2026-10-19: upstream licence headers were read at the revisions named in the
notices, FFmpeg's at tag `n7.1`.

## Classes

| Class | Meaning | Action |
|---|---|---|
| **A** | Licence-compatible, required notice already present (or none required) | none |
| **B** | Licence-compatible, notice was missing | notice added in this audit |
| **C** | Licence-incompatible (GPL-only, LGPL-2.1-only, no licence) | clean-room rewrite from the named specification, by someone who has not read the original |
| **R** | Not a code port: the comment cites an outside source for format facts, constants, behaviour or as an oracle, or disclaims a port | none, unless marked *audit* |

Where the notices live: audio code ships its notices in the root
[`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md), which the `Hawkynt.FileFormats.Audio`
package carries; everything else keeps a `THIRD-PARTY-NOTICE.<name>.txt` beside the code.

## Summary

| Class | Upstream rows | Notes |
|---|---|---|
| A | 19 | |
| B | 17 | FFmpeg alone covers 38 codec folders |
| C | 3 | specifications: `docs/CVF-CODECS-ON-DISK.md` (rewrite already under way), `docs/IT-SAMPLE-COMPRESSION-SPEC.md`, `docs/FDS-EXPANSION-SOUND-SPEC.md` |
| R | 8 | |

## C — incompatible, must be rewritten clean-room

| Claim site | Upstream | Licence | Specification for the rewrite | Status |
|---|---|---|---|---|
| `Compression.Core/FileSystems/Cvf/CvfLzCodec.cs` (bit reader: "faithful port of dmsdos dblb_*") | dmsdos (`sandsmark/dmsdos`) decompression routines (`dblb_*` bit reader and the DS/JM decoders) | GPL-2.0 | [`CVF-CODECS-ON-DISK.md`](CVF-CODECS-ON-DISK.md) | clean-room rewrite in progress separately |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.TrackerXmIt/ItSampleDecompressor.cs` | Schism Tracker `fmt/compression.c` (`it_decompress8/16`) | GPL-2.0-or-later | [`IT-SAMPLE-COMPRESSION-SPEC.md`](IT-SAMPLE-COMPRESSION-SPEC.md) | to do |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Nes2a03/Expansion/FdsAudio.cs` | NSFPlay `xgm/devices/Sound/nes_fds.cpp` (Brezza / Brad Smith) | none — the readme only presumes redistribution is allowed | [`FDS-EXPANSION-SOUND-SPEC.md`](FDS-EXPANSION-SOUND-SPEC.md) | to do |

## B — compatible, notice added

| Claim site | Upstream | Licence | Notice |
|---|---|---|---|
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Aac/AacSbr.cs`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Aac/AacSbrTables.cs`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Ac3/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.AdpcmX/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.AmrNb/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.AmrWb/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Atrac1/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Atrac3/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.BinkAudio/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Bonk/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Cook/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.CriHca/HcaCodec.cs`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Dfpwm/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Dts/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.G7231/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Mace/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Musepack/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Nellymoser/NellymoserLfg.cs`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Ra144/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Ra288/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Ralf/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.S302M/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Sbc/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Sipr/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Siren/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.SmackerAudio/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.SolDpcm/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.TrueSpeech/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Tta/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.WavArc/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Wma/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.WmaLossless/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.WmaPro/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Xma/`, `Hawkynt.FileFormats.Audio/FileFormats/FileFormat.Voc/VocReader.cs` (Creative ADPCM), plus the FFmpeg halves of Qoa, MonkeysAudio and Speex below | FFmpeg `libavcodec`/`libavformat`/`libavutil` — per-folder file list in the notice | LGPL-2.1-or-later (every header checked; none is a GPL-only part) — taken under LGPL-3.0 | `THIRD-PARTY-NOTICES.md` § FFmpeg |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.InterplayAcm/` | FFmpeg `libavcodec/interplayacm.c` (Kreen, Gashlin, Mahol) | ISC | `THIRD-PARTY-NOTICES.md` § Interplay ACM |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Speex/` | FFmpeg `speexdec.c`/`speexdata.h`, derived from Xiph libspeex | BSD-3-Clause (Xiph) + LGPL-2.1-or-later | `THIRD-PARTY-NOTICES.md` § Speex |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.MonkeysAudio/` | Monkey's Audio SDK (Matthew T. Ashland); also FFmpeg `apedec.c` | BSD-3-Clause; LGPL-2.1-or-later | `THIRD-PARTY-NOTICES.md` § Monkey's Audio SDK |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.WavPack/` | WavPack (`dbry/WavPack`: `unpack.c`, `pack.c`, `words.c`, `bits.c`, float support) | BSD-3-Clause | `THIRD-PARTY-NOTICES.md` § WavPack |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Qoa/` | `phoboslab/qoa` `qoa.h`; also FFmpeg `qoadec.c` | MIT; LGPL-2.1-or-later | `THIRD-PARTY-NOTICES.md` § QOA |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.CriHca/HcaImdct.cs` | VGAudio `Mdct.RunImdct` | MIT | `THIRD-PARTY-NOTICES.md` § VGAudio |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Opus/OpusRangeDecoder.cs` | libopus `celt/entdec.c`, `entcode.c` | BSD-3-Clause | `THIRD-PARTY-NOTICES.md` § libopus |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Opl/` | Nuked-OPL3 1.8 (Nuke.YKT) | LGPL-2.1-or-later — taken under LGPL-3.0 | `THIRD-PARTY-NOTICES.md` § Nuked-OPL3 |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Ym2413/` | emu2413 1.5.9 (Mitsutaka Okazaki) | MIT | `THIRD-PARTY-NOTICES.md` § emu2413 |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.G72x/` | Sun Microsystems G.721/G.723/G.726 reference (`g72x.c` …) | permissive ("unrestricted use") | `THIRD-PARTY-NOTICES.md` § CCITT / Sun |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Bsc/` | libbsc (Ilya Grebnov) LZP, QLFC, range coder, tables | Apache-2.0 | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Bsc/THIRD-PARTY-NOTICE.libbsc.txt` — the sources already pointed to it, but it had never been committed |
| `Compression.Core/Transforms/BcjFilter.cs` (ARM-Thumb, ARM64, RISC-V) | XZ Utils liblzma `simple/armthumb.c`, `arm64.c`, `riscv.c` | 0BSD | `Compression.Core/Transforms/THIRD-PARTY-NOTICE.liblzma.txt` |
| `Compression.Core/Dictionary/Zx0/Zx0BuildingBlock.cs` | ZX0 (Einar Saukas) `compress.c` stream format | BSD-3-Clause | `Compression.Core/Dictionary/Zx0/THIRD-PARTY-NOTICE.zx0.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Ghost/` | `nyarime/gho` (Go) | MIT | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Ghost/THIRD-PARTY-NOTICE.gho.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Macrium/MacriumPreXFormatDescriptor.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Macrium/MacriumPreXCodec.cs` | `ccooper21/mrimg-tools` (Christopher Cooper) | MIT | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Macrium/THIRD-PARTY-NOTICE.mrimg-tools.txt` |
| `Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.Hammer/HammerWriter.cs`, `Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.Hammer2/Hammer2Writer.cs` | DragonFly BSD `newfs_hammer`, `newfs_hammer2` and their on-disk helpers | BSD-3-Clause | `THIRD-PARTY-NOTICE.dragonfly.txt` in each folder |

## A — compatible, notice already in place

| Claim site | Upstream | Licence | Where the notice is |
|---|---|---|---|
| `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.Trx/TrxImage.cs`, `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.Seama/SeamaImage.cs` | binwalk v3 TRX, SEAMA and DLOB header layouts, revision `26713972e3f9f52dc37d4a421c4554b0bd9e82ef` | MIT | `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.Trx/THIRD-PARTY-NOTICE.binwalk.txt`, `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.Seama/THIRD-PARTY-NOTICE.binwalk.txt` |
| `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.AndroidBootImage/AndroidBootImage.cs`, `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.AndroidBootImage/AndroidBootImageMetadata.cs` | AOSP mkbootimg `bootimg.h`, `mkbootimg.py` and `unpack_bootimg.py`, revision `d2bb0af5ba6d3198a3e99529c97eda1be0b5a093` | BSD-3-Clause and Apache-2.0 | `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.AndroidBootImage/THIRD-PARTY-NOTICE.mkbootimg.txt` |
| `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.AndroidVbmeta/AvbVbmeta.cs`, `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.AndroidVbmeta/AvbDescriptorText.cs` | AOSP Android Verified Boot headers and `avbtool.py`, revision `c5066a96caa7bf4150c0a8cc8cc14ab81733fdc7` | MIT | `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.AndroidVbmeta/THIRD-PARTY-NOTICE.avb.txt` |
| `Compression.Core/Dictionary/Lzbitmap/` | libzbitmap (Ernesto A. Fernández, Corellium) | MIT | `THIRD-PARTY-NOTICE.libzbitmap.txt` |
| `Compression.Core/Dictionary/Csc/` | libcsc (Fu Siyuan) | Unlicense | `THIRD-PARTY-NOTICE.libcsc.txt` |
| `Compression.Core/Dictionary/Arj/ArjFastestDecoder.cs` | XADMaster `XADARJFastestHandle.m` (Dag Ågren, MacPaw), revision `7cb9ee0abbb163f261e4cb74501e15067032319c` | LGPL-2.1-or-later | `Compression.Core/Dictionary/Arj/THIRD-PARTY-NOTICE.XADMaster.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.DiskDoubler/DiskDoublerReader.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.DiskDoubler/DiskDoublerAdDecoder.cs` | XADMaster `XADDiskDoublerParser.m` and `XADDiskDoublerADnHandle.m`, revision `7cb9ee0abbb163f261e4cb74501e15067032319c` | LGPL-2.1-or-later | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.DiskDoubler/THIRD-PARTY-NOTICE.XADMaster.txt` |
| `Compression.Core/Dictionary/Lzh/Lh2Decoder.cs`, `Compression.Core/Dictionary/Lzh/Lh3Decoder.cs`, `Compression.Core/Dictionary/Lzh/LhaLegacyBitReader.cs` | XADMaster `XADLZHOldHandles.m`, revision `7cb9ee0abbb163f261e4cb74501e15067032319c` | LGPL-2.1-or-later | `Compression.Core/Dictionary/Lzh/THIRD-PARTY-NOTICE.XADMaster.txt` |
| `Compression.Core/Dictionary/Lzh/Lh1Tree.cs`, `Compression.Core/Dictionary/Lzh/Lh1Decoder.cs`, `Compression.Core/Dictionary/Lzh/LzsDecoder.cs`, `Compression.Core/Dictionary/Lzh/Lz5Decoder.cs`, `Compression.Core/Dictionary/Lzh/Pm1Decoder.cs`, `Compression.Core/Dictionary/Lzh/Pm2Decoder.cs`, `Compression.Core/Dictionary/Lzh/PmaHistory.cs`, `Compression.Core/Dictionary/Lzh/LhaBitInput.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Lzh/LhaHeaderParser.cs` | lhasa (Simon Howard), revision `75ed83559f23e9538e0045c62f53f77ab03d03d6` | ISC | `Compression.Core/Dictionary/Lzh/THIRD-PARTY-NOTICE.lhasa.txt`; `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Lzh/THIRD-PARTY-NOTICE.lhasa.txt` |
| `Compression.Core/Streams/Lzfse/` | Apple lzfse reference | BSD-3-Clause | `THIRD-PARTY-NOTICE.lzfse.txt`; `LzfseFseDecoder.cs` also carries the header inline |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Nellymoser/NellymoserCodec.cs`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Nellymoser/NellymoserTables.cs` | FFmpeg `nellymoserdec.c`, `nellymoser.c` | MIT | `THIRD-PARTY-NOTICES.md` § Nellymoser |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Alac/` | Apple ALAC reference | Apache-2.0 | `THIRD-PARTY-NOTICES.md` § ALAC |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Gsm610/` | GSM 06.10 reference (Degener, Bormann, TU Berlin) | permissive, notice must stay | heads `Gsm610Codec.cs`; now also in `THIRD-PARTY-NOTICES.md` |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Mp3/` | minimp3 (`lieff/minimp3` 7b590fdc) — and, for `Mp3Encoder.Managed.cs`, the vendored GroovyCodecs | CC0-1.0; LGPL-3.0 | CC0 needs none; GroovyCodecs in `THIRD-PARTY-NOTICES.md` and `VENDORED_AUDIO_CODECS.md` |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Lpc10/Lpc10Tables.cs` | U.S. DoD LPC-10 reference tables | public domain | none needed |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Vorbis/VorbisCodec.cs` | stb_vorbis 1.22 (structural reference) | public domain / MIT | none needed |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Ba2/BethesdaLookup3.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Balz/BalzStream.cs`, `Compression.Core/Entropy/Ppmd/PpmdModelH.cs` | Bob Jenkins lookup3; BALZ max-parser score; Shkarin's PPMd rescale constant | public domain / constants | none needed |
| `Hawkynt.FileFormats.Audio/Codecs/VENDORED_AUDIO_CODECS.md` | Concentus, .NET Ogg Vorbis Encoder, GroovyCodecs, AmrWbLib (vendored whole) | BSD-3 / MIT / LGPL-3.0 / BSD-3 | `THIRD-PARTY-NOTICES.md` and the vendored `LICENSE.txt` files |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Aomei/AomeiFormatDescriptor.cs` | this repository's reverse-engineered AOMEI Backupper container layout | own work | none needed |

## R — not a code port

| Claim site | What the comment refers to | Note |
|---|---|---|
| `Hawkynt.FileFormats.Audio/Codecs/Codec.G722/G722Codec.cs` | ITU-T G.722 (09/2012), via [`G722-SPEC.md`](G722-SPEC.md); FFmpeg 8.1.2 as a black-box oracle | independent implementation from the specification replaces the former SpanDSP-derived codec; checked-in encoder and decoder vectors record the oracle |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.HuC6280/PcePsg.cs`, `Hawkynt.FileFormats.Audio/Codecs/Codec.HuC6280/CpuHuC6280.cs` | Mednafen `pce_psg.cpp`, `huc6280.cpp` (GPL-2.0-or-later), formerly named as behavioural references | reviewed 2026-10-23: no structural correspondence. Mednafen's PSG uses an 18-bit five-tap LFSR, integer dB tables and frequency caches; PcePsg uses a 17-bit two-tap LFSR, a floating-point taper and an additive LFO. Mednafen's CPU is opcode macros; CpuHuC6280 is a plain switch and shares only instruction-set facts. The Mednafen wording was removed from both files |
| `Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.BcacheFs/BcacheFsRawKey.cs` | bcachefs `get_inc_field()` (GPL-2.0) — the packed-key bit layout | "ports … semantics" means the on-disk bit order, a format fact; the implementation is an independent bit-serial loop |
| `Compression.Core/Dictionary/Nrv2b/Nrv2bBuildingBlock.cs`, `Compression.Core/Dictionary/Nrv2d/Nrv2dBuildingBlock.cs`, `Compression.Core/Dictionary/Nrv2e/Nrv2eBuildingBlock.cs` | UCL `n2b_d.ch`, `n2d_d.c`, `n2e_d.c` (GPL-2.0-or-later) cited for the stream layout | format facts, verified against real UPX output; no code claim |
| `Compression.Core/Dictionary/Aplib/AplibBuildingBlock.cs`, `Compression.Core/BuildingBlocks/DoubleSpaceCompressor.cs`, `Compression.Core/BuildingBlocks/DriveSpaceCompressor.cs`, `Compression.Core/Dictionary/Balz/BalzBuildingBlock.cs`, `Compression.Core/Dictionary/Crush/CrushBuildingBlock.cs`, `Compression.Core/Dictionary/Lizard/LizardBuildingBlock.cs`, `Compression.Core/Dictionary/Lzg/LzgBuildingBlock.cs`, `Compression.Core/Dictionary/Lzap/LzapEncoder.cs`, `Compression.Core/Entropy/ContextMixing/MatchModel.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.ExePackers/WinUpackStream.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.ExePackers/JdpackExecutablePackerHandler.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.ExePackers/AsPackLzDecoder.cs` | "clean-room port of the format", or an explicit "not a port of …" | disclaimers |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Aac/AacHuffmanCodebooks.cs`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Aac/AacScaleFactorBands.cs`, `Compression.NativeUI/Theming/IconSet.cs` | tables transcribed from ISO/IEC 14496-3; the project's own former WPF artwork | constants from the standard; own work |
| `Compression.Core/Deflate/DeflateDecompressor.cs`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Shorten/`, `Hawkynt.FileFormats.Audio/FileFormats/FileFormat.Bik/BikReader.cs`, `Hawkynt.FileFormats.Audio/FileFormats/FileFormat.Asf/AsfDepayloader.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.MpegTs/MpegTsWriter.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Lizard/LizardStream.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Freeze/FreezeStream.cs` | libdeflate's table design (MIT); shorten 3.6 / FFmpeg constants; FFmpeg demuxers and muxers as behaviour oracles; GPL references for Lizard/Freeze behaviour | design ideas, constants and oracles only |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Ym2612/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Ym2151/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Ay8910/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Tracker/AmigaPeriods.cs`, `Hawkynt.Algorithms.Hashing/Algorithms/HamsiSmall.cs`, `Hawkynt.Algorithms.Hashing/Algorithms/Fugue.cs`, `Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.AdvFs/AdvFsReader.cs`, `Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.HfsPlus/HfsPlusDecmpfs.cs` | die-extracted OPN/OPM ROMs (Nuked-OPN2, ymfm, Nuked-OPLL as cross-checks), MAME's AY table, the ProTracker period table, sphlib tables, AdvFS headers (GPL), keramics test data (Apache-2.0) and linux-apfs-rw (GPL) marker rule | constants and on-disk layout facts |

Not listed: the many filesystem and container readers that cite kernel headers, `*progs`
tools, libyal documentation or vendor specifications for field offsets and use the
reference tools as test oracles. Those are format facts (rung 2/3) and make no code claim.

## Heuristic scan for unclaimed ports

Beyond the comment search, product code was scanned for C idioms a transliteration leaves
behind (snake_case locals, `goto` chains, pointer-style index arithmetic). It found
MurmurHash3's tail fall-through in `FastHashes.cs` (public domain, idiomatic) and nothing else
that suggests an undeclared port.
