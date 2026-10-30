# Codec proof audit

A codec whose encoder and decoder share the same mistake passes every round trip and is still wrong. This audit looks for that case. For every codec in `Compression.Core`, and every container that embeds a codec, it records one independent proof, or says plainly that there is none:

- a stream written by a third party decodes byte-exact, and/or
- a third-party decoder accepts our output.

Snapshot: 2026-10-30, `main` at `b3bbf782` plus the open pull requests named below. Re-run the probes before trusting a row that is older than the code it describes.

## How the proofs were made

| Oracle | Used for |
|---|---|
| 7-Zip 26.04 | 7z coders, ZIP methods, CAB, WIM, CHM, `.pmd`, `.lzma` |
| Windows `ntdll` `RtlCompressBuffer` and `cabinet.dll` Compression API | LZNT1, XPRESS, XPRESS Huffman, MSZIP, LZMS |
| `makecab` / `expand` (Windows) | CAB MSZIP and LZX, SZDD |
| Python bindings of the reference libraries | brotli, lz4, zstd, snappy, ncompress, liblzfse, aPLib, LZO, Deflate64, PPMd (pyppmd), liblzma, zlib, bz2, dissect.util, ndspy (LZ10), oead (Yaz0), packbits, Pillow (PackBits decoder) |
| Reference C sources built with gcc | FastLZ, liblzf, BriefLZ, liblzg, LZAV, Lizard, Pithy, ZX0, salvador, apultra, UCL, clzip 1.14, rnc_propack 1.8; revisions in `Compression.Tests/CodecProof/OracleVectors/README.md` |
| Original programs | Dmitry Shkarin's PPMd var.H/var.I, WinRAR 2.90, hwzip 2.4 |

Pinned proofs:
- `OracleVectorTests` decodes streams that the third-party encoders wrote (78 vectors).
- `ArchiveCodecOracleTests` and `CompressionApiOracleTests` re-ask 7-Zip and Windows on every run where the tool is present.

Tests named in the tables are the ones that pin each row.

Status words:
- **both**: their streams decode and they accept ours.
- **decode** / **encode**: only that direction is proven.
- **fixed #n**: wrong on `main`; the pull request makes it real and pins the proof.
- **broken**: wrong today; still open.
- **not a format**: a textbook primitive with no third-party bitstream to match.
- **unproven**: claims a real format, but no proof has been run yet; the reason is given.

## Building blocks

### Proven

| Block | Status | Proof |
|---|---|---|
| `BB_Brotli` | both | brotli 1.2.1 reads ours; `OracleVectorTests` |
| `BB_Bzip2` | both | libbzip2 (CPython `bz2`); `OracleVectorTests` |
| `BB_Deflate` | both | zlib raw inflate; `OracleVectorTests` |
| `BB_Zopfli` | encode | zlib reads ours (decoder is `BB_Deflate`) |
| `BB_Deflate64` | both | inflate64 1.0.4 (zlib `infback9`); `OracleVectorTests` |
| `BB_Lz4`, `BB_Lz4Frame` | both | lz4 4.4.5 block and frame APIs; `OracleVectorTests`, `Lz4ExternalInteropTests` |
| `BB_Zstd` | both | libzstd via zstandard 0.25.0; `OracleVectorTests` |
| `BB_Xz` | both | liblzma; `OracleVectorTests` |
| `BB_Lzma` | encode | liblzma raw LZMA1 decoder reads ours; 7z LZMA in `SevenZipLibarchiveVectorTests` (#596) |
| `BB_Lzc` | both | ncompress (Python binding 1.0.2); `OracleVectorTests` |
| `BB_Snappy` | both | snappy raw format (cramjam 2.14.0); `OracleVectorTests` |
| `BB_Lzo` | both | dissect.util reads ours; LZO 2.10 LZO1X-1 (lzallright) in `OracleVectorTests`; `LzopInteropTests` |
| `BB_Pithy` | both | pithy built from source; `OracleVectorTests` |
| `BB_Zx0` | both | ZX0 2.2 **classic (v1) format** (`zx0 -c` / `dzx0 -c`); the v2 default format is not what this block speaks; `OracleVectorTests` |
| `BB_Salvador` | both | salvador 1.4.2; `OracleVectorTests` |
| `BB_Aplib` | both | apultra 1.4.8 and the Python `aplib` depacker read ours; `OracleVectorTests` |
| `BB_Lzbitmap` | both | dissect.util reads ours; libfshfs HFS+ volumes (`HfsPlusLibfshfsOracleTests`) |
| `BB_UnixPack` | encode | `gzip -d` (pack support) reads ours |
| `BB_Csc` | both | libcsc `csc` (`CscLibcscInteropTests`) |
| `BB_Lzjb`, `BB_Zle` | decode | OpenZFS 2.2.2 pools (`ZfsReferencePoolTests`) |
| `BB_StackerSd4` | both | dmsdos `dcread` (`GenuineStackerCodecOracleTests`) |
| `BB_Lzs`, `BB_DoubleSpace`, `BB_DriveSpace`, `BB_DsLz77` | decode | dmsdos cluster vectors (`CvfReferenceVectorTests`) |
| `BB_KwajLzh` | encode | libmspack `msexpand` (`KwajLibmspackInteropTests`) |
| `BB_Lzx` | both | makecab-written LZX 15/18/21 decode, expand and 7-Zip read ours (`ArchiveCodecOracleTests`); WIM LZX via 7-Zip |
| `BB_Lzms` | both | Windows Compression API (`CompressionApiOracleTests`); WIM LZMS via 7-Zip |
| `BB_Arj` | decode | open-source ARJ port (`ArjMethodVectorTests`, `ArjOracleTests`) |
| `BB_Ace` | both | acefile (`AceTests`) |
| `BB_QuickLz` | decode | vectors from the independent quicklz crate 0.3.1 (`QuickLzTests`) |
| `BB_BcjArm`, `BB_BcjArmThumb`, `BB_BcjArm64`, `BB_BcjPowerPc`, `BB_BcjRiscV` | both | 7-Zip 26.04 7z filters, both ways (probe run on `main`; 7z tests belong to the 7z owner) |
| `BB_Swap2`, `BB_Swap4` | both | 7-Zip `Swap2`/`Swap4` coders, both ways (probe run on `main`) |
| `BB_GbaLz77` | both | ndspy 4.2.0 `lz10` (GBA/DS BIOS LZ77) reads ours; its streams in `OracleVectorTests` |
| `BB_Yaz0` | both | oead 1.3.0 `yaz0` reads ours; its streams in `OracleVectorTests` |
| `BB_PackBits` | both | `packbits` 0.6 and Pillow's TIFF PackBits decoder read ours; `packbits` streams in `OracleVectorTests` |

### Fixed in open pull requests

| Block | PR | What was wrong / proof |
|---|---|---|
| `BB_Lzfse`, `BB_Lzvn` | #620 | Private look-alikes; now the `LzfseStream` codec; Apple lzfse both ways |
| `BB_Xpress` | #598 | XPRESS Huffman terminator and block rules; Windows `RtlDecompressBufferEx` both ways |
| `BB_Quantum` | #611 | Block end one byte late; libmspack vectors, Microsoft's block reproduced, 7-Zip |
| `BB_Implode`, `BB_Reduce`, `BB_Shrink` | #609 | Private variants; PKZIP 1.10/0.92 streams, hwzip, 7-Zip |
| `BB_BcjX86`, `BB_BcjSparc`, `BB_BcjIa64` | #595 | Filter rules; liblzma 5.8.4 conversion and vectors |
| `BB_Rar`, `BB_Rar3` | RAR owner (#553, #560) | Not touched here |
| `BB_Lzf` | #636 | Private variant (back-references one byte short); liblzf 3.6 conversion, byte-identical to liblzf |
| `BB_FastLz` | #637 | Private match encoding, no level 2; FastLZ conversion, byte-identical at levels 1 and 2 |
| `BB_BriefLz`, `BB_Lzg`, `BB_Lizard` | #638 | Private codecs; the blocks are now the proven `BriefLz`, `Lzg` and `Lizard` format codecs |
| `BB_Lzav` | #641 | No offset-carry bits, no final literals; LZAV 5.20 conversion, byte-identical to `lzav_compress_default` |
| `BB_Nrv2b`, `BB_Nrv2d`, `BB_Nrv2e` | #640 | Encoders left out UCL's end-of-stream code; UCL 1.03 now restores every stream we write. Decoders already read UCL's (`OracleVectorTests`) |
| `BB_Ppmd` | #623, then the 7z wiring | `PpmdModelH` is a private model; #623 adds 7-Zip's coder on #560's `Ppmd7` (byte-identical to pyppmd); the block moves over with the 7z wiring |

### Broken, open

None left among the building blocks the audit found broken; see the fixes above.

### Claims a real format, unproven

| Block | Why not yet |
|---|---|
| `BB_842` | No IBM 842 oracle built (Linux `lib/842`, NX hardware) |
| `BB_Yay0` | No Yay0 reference run (oead has Yaz0 only) |
| `BB_NuLzw` | nulib2 not run |
| `BB_Lzh`, `BB_MsLzh`, `BB_Sqx` | Covered only as far as their archive formats' own tests go; not probed as blocks |
| `BB_Lzrw1`, `BB_Lzrw3` | Ross Williams' reference code not run |
| `BB_Balz`, `BB_Crush`, `BB_Zling`, `BB_Density`, `BB_Lzfx` | Reference programs exist (BALZ, CRUSH, zling, density, lzfx); not yet built |
| `BB_Shoco` | Uses a locally trained model, so its output cannot match `shoco`'s default model by construction |

### Named after a codec, explicitly not its format

The descriptions already say these are reduced or "-style" reimplementations, not the named codec's bitstream: `BB_Bcm`, `BB_Bsc`, `BB_Cmix`, `BB_Mcm`, `BB_Paq8hp`, `BB_LZHAM`, `BB_Lzturbo`, `BB_CTW`. Their names invite the same mistake `BB_Lzfse` made. Either give them the real codec or names that do not claim one; that is the owner's call.

### Not a third-party format

Textbook primitives with no external bitstream to match. Round-trip and ratio tests are the right check for these:

`BB_AdaptiveHuffman`, `BB_Arithmetic`, `BB_BPE`, `BB_Bwt`, `BB_ContextMixing`, `BB_ContextTreeWeighting`, `BB_Delta`, `BB_DeltaRle`, `BB_Dmc`, `BB_Dna`, `BB_Dpcm`, `BB_EliasDelta`, `BB_EliasGamma`, `BB_ExpGolomb`, `BB_FSE`, `BB_Fibonacci`, `BB_Fpaq0`, `BB_Golomb`, `BB_GolombFixedM`, `BB_Huffman`, `BB_Levenshtein`, `BB_Lz77`, `BB_Lz77Optimal`, `BB_Lz78`, `BB_Lzap`, `BB_Lzmw`, `BB_Lzp`, `BB_Lzrle`, `BB_Lzss`, `BB_Lzw`, `BB_Lzwl`, `BB_Mtf`, `BB_Neural`, `BB_Omega`, `BB_PPM`, `BB_RangeCoding`, `BB_RePair`, `BB_Rle`, `BB_ROLZ`, `BB_Sequitur`, `BB_ShannonFano`, `BB_SuffixTree`, `BB_Tunstall`, `BB_Unary`, `BB_rANS`, `BB_tANS`.

## Stream formats

| Format | Status | Proof / finding |
|---|---|---|
| `Gzip`, `Zlib`, `Bzip2`, `Xz`, `Zstd`, `Brotli`, `Lz4`, `Snappy` (framed), `Compress` | both | The Python oracles above, both directions |
| `Lzfse` | both | Apple lzfse (pyliblzfse); fixtures in #620 |
| `BriefLz` | both | `blzpack` (jibsen/brieflz); `OracleVectorTests` |
| `Lzg` | both | `lzg`/`unlzg` (liblzg); `OracleVectorTests` |
| `Lizard` | both | `lizard` 2.1.0 CLI; `OracleVectorTests` |
| `Lzip` | both | clzip 1.14 reads ours and we read its; `OracleVectorTests` |
| `Yaz0` | both | oead 1.3.0 `yaz0`; `OracleVectorTests` |
| `Rnc` | encode, partly decode | rnc_propack 1.8 (lab313ru's ProPack reimplementation) unpacks our files; we unpack its method-1 text and zero files (`OracleVectorTests`). Its own unpacker crashes on its mixed and 23-byte outputs, so those prove nothing |
| `UnixPack`, `UuEncoding`, `B64Encoding` | encode | `gzip -d`, `binascii.a2b_uu`, `base64` read ours |
| `Lzma` (`.lzma`) | fixed #635 | We wrote an end marker *and* the uncompressed size. liblzma (CPython `lzma`) rejects that as corrupt; 7-Zip accepts it. LZMA SDK writes no end marker when the size is known. The reader is proven (liblzma-written files decode) |
| `Szdd` | fixed #639 | `expand.exe` decoded our text sample differently (853 bytes wrong). The greedy encoder compared candidates against the ring as it was before the match; it now reads slots the match itself writes, and every encoder avoids the 16 initial slots `expand.exe` (zeros) and libmspack (spaces) disagree about. The decoder was already exact. `SzCompress` shares the code |
| `Ppmd` (`.pmd`) | fixed #627 | Was a private wrapper. Now Shkarin's format: his PPMd var.H/var.I files decode, our files match his byte for byte apart from the time stamp, and 7-Zip extracts them |
| `Lzop`, `QuickLz`, `Density`, `Freeze`, `Squeeze`, `Crunch`, `IcePacker`, `PowerPacker`, `RefPack`, `Rzip`, `Ecm`, `Flac`, `YEnc`, `XxEncoding`, `BinHex`, `MacBinary`, `Kwaj`, `SzCompress`, `Lzs`, `Lzham`, `Bsc`, `Bcm`, `Mcm`, `Paq8`, `Cmix`, `Balz`, `Zling`, `ApLib`, `PackBits` | not probed here | Several have proofs in their own test classes (`Lzop`: `LzopInteropTests`; `Kwaj`: `KwajLibmspackInteropTests`; `Ecm` (#489)). The rest are unproven by this audit |

## Containers with embedded codecs

| Container | Status | Proof |
|---|---|---|
| ZIP | both | Deflate, Deflate64, BZip2, Copy, LZMA read (7-Zip writes); Deflate, Deflate64, BZip2, XZ, Zstd written (7-Zip reads): `ArchiveCodecOracleTests`. Shrink, Reduce, Implode #609; LZMA flag #610; PPMd (method 98) #559 |
| CAB | both | makecab MSZIP and LZX decode; expand and 7-Zip read our MSZIP and LZX (`ArchiveCodecOracleTests`); Quantum #611 |
| WIM | encode | 7-Zip reads our XPRESS, LZX, LZMS and stored WIMs (`ArchiveCodecOracleTests`); readers: `WimReferenceReaderTests` |
| 7z | owner: 7z agent | LZMA sizes #596, BCJ filters #595, PPMd coder #623; conformance matrix #619 |
| RAR | owner: RAR agent | RAR 1.5/2.0 #553/#572; RAR 2.9 #560 |
| CHM | owner: #588 | Rewritten against `7-zip.chm`; `hh.exe -decompile` also reads its output (train note `fixes/588.md`) |
| NTFS LZNT1 | both | #597 (Windows `RtlCompressBuffer`) |
| `.pmd` | both | #627 (Shkarin's PPMd, 7-Zip) |
