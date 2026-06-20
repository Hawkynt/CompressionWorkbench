# Microsoft DriveSpace 3 (DVR3) CVF — format notes

> **Driver-verification status (2026-06-18).**
> - **`GenuineDvr3Writer` is driver-proven (read):** the independent `dmsdos`
>   driver detects its output as "drivespace 3 CVF", mounts it, and reads every
>   file back **byte-exact** (gated by `DriveSpace3GenuineDmsdosTests`).
> - **The legacy `DriveSpace3Writer` (`MS_DSP3` / `DVR3` / offset-36) is NOT
>   genuine:** `dmsdos` rejects it (`cvftest` → "not a known CVF"). Its `MS_DSP3` /
>   offset-36 assumptions below were never confirmed against a real driver and are
>   wrong about genuine DriveSpace 3; it is retained only for self-round-trip.
>
> Genuine DriveSpace 3 is a member of the DOS **`MSDBL6.0` / `MSDSP6.0`** CVF
> family (same container as `FileSystem.DoubleSpace.GenuineCvfWriter`) with
> `sectors/cluster = 64` (boot byte 13), `version_flag = 3` (boot byte 51),
> **5-byte MDFAT entries** (102 per sector + 2 pad bytes;
> `pos = (s_dcluster+cl)*5 + ((s_dcluster+cl)/102)*2 + 512*mdfatstart`; stored
> cluster = flags 3 / size_lo = size_hi = 63) and an inner **FAT16** volume —
> exactly as `GenuineDvr3Writer` emits. The offset-36 notes below describe only
> the legacy self-format.

Clean-room notes for the on-disk shape of a Microsoft DriveSpace 3 Compressed
Volume File (CVF), as shipped with the Windows 95 Plus! Pack (1995) and OSR2.
No driver code was copied; only format facts were extracted.

**Confirmed against the dmsdos documentation**
(<https://github.com/sandsmark/dmsdos/tree/master/doc>, `dmsdos.doc`): cluster
size is **32 KB for DriveSpace 3** (= 64 sectors/cluster, what `GenuineDvr3Writer`
emits) versus 8 KB for the other compressed filesystems. Per-cluster compression
is an orthogonal codec keyed by a 4-byte header — `JM-0-0` (DriveSpace 3 *Normal*,
shared with DOS 6.22 DriveSpace), `JM-0-1` (*High*), `SQ-0-0` (*Ultra*).
`GenuineDvr3Writer` writes **STORED (uncompressed)** clusters (MDFAT flag = used +
uncompressed), a driver-accepted mode that sidesteps codec parity; emitting
`JM`/`SQ`-compressed clusters is a future enhancement.

## Correction to the "MSDBL inner-base @0x27" assumption

DriveSpace 3 (`MS_DSP3` / `DVR3`) does **not** use the DOS-6.22 `MSDBL6.x`
inner-FAT-base substructure (the `u16 @0x27` inner-volume base sector that the
`FileSystem.DoubleSpace.GenuineCvfWriter` / `DoubleSpaceReader` real-MSDBL path
relies on). Instead the genuine OSR2 DVR3 image uses the **offset-36 CVF-field
header** with the inner FAT volume mapped at file offset 0:

```
header sector 0  : MDBPB (standard BPB + CVF fields at 0x24/36)
inner reserved   : MDBPB itself (1 reserved sector)
inner FAT1/FAT2  : two 16-sector FAT16 copies
inner root dir   : 32 sectors (512 dir entries)
inner data area  : per-cluster mirror (starts at firstDataSector)
MDFAT region     : MdfatStart .. +MdfatLen sectors
BitFAT region    : BitFatStart .. +BitFatLen sectors
DATA region      : DataStart .. (physical compressed/stored runs)
```

This is exactly the layout `FileSystem.DriveSpace3.DriveSpace3Reader` /
`DriveSpace3Writer` already implement (the offset-44 family), and the existing
reader reads the genuine `drvspace3.cvf` byte-exact — see the round-trip test
`Genuine_DVR3_StoredCluster_Layout_RoundTrips`.

## MDBPB (sector 0) — confirmed field map for the genuine image

| Offset | Size | Field                | Genuine value           |
|-------:|-----:|----------------------|-------------------------|
| 0x00   | 3    | JMP                  | `EB 58 90`              |
| 0x03   | 8    | OEM name             | `"MS_DSP3"` + 1 NUL pad |
| 0x0B   | 2    | bytes/sector         | 512                     |
| 0x0D   | 1    | sectors/cluster      | 8 (→ 4096-byte cluster) |
| 0x0E   | 2    | reserved sectors     | 1                       |
| 0x10   | 1    | FAT count            | 2                       |
| 0x11   | 2    | root dir entries     | 512                     |
| 0x13   | 2    | total sectors (16)   | 32827                   |
| 0x15   | 1    | media descriptor     | `0xF8`                  |
| 0x16   | 2    | sectors per FAT      | 16                      |
| 0x18   | 2    | sectors per track    | 63                      |
| 0x1A   | 2    | heads                | 255                     |
| 0x24   | 4    | **CvfSignature**     | `"DVR3"`                |
| 0x28   | 4    | CVF version          | `0x00030300`            |
| 0x2C   | 4    | **MdfatStart** (sec) | 32777                   |
| 0x30   | 4    | **MdfatLen** (sec)   | 32                      |
| 0x34   | 4    | **BitFatStart**(sec) | 32809                   |
| 0x38   | 4    | **BitFatLen** (sec)  | 1                       |
| 0x3C   | 4    | **DataStart** (sec)  | 32810                   |
| 0x40   | 4    | **DataLen** (sec)    | 17                      |
| 0x48   | 4    | inner cluster count  | 4089                    |
| 0x1FE  | 2    | boot signature       | `55 AA`                 |

The CvfSignature lives at file offset 36 (0x24); MdfatStart at 44 (0x2C). These
are the same offsets the DoubleSpace/DriveSpace 6.x `DBLS`/`DVRS` headers use —
only the OEM string (`MS_DSP3`) and CvfSignature (`DVR3`) changed for DVR3.

## MDFAT entry encoding — confirmed

Each inner-volume cluster index `C` maps to a little-endian `u32` at
`MdfatStart*512 + C*4`:

```
bits  0..20  physical sector offset within the DATA region
bits 21..27  run length in sectors (1..127)
bits 28..31  flags: 1 = stored, 2 = compressed, 0 = free/unallocated
```

The absolute byte offset of a run is `(DataStart + physSector) * 512`. Clusters
0 and 1 are reserved (entry = 0). In the oracle image only cluster 2 is used:
`raw = 0x10200000` → phys = 0, run = 1 sector, flags = 1 (stored).

## Per-run framing — confirmed (stored)

Each physical run begins with a 2-byte little-endian header:

```
bits  0..11  payload length minus 1   (so 1..4096 bytes)
bit  15      0 = stored verbatim, 1 = compressed (codec payload follows)
```

For the genuine `HELLO.TXT` (18 bytes) the run was:

```
11 00  43 57 42 5F 43 56 46 5F 50 52 4F 4F 46 5F 4F 4B 0D 0A
^^^^^  header: (0x0011 & 0x0FFF)+1 = 18, bit15 = 0 (stored)
       payload: "CWB_CVF_PROOF_OK\r\n"  (18 bytes)
```

The reader/codec (`MsLzhBlockCodec`) decode this exactly; round-trip is
byte-identical.

## Inner FAT12/16 volume — confirmed

The inner volume is a normal FAT16 image embedded at file offset 0:
`reserved(1) + 2*FAT(16) + root(32)` → first data sector 65. The root directory
holds standard 8.3 (and VFAT LFN) entries; `HELLO.TXT` at cluster 2, size 18.
File data is read by walking the inner FAT chain and resolving each cluster
through the MDFAT to its physical run, then decoding the run framing.

## Per-cluster compression codec (HiPack / UltraPack)

DriveSpace 3 replaced the DOS-6.x DS-LZ77 codec with an LZ77 + Huffman
("HiPack" / "UltraPack") scheme. **None of the three locally available oracle
images (`drvspace3.cvf`, `drvspace62.cvf`, `dblspace60.cvf`) contain a single
compressed cluster** — every used cluster is a small stored run — so the
genuine HiPack bit-stream layout (Huffman table framing, window/dictionary init,
block-count bytes) could **not** be reverse-engineered from local resources.

`Compression.Core.Dictionary.MsLzh` implements a self-consistent, DEFLATE-shaped
LZ77 + canonical-Huffman codec (4 KiB window, fixed + dynamic Huffman tiers)
that our own reader/writer round-trip byte-exact. It is **not** asserted to be
bit-compatible with Microsoft's reference decompressor; a real DVR3 image
*containing a compressed cluster* would be required to pin the genuine codec
framing.

## Remaining for the Win95 driver-mount proof

The genuine **reader** path is proven against a real Microsoft DVR3 image
(`drvspace3.cvf` extracted from a MS-DOS/Win95 guest): our reader lists and
extracts `HELLO.TXT` byte-exact. What is *not* yet proven locally:

1. **Our writer mounted by the real Win95 DriveSpace 3 driver.** Requires a
   Windows 95 guest with DRVSPACE.VXD/.BIN (the DOS 6.22 guest used elsewhere
   does not load the DVR3 driver). Handled separately.
2. **Genuine HiPack/UltraPack codec bit-compatibility.** Needs a real DVR3
   image with an actually-compressed cluster as an oracle (none available
   locally). Our codec is round-trip-correct but framing is our own.
