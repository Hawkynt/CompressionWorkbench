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
| A | 38 | |
| B | 24 | FFmpeg alone covers 38 codec folders |
| C | 1 | specification: `docs/FDS-EXPANSION-SOUND-SPEC.md` |
| R | 12 | |

## C — incompatible, must be rewritten clean-room

| Claim site | Upstream | Licence | Specification for the rewrite | Status |
|---|---|---|---|---|
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
| `Compression.Core/Transforms/BcjFilter.cs` (x86, ARM-Thumb, ARM64, SPARC, IA-64, RISC-V) | XZ Utils liblzma `simple/x86.c`, `armthumb.c`, `arm64.c`, `sparc.c`, `ia64.c`, `riscv.c` | 0BSD | `Compression.Core/Transforms/THIRD-PARTY-NOTICE.liblzma.txt` |
| `Compression.Core/Dictionary/Zx0/Zx0BuildingBlock.cs` | ZX0 (Einar Saukas) `compress.c` stream format | BSD-3-Clause | `Compression.Core/Dictionary/Zx0/THIRD-PARTY-NOTICE.zx0.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Ghost/` | `nyarime/gho` (Go) | MIT | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Ghost/THIRD-PARTY-NOTICE.gho.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Macrium/MacriumPreXFormatDescriptor.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Macrium/MacriumPreXCodec.cs` | `ccooper21/mrimg-tools` (Christopher Cooper) | MIT | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Macrium/THIRD-PARTY-NOTICE.mrimg-tools.txt` |
| `Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.Hammer/HammerWriter.cs`, `Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.Hammer2/Hammer2Writer.cs` | DragonFly BSD `newfs_hammer`, `newfs_hammer2` and their on-disk helpers | BSD-3-Clause | `THIRD-PARTY-NOTICE.dragonfly.txt` in each folder |
| `Compression.Core/Dictionary/Shoco/ShocoBuildingBlock.cs`, `Compression.Core/Dictionary/Shoco/ShocoModel.cs` | shoco `shoco.c`, `shoco_model.h` (Christian Schramm), revision `7c92fa9c4161ea97da90cc78e38cfb18476ba882` | MIT | `Compression.Core/Dictionary/Shoco/THIRD-PARTY-NOTICE.shoco.txt` |
| `Compression.Core/Dictionary/Density/DensityCodec.cs` | density-rs 0.16.6 (Guillaume Voirin), revision `58938943bb0dfd85b186551a3d1d36cc286f9c1d` | MIT OR Apache-2.0 (used as MIT) | `Compression.Core/Dictionary/Density/THIRD-PARTY-NOTICE.density.txt` |
| `Compression.Core/Dictionary/Zling/ZlingCodec.cs` | libzling (Zhang Li), revision `6828e31517bf94fda8e1e8e15a583ab58acde637` | BSD-3-Clause | `Compression.Core/Dictionary/Zling/THIRD-PARTY-NOTICE.libzling.txt` |
| `Compression.Core/Dictionary/Balz/BalzCodec.cs` | Ilya Muravyov `balz.cpp` (BALZ 1.20) | public domain | `Compression.Core/Dictionary/Balz/THIRD-PARTY-NOTICE.BALZ.txt` |
| `Compression.Core/Dictionary/Crush/CrushCodec.cs` | Ilya Muravyov `crush.cpp` (CRUSH 1.00) | public domain | `Compression.Core/Dictionary/Crush/THIRD-PARTY-NOTICE.CRUSH.txt` |
| `Compression.Core/Dictionary/Lzrw3/` | Ross N. Williams `lzrw3.c` (release 1, 30-Jun-1991) | public domain | `Compression.Core/Dictionary/Lzrw3/THIRD-PARTY-NOTICE.LZRW3.txt` |
| `Compression.Core/Dictionary/Lzrw1/` | Ross N. Williams `lzrw1.c` (31-Mar-1991) | public domain | `Compression.Core/Dictionary/Lzrw1/THIRD-PARTY-NOTICE.LZRW1.txt` |

## A — compatible, notice already in place

| Claim site | Upstream | Licence | Where the notice is |
|---|---|---|---|
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.CompactPro/CompactProCodec.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.CompactPro/CompactProReader.cs` | XADMaster `XADCompactProParser.m`, `XADCompactProRLEHandle.m`, `XADCompactProLZHHandle.m`, fetched 2026-10-19 as recorded in the notice | LGPL-2.1-or-later | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.CompactPro/THIRD-PARTY-NOTICE.XADMaster.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.FreeArc/FreeArcGrzip.cs` | libGRZip (Ilya Grebnov), the copy in FreeArc 0.67 `Compression/GRZip` | LGPL-2.1-or-later | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.FreeArc/THIRD-PARTY-NOTICE.GRZip.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.FreeArc/FreeArcPpmd.cs` | PPMd var.I (Dmitry Shkarin) and the carryless range coder (Dmitry Subbotin), the copy in FreeArc 0.67 `Compression/PPMD` | public domain | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.FreeArc/THIRD-PARTY-NOTICE.PPMd.txt` |
| `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.Trx/TrxImage.cs`, `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.Seama/SeamaImage.cs` | binwalk v3 TRX, SEAMA and DLOB header layouts, revision `26713972e3f9f52dc37d4a421c4554b0bd9e82ef` | MIT | `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.Trx/THIRD-PARTY-NOTICE.binwalk.txt`, `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.Seama/THIRD-PARTY-NOTICE.binwalk.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Uc2/` | klauspost/uc2, revision `53622f89ada1cf0d4a69f91b653bdd44ec5b0f92`, format parsing and Ultra/Turbo decoding; UC2 supermaster dictionary from the same revision | LGPL-3.0 | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Uc2/THIRD-PARTY-NOTICE.UltraCompressor2.txt` |
| `Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.Fossil/FossilReader.cs` | plan9port Fossil `pack.c`, `vac.c`, `vac.h`, `source.c`, `file.c`, `dat.h`, revision `1f1396913fb5372408f442ed26933f5ae4b399cd` | MIT | `Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.Fossil/THIRD-PARTY-NOTICE.Fossil.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.StuffIt/` | XADMaster StuffIt parsing and fork decoding, revision `7cb9ee0abbb163f261e4cb74501e15067032319c`, file list in the notice | LGPL-2.1-or-later | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.StuffIt/THIRD-PARTY-NOTICE.XADMaster.txt` |
| `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.AndroidBootImage/AndroidBootImage.cs`, `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.AndroidBootImage/AndroidBootImageMetadata.cs` | AOSP mkbootimg `bootimg.h`, `mkbootimg.py` and `unpack_bootimg.py`, revision `d2bb0af5ba6d3198a3e99529c97eda1be0b5a093` | BSD-3-Clause and Apache-2.0 | `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.AndroidBootImage/THIRD-PARTY-NOTICE.mkbootimg.txt` |
| `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.AndroidVbmeta/AvbVbmeta.cs`, `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.AndroidVbmeta/AvbDescriptorText.cs` | AOSP Android Verified Boot headers and `avbtool.py`, revision `c5066a96caa7bf4150c0a8cc8cc14ab81733fdc7` | MIT | `Hawkynt.FileFormats.FileSystems/FileFormats/FileFormat.AndroidVbmeta/THIRD-PARTY-NOTICE.avb.txt` |
| `Compression.Core/Dictionary/KwajLzh/`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Kwaj/KwajStream.cs` | kwaj-lzh `src/kwaj.ts`, `src/bitreader.ts`, `src/lzss.ts` (yaodub), revision `f71544bc4f848420d8aedb46c2ae602b88a3e998` | MIT | `Compression.Core/Dictionary/KwajLzh/THIRD-PARTY-NOTICE.kwaj-lzh.txt`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Kwaj/THIRD-PARTY-NOTICE.kwaj-lzh.txt` |
| `Compression.Core/Dictionary/Ace/`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Ace/AceReader.cs` | acefile `acefile.py` (Daniel Roethlisberger and contributors), revision `11ed076ae19e8228148731cfbc05f019a395b3b9` | BSD-2-Clause | `Compression.Core/Dictionary/Ace/THIRD-PARTY-NOTICE.acefile.txt`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Ace/THIRD-PARTY-NOTICE.acefile.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Dms/DmsDecoder.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Dms/DmsReader.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Dms/DmsRecords.cs` | libxad DMS client `libxad/clients/DMS.c` (Dirk Stöcker), DMS 1.13 (2006-05-31) | LGPL-2.1-or-later | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Dms/THIRD-PARTY-NOTICE.libxad.txt` |
| `Compression.Core/Dictionary/Lzbitmap/` | libzbitmap (Ernesto A. Fernández, Corellium) | MIT | `THIRD-PARTY-NOTICE.libzbitmap.txt` |
| `Compression.Core/Dictionary/Csc/` | libcsc (Fu Siyuan) | Unlicense | `THIRD-PARTY-NOTICE.libcsc.txt` |
| `Compression.Core/Dictionary/Lzav/LzavCodec.cs` | LZAV 5.20 `lzav.h` (Aleksey Vaneev), revision `b93c884dab90ac16112f78daed9ff6e07c3d2fac` | MIT | `Compression.Core/Dictionary/Lzav/THIRD-PARTY-NOTICE.lzav.txt` |
| `Compression.Core/Dictionary/FastLz/FastLzCompressor.cs`, `Compression.Core/Dictionary/FastLz/FastLzDecompressor.cs` | FastLZ `fastlz.c` (Ariya Hidayat), revision `b1342dabcf5257ab303743c9332fe75e9147a011` | MIT | `Compression.Core/Dictionary/FastLz/THIRD-PARTY-NOTICE.FastLZ.txt` |
| `Compression.Core/Dictionary/Lzf/LzfCompressor.cs`, `Compression.Core/Dictionary/Lzf/LzfDecompressor.cs` | liblzf 3.6 `lzf_c.c`, `lzf_d.c` (Marc Lehmann) | BSD-2-Clause (dual BSD/GPL-2.0, taken under BSD) | `Compression.Core/Dictionary/Lzf/THIRD-PARTY-NOTICE.liblzf.txt` |
| `Compression.Core/Dictionary/Zip/ShrinkDecoder.cs`, `Compression.Core/Dictionary/Zip/ShrinkEncoder.cs`, `Compression.Core/Dictionary/Zip/ReduceDecoder.cs`, `Compression.Core/Dictionary/Zip/ReduceEncoder.cs`, `Compression.Core/Dictionary/Zip/ImplodeDecoder.cs`, `Compression.Core/Dictionary/Zip/ImplodeEncoder.cs`, `Compression.Core/Dictionary/Zip/ZipLegacyBitStream.cs` | hwzip 2.4 `shrink.c`, `reduce.c`, `implode.c`, `bitstream.h` (Hans Wennborg) | public domain | `Compression.Core/Dictionary/Zip/THIRD-PARTY-NOTICE.hwzip.txt` |
| `Compression.Core/Dictionary/Arj/ArjFastestDecoder.cs` | XADMaster `XADARJFastestHandle.m` (Dag Ågren, MacPaw), revision `7cb9ee0abbb163f261e4cb74501e15067032319c` | LGPL-2.1-or-later | `Compression.Core/Dictionary/Arj/THIRD-PARTY-NOTICE.XADMaster.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Zpaq/` | libzpaq / zpaq 7.15 (Matt Mahoney) | public domain (Unlicense) | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Zpaq/THIRD-PARTY-NOTICE.libzpaq.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.DiskDoubler/DiskDoublerReader.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.DiskDoubler/DiskDoublerAdDecoder.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.DiskDoubler/DiskDoublerCodecs.cs` | XADMaster `XADDiskDoublerParser.m`, `XADDiskDoublerADnHandle.m`, `XADDiskDoublerMethod2Handle.m`, `XADStacLZSHandle.m` and `XADDeltaHandle.m`, revision `7cb9ee0abbb163f261e4cb74501e15067032319c` | LGPL-2.1-or-later | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.DiskDoubler/THIRD-PARTY-NOTICE.XADMaster.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.InstallerVise/` | timdoug/unvise, revision `01b08a1455792fc6268127ad9f67f9bb5130d8da`: transport, resource map, substitution-table recovery, VISE DEFLATE normalisation, catalog layouts and extraction | MIT | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.InstallerVise/THIRD-PARTY-NOTICE.unvise.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.ClickteamInstaller/ClickteamReader.cs` | Bioruebe/cicdec 3.0.1, revision `42c3c9d030d75ed9096e3c84ef5ffd19bde51b37`: block walk, file list layouts 2.0-4.0 and member encoding | BSD-3-Clause | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.ClickteamInstaller/THIRD-PARTY-NOTICE.cicdec.txt` |
| `Compression.Core/Dictionary/Lzh/Lh2Decoder.cs`, `Compression.Core/Dictionary/Lzh/Lh3Decoder.cs`, `Compression.Core/Dictionary/Lzh/LhaLegacyBitReader.cs` | XADMaster `XADLZHOldHandles.m`, revision `7cb9ee0abbb163f261e4cb74501e15067032319c` | LGPL-2.1-or-later | `Compression.Core/Dictionary/Lzh/THIRD-PARTY-NOTICE.XADMaster.txt` |
| `Compression.Core/Dictionary/Lzh/Lh1Tree.cs`, `Compression.Core/Dictionary/Lzh/Lh1Decoder.cs`, `Compression.Core/Dictionary/Lzh/LzsDecoder.cs`, `Compression.Core/Dictionary/Lzh/Lz5Decoder.cs`, `Compression.Core/Dictionary/Lzh/Pm1Decoder.cs`, `Compression.Core/Dictionary/Lzh/Pm2Decoder.cs`, `Compression.Core/Dictionary/Lzh/PmaHistory.cs`, `Compression.Core/Dictionary/Lzh/LhaBitInput.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Lzh/LhaHeaderParser.cs`, `Compression.Core/Dictionary/Lzh/LzhDecoder.cs` (LHARK codes), `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Lzh/LhaReader.cs` (self-extractor search) | lhasa (Simon Howard), revision `75ed83559f23e9538e0045c62f53f77ab03d03d6` | ISC | `Compression.Core/Dictionary/Lzh/THIRD-PARTY-NOTICE.lhasa.txt`; `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Lzh/THIRD-PARTY-NOTICE.lhasa.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.InnoSetup/` | innoextract 1.10-dev (Daniel Scharrer), master as of 2026-10-19; layouts 6.4.2 to 7.0.0.3 from Inno Setup's own `Shared.Struct.pas`, `Setup.MainFunc.pas`, `Compression.Base.pas`, tags `is-6_4_2` through `is-7_1_0` | zlib (innoextract); Inno Setup License | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.InnoSetup/THIRD-PARTY-NOTICE.InnoSetup.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Nsis/` | NSIS `Source/exehead/fileform.h`, `fileform.c`, `util.c`, `Source/bzip2/decompress.c` (master as of 2026-10-19); 7-Zip `CPP/7zip/Archive/Nsis/NsisIn.cpp`, `NsisDecode.cpp` | zlib/libpng (NSIS); LGPL-2.1-or-later (7-Zip) | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Nsis/THIRD-PARTY-NOTICE.NSIS.txt` |
| `Compression.Core/Streams/Lzfse/` | Apple lzfse reference | BSD-3-Clause | `THIRD-PARTY-NOTICE.lzfse.txt`; `LzfseFseDecoder.cs` also carries the header inline |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Nellymoser/NellymoserCodec.cs`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Nellymoser/NellymoserTables.cs` | FFmpeg `nellymoserdec.c`, `nellymoser.c` | MIT | `THIRD-PARTY-NOTICES.md` § Nellymoser |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Alac/` | Apple ALAC reference | Apache-2.0 | `THIRD-PARTY-NOTICES.md` § ALAC |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Gsm610/` | GSM 06.10 reference (Degener, Bormann, TU Berlin) | permissive, notice must stay | heads `Gsm610Codec.cs`; now also in `THIRD-PARTY-NOTICES.md` |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Mp3/` | minimp3 (`lieff/minimp3` 7b590fdc) — and, for `Mp3Encoder.Managed.cs`, the vendored GroovyCodecs | CC0-1.0; LGPL-3.0 | CC0 needs none; GroovyCodecs in `THIRD-PARTY-NOTICES.md` and `VENDORED_AUDIO_CODECS.md` |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Lpc10/Lpc10Tables.cs` | U.S. DoD LPC-10 reference tables | public domain | none needed |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Vorbis/VorbisCodec.cs` | stb_vorbis 1.22 (structural reference) | public domain / MIT | none needed |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Ba2/BethesdaLookup3.cs`, `Compression.Core/Entropy/Ppmd/PpmdModelH.cs` | Bob Jenkins lookup3; Shkarin's PPMd rescale constant | public domain / constants | none needed |
| `Hawkynt.FileFormats.Audio/Codecs/VENDORED_AUDIO_CODECS.md` | Concentus, .NET Ogg Vorbis Encoder, GroovyCodecs, AmrWbLib (vendored whole) | BSD-3 / MIT / LGPL-3.0 / BSD-3 | `THIRD-PARTY-NOTICES.md` and the vendored `LICENSE.txt` files |
| `Compression.Core/Dictionary/PkwareDcl/`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Mpq/MpqHuffman.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Mpq/MpqAdpcm.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Mpq/MpqSparse.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Mpq/MpqCompression.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Mpq/MpqCrypto.cs` | StormLib (Ladislav Zezula) `pklib/explode.c`, `pklib/implode.c`, `huffman/huff.cpp`, `adpcm/adpcm.cpp`, `sparse/sparse.cpp`, `SCompression.cpp`, `SBaseCommon.cpp`, revision `116135c27e8c0dcf8ea0a0c549a6d737ce10fc73` | MIT | `Compression.Core/Dictionary/PkwareDcl/THIRD-PARTY-NOTICE.StormLib.txt`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Mpq/THIRD-PARTY-NOTICE.StormLib.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Aomei/AomeiFormatDescriptor.cs` | this repository's reverse-engineered AOMEI Backupper container layout | own work | none needed |
| `Compression.Core/FileSystems/SquashFs/SquashFsLegacyReader.cs` | 7-Zip 23.01 `CPP/7zip/Archive/SquashfsHandler.cpp` (Igor Pavlov) | LGPL-2.1-or-later — taken under LGPL-3.0 | `Compression.Core/FileSystems/SquashFs/THIRD-PARTY-NOTICE.7-Zip.txt` |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.B1/B1Reader.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.B1/B1Writer.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.B1/B1Serialization.cs` | b1-pack (b1.org), revision `eb2c91854082c37d1f3fcc4edcc7bdfd5806dfc1` | Apache-2.0 | `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.B1/THIRD-PARTY-NOTICE.b1-pack.txt` |

## R — not a code port

| Claim site | What the comment refers to | Note |
|---|---|---|
| `Compression.Core/Dictionary/Ibm842/Ibm842Codec.cs` | [`IBM842-ON-DISK.md`](IBM842-ON-DISK.md); Linux `lib/842` (GPL-2.0) read as a format description and run in user space as a black-box oracle | independent implementation from the description; the operation table is copied as a constant; kernel-written vectors pin it |
| `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Rzip/RzipStream.cs` | [`RZIP-ON-DISK.md`](RZIP-ON-DISK.md); rzip 2.1 (GPL-2.0-or-later) read as a format description and built as a black-box oracle | independent implementation from the description; rzip-written files and rzip round trips pin it |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.TrackerXmIt/ItSampleDecompressor.cs` | [`IT-SAMPLE-COMPRESSION-SPEC.md`](IT-SAMPLE-COMPRESSION-SPEC.md); libxmp 4.6.3 and libopenmpt as black-box oracles | independent rewrite from the specification replaces the former Schism Tracker-derived decoder; checked-in IT214/IT215 modules and libxmp PCM digests record the oracle |
| `Compression.Core/FileSystems/Cvf/CvfLzCodec.cs`, `Compression.Core/FileSystems/Cvf/MsdblLzCodec.cs`, `Compression.Core/FileSystems/Cvf/Sd4Codec.cs` | the measured format description in [`CVF-CODECS-ON-DISK.md`](CVF-CODECS-ON-DISK.md); dmsdos (GPL-2.0) as a black-box oracle | independent rewrite from the format description and reference vectors replaces the former dmsdos-derived bit reader and DS/JM decoders |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.G722/G722Codec.cs` | ITU-T G.722 (09/2012), via [`G722-SPEC.md`](G722-SPEC.md); FFmpeg 8.1.2 as a black-box oracle | independent implementation from the specification replaces the former SpanDSP-derived codec; checked-in encoder and decoder vectors record the oracle |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.HuC6280/PcePsg.cs`, `Hawkynt.FileFormats.Audio/Codecs/Codec.HuC6280/CpuHuC6280.cs` | Mednafen `pce_psg.cpp`, `huc6280.cpp` (GPL-2.0-or-later), formerly named as behavioural references | reviewed 2026-10-23: no structural correspondence. Mednafen's PSG uses an 18-bit five-tap LFSR, integer dB tables and frequency caches; PcePsg uses a 17-bit two-tap LFSR, a floating-point taper and an additive LFO. Mednafen's CPU is opcode macros; CpuHuC6280 is a plain switch and shares only instruction-set facts. The Mednafen wording was removed from both files |
| `Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.BcacheFs/BcacheFsRawKey.cs` | bcachefs `get_inc_field()` (GPL-2.0) — the packed-key bit layout | "ports … semantics" means the on-disk bit order, a format fact; the implementation is an independent bit-serial loop |
| `Compression.Core/Dictionary/Nrv2b/Nrv2bBuildingBlock.cs`, `Compression.Core/Dictionary/Nrv2d/Nrv2dBuildingBlock.cs`, `Compression.Core/Dictionary/Nrv2e/Nrv2eBuildingBlock.cs` | UCL `n2b_d.ch`, `n2d_d.c`, `n2e_d.c` (GPL-2.0-or-later) cited for the stream layout | format facts, verified against real UPX output; no code claim |
| `Compression.Core/Dictionary/Aplib/AplibBuildingBlock.cs`, `Compression.Core/BuildingBlocks/DoubleSpaceCompressor.cs`, `Compression.Core/BuildingBlocks/DriveSpaceCompressor.cs`, `Compression.Core/Dictionary/Lzap/LzapEncoder.cs`, `Compression.Core/Entropy/ContextMixing/MatchModel.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.ExePackers/WinUpackStream.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.ExePackers/JdpackExecutablePackerHandler.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.ExePackers/AsPackLzDecoder.cs` | "clean-room port of the format", or an explicit "not a port of …" | disclaimers |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Aac/AacHuffmanCodebooks.cs`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Aac/AacScaleFactorBands.cs`, `Compression.NativeUI/Theming/IconSet.cs` | tables transcribed from ISO/IEC 14496-3; the project's own former WPF artwork | constants from the standard; own work |
| `Compression.Core/Deflate/DeflateDecompressor.cs`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Shorten/`, `Hawkynt.FileFormats.Audio/FileFormats/FileFormat.Bik/BikReader.cs`, `Hawkynt.FileFormats.Audio/FileFormats/FileFormat.Asf/AsfDepayloader.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.MpegTs/MpegTsWriter.cs`, `Compression.Core/Streams/Lizard/LizardStream.cs`, `Hawkynt.FileFormats.Archives/FileFormats/FileFormat.Freeze/FreezeStream.cs` | libdeflate's table design (MIT); shorten 3.6 / FFmpeg constants; FFmpeg demuxers and muxers as behaviour oracles; GPL references for Lizard/Freeze behaviour | design ideas, constants and oracles only |
| `Hawkynt.FileFormats.Audio/Codecs/Codec.Ym2612/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Ym2151/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Ay8910/`, `Hawkynt.FileFormats.Audio/Codecs/Codec.Tracker/AmigaPeriods.cs`, `Hawkynt.Algorithms.Hashing/Algorithms/HamsiSmall.cs`, `Hawkynt.Algorithms.Hashing/Algorithms/Fugue.cs`, `Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.AdvFs/AdvFsReader.cs`, `Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.HfsPlus/HfsPlusDecmpfs.cs` | die-extracted OPN/OPM ROMs (Nuked-OPN2, ymfm, Nuked-OPLL as cross-checks), MAME's AY table, the ProTracker period table, sphlib tables, AdvFS headers (GPL), keramics test data (Apache-2.0) and linux-apfs-rw (GPL) marker rule | constants and on-disk layout facts |

Not listed: the many filesystem and container readers that cite kernel headers, `*progs`
tools, libyal documentation or vendor specifications for field offsets and use the
reference tools as test oracles. Those are format facts (rung 2/3) and make no code claim.

## Heuristic scan for unclaimed ports

Beyond the comment search, product code was scanned for C idioms a transliteration leaves
behind (snake_case locals, `goto` chains, pointer-style index arithmetic). It found
MurmurHash3's tail fall-through in `FastHashes.cs` (public domain, idiomatic) and nothing else
that suggests an undeclared port.
