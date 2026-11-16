# Microsoft DoubleSpace / DriveSpace CVF — format notes

> **Verification status.**
> - **MS-DOS 6.22's own `DRVSPACE.BIN`** mounts every volume `DriveSpaceFormatDescriptor.Create`
>   writes (JM and stored clusters), reads every file byte-exact, writes a new file into it that reads
>   back — through DOS and through our reader — and `SCANDISK` reports nothing to fix
>   (`MsDosRealDriverTests`, opt-in, see `Compression.Tests/Support/MsDosImageStaging.md`).
> - **MS-DOS 7.1's `DRVSPACE.BIN`** mounts what both descriptors create (DoubleSpace DS and stored,
>   DriveSpace JM) and reads every file byte-exact.
> - A volume **MS-DOS 6.22's `DRVSPACE /CREATE` wrote** and filled is checked in
>   (`Compression.Tests/Cvf/ReferenceVectors/Volumes/`); our readers read it byte-exact, an add keeps
>   it readable, and our writer's system area matches its field for field.
> - The independent `dmsdos` driver mounts and reads every volume either descriptor creates
>   (`CvfCreateDmsdosTests`).
> - MS-DOS 6.22's driver lists a volume with DS-0-0 clusters but answers each DS cluster with a data
>   error: DS is DoubleSpace's codec, which DriveSpace 6.22 does not decode. MS-DOS 7.1 decodes both.

Both products use the version-2 **`MSDBL6.0`** compressed volume file: 512-byte sectors, 8 KiB
clusters (16 sectors), a 4-byte MDFAT entry per cluster. DoubleSpace writes DS-0-0 clusters,
DriveSpace JM-0-0 ([`docs/CVF-CODECS-ON-DISK.md`](../../../docs/CVF-CODECS-ON-DISK.md)); either may
store a cluster uncompressed.

## 🧩 Layout

Measured on volumes MS-DOS 6.22's `DRVSPACE /CREATE` wrote (sizes 0.5 to 6 MB) and confirmed by what
its driver accepts. Sector numbers are 512-byte sectors from the start of the file.

| Sectors | Content |
|---|---|
| 0 | MDBPB (below) |
| 1 … MDBPB 0x24 | BitFAT: one bit per sector of the cluster heap, set when a cluster run occupies it; bits run MSB-first through little-endian 16-bit words. The driver allocates new runs from it, so a run it does not mark is overwritten. |
| MDBPB 0x24 + 1 … | MDFAT: one little-endian u32 per inner cluster at byte `(0x24 + 1) * 512 + (0x2D + cluster) * 4` — sector of the run minus 1 in bits 0–20, on-disk sectors − 1 in bits 22–25, decoded sectors − 1 in bits 26–29, flags (bit 31 used, bit 30 uncompressed) in 30–31 |
| MDBPB 0x27 (inner base) | The inner volume's boot sector, then its reserved sectors, FAT, root directory |
| inner base + 1 | Starts with the media byte and `DR\0` |
| inner base + first data (0x2B) + 2 … | Cluster heap; the BitFAT's first bit is this sector |
| last sector of the file | Starts with `MDR\0` |

The driver writes a file's last cluster only as far as its data reaches: a 5000-byte file is one
stored run of 10 sectors (on-disk and decoded sizes 10), the 3616-byte tail of a 20000-byte file a
compressed run whose decoded size is 8 sectors. The sector holding the end of such a stream carries
leftover bytes after the stream's final sync, so a reader decodes exactly the decoded-size sectors and
treats the rest of the cluster as zero.

The MDFAT index of inner cluster *n* is `n + 0x2D`, and 0x2D is `first data / 16 − 2`: the driver maps
the inner volume in whole-cluster slots, so the system area (reserved + FAT + root directory) ends on a
cluster boundary. The reserved-sector count absorbs the padding.

### MDBPB

| Offset | Size | Value on a volume whose maximum size is the smallest tier |
|---|---|---|
| 0x03 | 8 | `MSDBL6.0` |
| 0x0B | 2 | 512 bytes per sector |
| 0x0D | 1 | 16 sectors per cluster |
| 0x0E | 2 | 28 reserved sectors |
| 0x10 | 1 | 1 FAT (the inner boot sector shows DOS 2; the driver serves the one FAT for both) |
| 0x11 | 2 | 512 root entries |
| 0x15 | 1 | media 0xF8 |
| 0x16 | 2 | 132 FAT sectors |
| 0x18 / 0x1A | 2 / 2 | 17 sectors per track, 6 heads |
| 0x20 | 4 | inner-volume sectors (the inner boot sector carries this plus one FAT) |
| 0x24 | 2 | 133 — MDFAT start − 1, also the BitFAT's last sector |
| 0x26 | 1 | 9 |
| 0x27 | 2 | 429 — inner base |
| 0x29 | 2 | 160 — root directory, inner sector |
| 0x2B | 2 | 192 — first data sector, inner |
| 0x2D | 2 | 10 — MDFAT index of cluster 0 (`0x2B / 16 − 2`) |
| 0x2F | 2 | 33 — FAT sectors / 4 |
| 0x31 | 2 | 1024 |
| 0x33 | 2 | 2 — CVF version |
| 0x35 | 2 | 20992 |
| 0x37 | 2 | 5 |
| 0x39 | 4 | `16  ` |
| 0x3D | 1 | 1 |
| 0x3E | 2 | 264 — FAT sectors × 2 |

The MDFAT spans `FAT sectors × 2 + 31` sectors (134 … 428). The inner FAT holds FAT12 entries
whatever the FAT area's size, as long as DOS counts fewer than 4085 clusters.

The tier follows the free space on the host when the volume is created, not the size requested:
hosts of 16 and 32 MB gave the values above for 0.5, 1, 1.5, 3 and 6 MB volumes; hosts of 64, 128 and
256 MB gave a second set (16 reserved, 256 FAT sectors, MDFAT start 258, inner base 801, root 272,
first data 304, 0x2D = 17, 0x2F = 64, 0x35 = 6656, 0x37 = 9, 0x3D–0x3F = `01 00 02`). The driver and
`SCANDISK` take these words together: on a volume of the first tier, changing 0x2F alone, or zeroing
0x35 and 0x37, makes `SCANDISK` reject the volume file as not a valid DriveSpace volume, and giving 0x3E
the first tier's value on a volume with a 1-sector FAT makes the driver return wrong data. Their individual meaning beyond the relations given
is not established, so `GenuineCvfWriter` writes the first tier's set whole; it is large enough for
any volume a FAT12 inner volume can describe.

Also carried as on created volumes: the `MDR\0` and `<media>DR\0` signatures (the driver mounts and
reads a volume without them) and the inner boot sector's 63 sectors per track and 16 heads.
