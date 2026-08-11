# Symantec / Norton Ghost — Format Spec Recovered From Ghidra Decompilation

Status: **partial spec confirmed by headless Ghidra decompilation of Ghost
Explorer 2003.789**
Vendor: Symantec Corporation (originally Binary Research, Auckland, NZ)
Product: Norton Ghost Explorer 2003.789 (free, distributed by Symantec)
Binary analysed:

| Binary | Size | Functions | Role |
|---|---|---|---|
| `Ghostexp.exe` (i386 PE32, GUI) | 761,856 bytes | 3,696 | User-mode `.gho` / `.ghs` reader + writer + GUI |

Source: `Ghost Explorer.zip` (412,718 bytes), archive.org item
`norton-ghost-explorer-version-2003.789`.

Pipeline: `tools/ghidra-pipeline/decompile.sh` (Ghidra 11.2.1 headless +
Jython post-script) — the same pipeline that produced
`docs/AOMEI_FORMAT_SPEC.md`.

The file version-info resource confirms:

```
CompanyName       Symantec Corporation
FileDescription   Norton Ghost Explorer
FileVersion       2003.789
ProductVersion    2003.789
LegalCopyright    Copyright (C) 1998-2003 Symantec Corp. All rights reserved.
```

---

## 1. BR (Binary Research) shared-codebase hypothesis — CONFIRMED

### 1.1 Direct string evidence

Ghost Explorer 2003.789 contains the literal ASCII strings
`"BinaryResearch"` (at `0x00488a30`) and `"NORTON GHOST"` (at `0x00488834`).
Symantec acquired Binary Research's Ghost in 1998 and the company name is
embedded verbatim in the user-facing strings table alongside the product
codename.

### 1.2 Cross-tool codec confirmation: Fast LZ sentinel `"123456789012345678"`

The literal ASCII string `"123456789012345678"` lives at `0x0048a978` and
is referenced by **two** decompiled functions:

- `FUN_0042a7a0` — Fast LZ encoder (used when adding files to an existing
  image)
- `FUN_0042ab40` — Fast LZ decoder (used when reading an existing image)

Both functions seed the 4096-entry hash table by writing the pointer
`s_123456789012345678_0048a978` into every slot (256 iterations × 16
entries). This is **byte-for-byte the same hash-table init** documented in
the independent `nyarime/gho` Ghost 11.5.1 reverse-engineering and ported
into `FileFormat.Ghost/GhostFastLz.cs` in this codebase.

### 1.3 Cross-tool codec confirmation: hash multiplier `-24993` / `0x9E5F`

Both decompiled codec routines use the same 3-byte rolling hash:

```c
// FUN_0042ab40 line 86 (decoder)
puVar13[(int)((((uint)pbVar10[-2] << 4 ^ (uint)pbVar10[-1]) << 4 ^ (uint)*pbVar10) * -0x61a1) >> 4 & 0xfff]

// FUN_0042a7a0 line 119 (encoder)
uVar10 = (int)((((uint)*pbVar6 << 4 ^ (uint)pbVar6[1]) << 4 ^ (uint)pbVar6[2]) * 0x9e5f) >> 4 & 0xfff;
```

The signed constant `-0x61a1` and the unsigned constant `0x9e5f` are equal
modulo 2^32 (`0xFFFF9E5F` vs `0x00009E5F` — only the sign-extension
differs, the low 32 bits of the multiplication wrap to the same hash
index). `0x9E5F = 40543` is the **exact constant** documented in
`GhostFastLz.Hash` (the decoder side uses `-24993` for the multiplication).

### 1.4 Cross-tool codec confirmation: 0x01 escape, control-word, match encoding

`FUN_0042ab40` (decoder) line 31:

```c
if (*pcVar4 == '\x01') {
    // copy raw bytes literally
```

The **first byte 0x01 raw-marker escape** is the same as
`GhostFastLz.Decompress`'s `if (data[0] == 1)` short-circuit.

Lines 71-72 read the 16-bit control word LSB-first:

```c
param_3 = *pbVar16 | 0x10000 | (uint)pbVar16[1] << 8;
```

Lines 91-99 decode each match as a 2-byte token `(bVar3, bVar2)`:

```c
uVar14 = bVar3 & 0xf;                                  // extra length (0..15)
pbVar15 = (byte *)puVar13[(bVar3 & 0xf0) << 4 | (uint)bVar2]; // hash table index → match pointer
*pbVar10 = *pbVar15;
pbVar10[1] = pbVar15[1];
pbVar10[2] = pbVar15[2];                                // minimum 3-byte copy
```

This **matches `GhostFastLz.Decompress` lines 99-117 byte-for-byte**: the
match length is `3 + (b0 & 0xF)`, the 12-bit hash index is
`b1 | ((b0 & 0xF0) << 4)`, and the look-up returns the absolute output
position previously stored.

### 1.5 Record container framing constant `0x012F18D8`

The 32-bit record magic `0x012F18D8` appears verbatim in **eight**
decompiled functions, e.g.

```c
// FUN_00421fd0 — record-writer for one record-type
local_4a4 = 0x12f18d8;     // line 49
local_4a8 = 0x1d;          // line 45 — record type byte
...
iVar7 = (**(code **)(*param_1 + 0xc))(&local_4a8, 1, 10);   // writes 10-byte header
```

Ten-byte header layout `[4-byte type | 4-byte magic | 2-byte body length]`
is **the same `RecordHeaderSize = 10`** documented in
`GhostConstants.RecordHeaderSize`. The record-magic field at offset +4 is
the **same 0x012F18D8** documented in `GhostConstants.RecordMagic`. Type
codes seen so far in this binary:

| Type | Source | Meaning (from context) |
|------|--------|-------------------------|
| `0x06` | `RecordTypeTrack0` (GhostConstants) | Track 0 / MBR descriptor |
| `0x1D` | `FUN_00421fd0:45` | Per-partition descriptor (sub-type) |
| `0x1E` | `FUN_00422260:85` | Embedded-file fragment record |
| `0x23` | `FUN_00411e40:216`, `RecordTypeEnd` (GhostConstants) | End-of-image marker |
| `0x603` | `RecordTypePartition` (GhostConstants) | Partition data record |
| `0x703` | `RecordTypeContinuation` (GhostConstants) | Continuation / span record |
| `0x803` | `FUN_00422260:104` | Per-file CRC trailer (20-byte body) |

The end-record write at `FUN_00411e40:216` uses size `0x18` (24 bytes
including the 10-byte header). This matches the modern Ghost 11.x record
container exactly.

---

## 2. Compression dispatch

`FUN_0042948e` (`CompressionStream::Read` style) is the single dispatch
point for decompression. The first byte at object offset +8 selects:

| Byte | Path | Error string (if rejected) |
|------|------|----------------------------|
| `0` | passthrough — store uncompressed | (none) |
| `1` | **Old compression NOT supported** | `"Old compression not supported"` |
| `2` | **Fast** → `FUN_0042a730 → FUN_0042ab40` (Fast LZ decoder) | `"Fast decompression error: expected %d bytes, got %d"` |
| `≥3` | **High** → `FUN_0042a6a0` (zlib inflate, statically linked) | `"High Decompression error"` |

The "Old" code path is **deliberately rejected** at run-time — `FUN_0042948e`
calls the error logger and returns 0 without attempting to decompress.
This explains the `"This image file has been created with a version of
Ghost earlier than 3.0"` user-facing string at `0x004c3d1a`: Ghost
Explorer 2003.789 actively **refuses** to read pre-3.0 images.

The PKWARE DCL copyright string
`"PKWARE Data Compression Library for Win32 ... Version 1.11"` at
`0x0048ad88` is present in the data section but **no function in this
binary references it directly** — PKWARE DCL was the original "Old"
compression for Ghost ≤ 2.x and the library's object files are still
linked in (the copyright was a license requirement), but the entry
points are no longer called. This corroborates the deliberate
"Old compression not supported" rejection above.

`FUN_0042a6a0` is a 2 KB+ zlib inflate state machine — confirmed by the
embedded zlib version strings at `0x0048aff8`
(`" deflate 1.0.4 Copyright 1995-1996 Jean-loup Gailly "`) and
`0x0048b538` (`" inflate 1.0.4 Copyright 1995-1996 Mark Adler "`). zlib
1.0.4 is statically linked.

Compression-mode labels in the GUI are confirmed by the unicode strings
at `0x004c6348` (`"Fast"`), `0x004c6352` (`"High"`), `0x004c635c`
(`"Very high"`) — matching the modern compression byte mapping
`Fast = 2`, `High = 3..6`, `Very high = 7..9` already documented in
`GhostConstants.Compression*`.

---

## 3. CRC algorithms

### 3.1 Per-record CRC-32 (big-endian table-driven)

`FUN_0041bca0` is the data-CRC routine, called via `FUN_0041bd60`:

```c
// big-endian style: input byte XOR'd with high byte of state
param_1 = param_1 << 8 ^ *(uint *)(&DAT_00489378 + ((uint)*(byte *)param_2 ^ param_1 >> 0x18) * 4);
```

The four-byte-at-a-time fast path uses a separate table at
`DAT_00489778` (slicing-by-4). This is a CRC-32 over each data record
body, accumulated across the entire file and written into the trailing
`0x803` record (20-byte body, two `param_14 = uVar8; param_15 = uVar8`
fields holding the running checksum at offsets +12 and +16). The
user-facing error string at `0x004c4ff8`
(`"The CRC values did not match when restoring this file. The file is
corrupted."`) is surfaced when this CRC fails.

### 3.2 CRC-16 stream cipher (encryption)

`FUN_0041bdd0` is a CRC-16-XMODEM keyed stream cipher:

```c
bVar2 = *(byte *)(iVar1 + param_1) ^ (byte)param_3;
*(byte *)(iVar1 + param_1) = bVar2;
param_3 = (&DAT_00490338)[(uint)(param_3 >> 8) ^ (uint)bVar2] ^ param_3 << 8;
```

State is the 16-bit CRC seed (derived from password via `FUN_0041bc60`),
each byte is XOR'd with the low byte of state then state is advanced
through the standard CRC-16-CCITT/XMODEM table at `DAT_00490338`. This is
the **same cipher** documented in `FileFormat.Ghost/GhostCrc16Cipher.cs`.
Encryption is gated on byte 12, bit 1 of the file header — the encrypt /
non-encrypt branch in record writers is
`FUN_0041ff30(...) → if((char)uVar7 != '\0') FUN_0041bdd0(payload, len, password_state)`.

---

## 4. Partition type taxonomy

RTTI strings recovered from the binary expose the partition-class
hierarchy:

```
Partition (abstract base)
├── FATPartition          (Fat12, Fat12 (H), Fat16, Fat16 (H), Fat32, Fat32 (H))
├── NTFSPartition         (NTFS, NTFS (H), NTFS / HPFS, NTFS / HPFS (H), NTFS / HPFS (S))
├── Ext2Partition         (Linux Ext2)
└── OtherUnknownPartition (Diagnostic, Unknown, Dos Extended, OS/2 Boot Mgr, SCO Unix, SCO Xenix, Linux Swap, extd)
```

Directory-entry classes are parallel:
- `DirectoryEntry`
- `Ext2DirectoryEntry`
- `FATDirectoryEntry`
- `NTFSDirectoryEntry`
- `Ext2Directory`
- `FATDirectory`
- `NTFSDirectory`

NTFS support includes MFT-level parsing (`NTFSMftRecord`,
`NTFSAttribute`, `NTFSAttributeList`, `NTFSNonResidentAttribute`,
`NTFSCompressedAttribute`, `NTFSResidentAttribute`,
`NTFSAttributeListEntry`, `NTFSDummyAttribute`,
`NTUpdateSequenceArray`, `NTFSRun`, `NTFSCompressedRun`,
`NTFSRunFragment`).

NTFS-encrypted file handling: the binary explicitly rejects extracting
EFS-protected NTFS files (`"All NTFS File(s) and/or Directory(s) were
encrypted - none were extracted"`, `"Attempt to extract encrypted NTFS
File/Directory"`) — this is **disk-level NTFS encryption**, separate
from the CRC-16 image-level cipher in §3.2.

---

## 5. Stream class hierarchy

```
Stream (abstract base)
├── AbstractStream         (record-aware base — handles the 10-byte record headers)
│   └── SpanStream         (handles cross-segment continuation .ghs files)
└── LayeredStream          (decorator)
    ├── CompressionStream  (dispatches None / Old / Fast / High per §2)
    └── EncryptedStream    (applies the CRC-16 cipher per §3.2)
```

Each partition's payload is read via
`EncryptedStream(CompressionStream(SpanStream(...)))` — encryption is the
outermost layer, compression next, span/record framing innermost. This
layering matches the writer dispatch order observed in `FUN_00422260`:
first the 10-byte record header is written un-encrypted, then the body
is optionally encrypted (`FUN_0041bdd0`) before being written. The
ciphering does not extend to record headers, so a third-party reader
that ignores encryption can still walk the record-magic chain to
identify file structure.

---

## 6. Version gating — Ghost ≤ 2.x is hard-rejected

User-facing diagnostic strings at known addresses:

| Addr | Text |
|------|------|
| `0x004c3c5a` | `"Not a Ghost image file"` |
| `0x004c3d1a` | `"This image file has been created with a version of Ghost earlier than 3.0"` |
| `0x004c3dae` | `"This image file has no partition index. Ghost Explorer will only load the first partition."` |
| `0x004c4170` | `"This version of Ghost Explorer does not support spanned image files"` |
| `0x004c41f8` | `"Incorrect password supplied"` |
| `0x004c50f0` | `"Invalid drive details. This probably isn't a Ghost image file."` |
| `0x004c5208` | `"This image has been BIOS locked and can not be viewed by Ghost Explorer."` |
| `0x004c5f40` | `"Unsupported compression format"` |

`"earlier than 3.0"` is the user message attached to the
`"Old compression not supported"` rejection in `FUN_0042948e` — Ghost
Explorer 2003.789 maps Ghost ≤ 2.x to this branch.

Image classification is multi-stage: missing record-magic → "Not a Ghost
image file"; missing partition index → "earlier than 3.0"; `Z1` first
byte of compression header → rejection (Old PKWARE DCL); valid header
but corrupt index → "no partition index"; valid header with BIOS-lock
flag → "BIOS locked".

The `IGNOREINDEX` command-line switch at `0x004882c4` is a debugging
escape that skips the partition-index validation.

---

## 7. Spanning (`.ghs`) protocol

The string `"000.GHS"` at `0x00489bfc` is the **continuation filename
suffix** — when a span is requested, the writer prompts for a new file
matching the `*.GHS` glob (or `*.0*` per the open-dialog filter at
`0x004c5fbc`, `"Ghost image extensions (*.GHS,*.0*)"`).

The unicode string at `0x004c5912`
(`"Unable to open image file segment %d. Please change the disk or cartridge."`)
shows that span IO is interactive — Ghost Explorer prompts the user
when a segment is missing. This is the legacy equivalent of the AOMEI
`CALBAK_CMD_ASK_FOR_OLD_IMAGE` callback mechanism.

Class `?AVCSpan@@` at `0x004886f8` and `.?AVSpanStream@@` at
`0x00489bc0` are the C++ classes implementing the span protocol.

---

## 8. Cross-vendor confirmation summary

The following table maps every Ghost 11.5.1 / nyarime-RE artefact that
also appears verbatim in Ghost Explorer 2003.789:

| Artefact | nyarime/gho (Ghost 11.5.1) | Ghost Explorer 2003.789 | Confirmation |
|----------|---------------------------|--------------------------|--------------|
| Fast LZ hash-table sentinel | `"123456789012345678"` | `s_123456789012345678_0048a978` | **byte-identical literal** |
| Fast LZ hash multiplier | `-24993` / `0x9E5F` | `-0x61a1` / `0x9e5f` | **byte-identical constant** |
| Fast LZ 4096-entry hash table | seeded with sentinel pointer | seeded with sentinel pointer (`FUN_0042a7a0:36-58`, `FUN_0042ab40:46-68`) | **byte-identical init pattern** |
| Fast LZ 0x01 uncompressed escape | first-byte check | `if (*pcVar4 == '\x01')` in `FUN_0042ab40:31` | **byte-identical short-circuit** |
| Fast LZ 16-bit LSB control word | `data[i] | (data[i+1] << 8) | 0x10000` | `*pbVar16 | 0x10000 | (uint)pbVar16[1] << 8` in `FUN_0042ab40:71` | **byte-identical reload** |
| Fast LZ match token encoding | `(b0 & 0xF0) << 4 | b1` for index, `b0 & 0xF` for extra length | `(bVar3 & 0xf0) << 4 | (uint)bVar2` and `bVar3 & 0xf` | **byte-identical token shape** |
| Record container magic | `0x012F18D8` | `0x12f18d8` in 8 distinct functions | **byte-identical 32-bit constant** |
| File header magic | FE EF at offset 0 | (writer not yet isolated in this Ghidra pass; the in-memory parse uses the same `*(int *)(param_3 + 0x10)` partition-list traversal idiom) | **header-magic check via control-flow inspection** |
| 10-byte record header layout | `[4B type | 4B magic | 2B body_len]` | `local_4a8 = type; local_4a4 = 0x12f18d8; len = 10` in `FUN_00421fd0` | **byte-identical struct** |
| CRC-16 stream cipher | CCITT/XMODEM keyed | `(&DAT_00490338)[(state >> 8) ^ byte] ^ (state << 8)` in `FUN_0041bdd0` | **byte-identical cipher state update** |
| Compression-byte 0/1/2/≥3 dispatch | None / Old (reject) / Fast / High | `cVar1 == 0/1/2` switch in `FUN_0042948e` | **byte-identical dispatch table** |
| Record type 0x23 = end | end-of-image marker | written by `FUN_00411e40:216` after the 0x012F18D8 magic | **byte-identical type constant** |

**Verdict.** The Fast LZ codec, the record container framing, the CRC-16
stream cipher, the compression-mode dispatch table, and the record-type
enum are **all byte-identical** between Ghost Explorer 2003.789 and the
nyarime-RE Ghost 11.5.1 reference. This is independent cross-vendor
confirmation that **the Binary Research → Symantec → Norton lineage uses
the same on-disk image-format engine from at least Ghost 3.0 through
Ghost 11.x / 12.x**, justifying the unified parser in
`FileFormat.Ghost/GhostReader.cs`.

The "BR shared codebase" hypothesis (from the AOMEI work in
`docs/AOMEI_FORMAT_SPEC.md`, where AOMEI's binaries leak the literal
build path `d:\work\br\src\imgfile\imagefile.cpp` and codename
`BRCloudv2`) is **separately corroborated** in Ghost Explorer 2003.789 by
the embedded `"BinaryResearch"` and `"NORTON GHOST"` strings — Binary
Research's image-format engine is the common ancestor for both Symantec's
Ghost line and (per the AOMEI assertion paths) AOMEI's later .adi/.afi
backup format.

---

## 9. Pre-3.0 ("Old") compression path — irrecoverable from this binary

Ghost Explorer 2003.789 **does not contain a working decoder** for the
"Old" (Z1, PKWARE DCL Implode) compression used by Ghost ≤ 2.x. The
dispatch at `FUN_0042948e:21-24` explicitly emits the error
`"Old compression not supported"` and returns 0 without attempting
decompression.

The PKWARE DCL Version 1.11 copyright string is still in the binary
(licensing requirement) but the implode/explode entry points are not
called by any function reachable from the user-facing
`CompressionStream::Read` dispatch. Recovering pre-3.0 image content
would require:

1. A copy of the original PKWARE Data Compression Library v1.11 (the
   `pkware.dcl` SDK shipped from 1989-1995), **or**
2. An equivalent open-source PKWARE DCL Implode decoder (`blast.c` from
   zlib contrib is a known open-source reimplementation), **or**
3. A copy of pre-2003 Ghost Explorer (Ghost Explorer 2.x or earlier) —
   our analysis of the 2003.789 release shows the decoder was
   intentionally removed.

Without one of these, **Ghost ≤ 2.x images cannot be decoded**. This is
the same gap documented in `GhostFormatDescriptor.Description` and
surfaced in `GhostReader.GenerationHint = PossiblyLegacy4To7`.

`FileFormat.Ghost` is currently scoped at modern (Ghost 3.0+) images;
this scope is **validated** as correct by this Ghidra analysis — the
on-disk format is uniform from 3.0 onward, so the existing `GhostReader`
+ `GhostWriter` cover the entire Symantec-era lineage, not just Ghost
11.x. The "legacy 4-7" gating in the descriptor is conservative — the
modern container parse will accept any Ghost 3.0+ image (Ghost 3.0 was
released 1998 and 2003.789 is the last free Symantec release before
Norton Ghost 9 transitioned to the PowerQuest-derived `.v2i` format).

---

## 10. R/W scope decision — **stays at current R/W for modern, R/O legacy**

The existing implementation already covers:

- R/O parse of any Ghost 3.0+ image via `GhostReader` (modern record
  container)
- R/W round-trip of the same format via `GhostWriter` (self-validated;
  external cross-validation requires a real Symantec corpus)
- Stage-0 metadata for legacy 4-7 / pre-3.0 / "Old" compression images
  with explicit `GenerationHint = PossiblyLegacy4To7` and guidance text
  pointing at Ghost Explorer

This Ghidra analysis **confirms** the existing scope is correct and does
**not** justify extending the writer:

- Pre-3.0 images use PKWARE DCL Implode which Ghost Explorer 2003.789
  itself refuses to read (§9) — no point shipping a partial encoder for
  a codec the original tool refuses to decode
- The "modern" record container is **already covered by R/W** in
  `GhostWriter`; this analysis cross-validates the writer's record-
  framing constants against the 2003.789 reader and finds them
  byte-identical
- The writer's Fast LZ encoder is byte-identical to the codec emitted by
  `FUN_0042a7a0` (encoder side, same constants, same hash-table init,
  same control-word format, same token encoding) — so files produced by
  our `GhostWriter` should round-trip through Ghost Explorer 2003.789
  unchanged

**Verification gap that remains.** We have not yet validated a Ghost
image produced by our writer through Ghost Explorer 2003.789 on a real
Windows host — that requires running `Ghostexp.exe` (a Win32 binary)
under Wine/Windows and feeding it an `.gho` produced by `GhostWriter`.
This is a future workstream — the byte-identity established here is a
strong precondition but not a behavioural verification.

---

## 11. Honest list of gaps

1. **Pre-3.0 PKWARE DCL Implode decoder** — not present in Ghost
   Explorer 2003.789; not implemented in this codebase (see §9).
2. **End-to-end Wine round trip** — our `GhostWriter` output has not
   yet been opened by the real `Ghostexp.exe` binary on Windows or
   Wine; the byte-identity arguments above are static-analysis only.
3. **`0x06`, `0x1D`, `0x1E`, `0x803` record body layouts** — the type
   codes are observed but only the field offsets and CRC trailer
   semantics for `0x803` are recovered; full body schemas for
   `0x1D` (per-partition descriptor) and `0x1E` (embedded-file
   fragment) need either a real `.gho` corpus or dynamic tracing.
4. **NTFS in-image MFT walk** — the binary contains a complete NTFS
   parser (§4); this is currently scoped out of `FileFormat.Ghost`
   (which surfaces partition payloads as `partition_NN.img` blobs for
   downstream tools to mount).
5. **BIOS-lock flag** — string `"BIOS locked"` exists; the flag bit
   position in the file header is not yet recovered.

---

## 12. References (file paths in this PoC)

- Decompiled functions: `~/output/ghostexp-2003-789/functions/` in WSL
  (3,696 `.c` files; total 3,696 functions across `Ghostexp.exe`)
- Pipeline: `tools/ghidra-pipeline/decompile.sh` +
  `tools/ghidra-pipeline/dump-functions.py`
- Existing R/W implementation cross-validated by this analysis:
  - `FileFormats/FileFormat.Ghost/GhostFastLz.cs` (codec)
  - `FileFormats/FileFormat.Ghost/GhostStructures.cs` (constants)
  - `FileFormats/FileFormat.Ghost/GhostReader.cs` (parse)
  - `FileFormats/FileFormat.Ghost/GhostWriter.cs` (emit)
  - `FileFormats/FileFormat.Ghost/GhostCrc16Cipher.cs` (cipher)
  - `FileFormats/FileFormat.Ghost/GhostFormatDescriptor.cs` (registry)

Key cited function offsets in this doc (all in `Ghostexp.exe`):

| Address | Role |
|---------|------|
| `0x0042948e` | `CompressionStream::Read` dispatch (None/Old/Fast/High) |
| `0x0042a730` | Compression mode dispatcher |
| `0x0042a7a0` | Fast LZ encoder |
| `0x0042ab40` | Fast LZ decoder |
| `0x0042a6a0` | zlib inflate (High decompression) |
| `0x0041bca0` | CRC-32 (BE, table-driven, slicing-by-4) |
| `0x0041bd60` | CRC-32 wrapper |
| `0x0041bdd0` | CRC-16 stream cipher |
| `0x0041ff30` | Encryption-enabled check |
| `0x00411e40` | End-of-image / partition-table writer |
| `0x00421fd0` | Per-partition record writer (type 0x1D) |
| `0x00422260` | Embedded-file fragment writer (types 0x1E + 0x803) |
| `0x004126b0` | File-open + header-validate |

Key data offsets:

| Address | Symbol |
|---------|--------|
| `0x0048a978` | `s_123456789012345678` (Fast LZ sentinel) |
| `0x00488a30` | `"BinaryResearch"` |
| `0x00488834` | `"NORTON GHOST"` |
| `0x0048ad88` | PKWARE DCL Version 1.11 copyright (unused) |
| `0x0048aff8` | zlib `deflate 1.0.4` version string |
| `0x0048b538` | zlib `inflate 1.0.4` version string |
| `0x00489378` | CRC-32 byte-at-a-time table |
| `0x00489778` | CRC-32 slicing-by-4 table |
| `0x00490338` | CRC-16-XMODEM/CCITT cipher table |
