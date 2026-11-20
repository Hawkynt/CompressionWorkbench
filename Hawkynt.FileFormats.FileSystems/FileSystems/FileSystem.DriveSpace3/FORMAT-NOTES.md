# Microsoft DriveSpace 3 CVF — format notes

> **Verification status.**
> - **Windows 98 SE's own real-mode `DRVSPACE.BIN`** (from the CD's boot floppy) mounts every volume
>   `DriveSpace3FormatDescriptor.Create` writes (stored, JM, SQ, Auto), reads every file byte-exact, and
>   its `SCANDISK /CHECKONLY` finds nothing wrong (`DriveSpace3RealDriverTests`, opt-in, see
>   `Compression.Tests/Support/MsDosImageStaging.md`). MS-DOS 7.1's `DRVSPACE.BIN` mounts and reads
>   them too.
> - Volumes **Windows 98's DriveSpace 3 created** and filled are checked in
>   (`Compression.Tests/Cvf/ReferenceVectors/Volumes/`): *Standard compression* (JM-0-0), *HiPack*
>   (JM-0-1) and, after Compression Agent's *UltraPack all files*, SQ-0-0. Our reader reads each
>   byte-exact, an add keeps it readable, and our writer's system area matches theirs field for field.
> - The independent `dmsdos` driver mounts and reads every volume we create and every volume Windows 98
>   created ("drivespace 3 CVF"; `CvfCreateDmsdosTests`, `DriveSpace3GenuineDmsdosTests`).
> - **Cluster codecs:** Windows 98 writes JM-0-0 (*Standard*), JM-0-1 (*HiPack*) and SQ-0-0 (*UltraPack*,
>   by Compression Agent); we create JM-0-0 and SQ-0-0 and read all three
>   ([`docs/CVF-CODECS-ON-DISK.md`](../../../docs/CVF-CODECS-ON-DISK.md)). DS-0-x clusters are read but
>   never written.
> - **One layout only.** The writer produces nothing but the container below; edits (add, remove,
>   defragment, wipe) rebuild the volume and keep its label. Fragmented clusters are not read.

DriveSpace 3 (Windows 95 Plus! Pack, OSR2, Windows 98) is a member of the DOS **`MSDBL6.0`** CVF family —
the same container idea as DoubleSpace/DriveSpace version 2 (`FileSystem.DoubleSpace`) — with 32 KiB
clusters, a longer MDBPB and a 5-byte MDFAT entry.

## 🧩 Layout

Measured on 1 MB and 3 MB volumes Windows 98's DriveSpace 3 created on a 64 MB host, with each
compression setting; the system area is the same in all of them. Sector numbers are 512-byte sectors.

| Sectors | Content |
|---|---|
| 0 | MDBPB, bytes 0x00–0x47 (below) |
| 1 – 1021 | BitFAT: one bit per sector of the cluster heap, MSB-first through little-endian 16-bit words (as in version 2) |
| 1022 – 1692 | MDFAT: 5-byte entries, 102 per sector then 2 pad bytes; entry of inner cluster *n* at index `n + 0x2D`. Bytes 0–2: run sector − 1; byte 3 bits 2–7: on-disk sectors − 1 (bits 0–1: 0 as the driver writes them; Compression Agent left 1 on two of the three clusters it processed); byte 4 bits 0–5: decoded sectors − 1, bit 6: uncompressed, bit 7: used |
| 1693 | Inner boot sector: the MDBPB's first 0x20 bytes with two FATs, total sectors `MDBPB 0x20 + 255`, then `29`, a serial, `COMPRESSED `, `FAT16   ` |
| 1694 | Starts `F8 08 22 00` |
| 1726 | FAT16 (255 sectors) |
| 1981 | Root directory (512 entries) |
| 2015 … | Cluster heap — inner base + first data (320) + 2; the BitFAT's first bit is this sector |
| last sector | Starts with `MDR\0` |

As in version 2, the driver writes a file's last cluster only as far as its data reaches (a 5000-byte
file is a 10-sector stored run; a 20000-byte cluster decodes to 40 sectors), and the MDFAT index is
`cluster + first data / 64 − 2`, so the inner system area (33 reserved + 255 FAT + 32 root sectors)
ends on a cluster boundary.

### MDBPB

| Offset | Value | Notes |
|---|---|---|
| 0x00 | `EB 6E 90` | the jump clears an MDBPB that runs to 0x47 |
| 0x03 | `MSDBL6.0` | |
| 0x0B | 512 bytes per sector, 64 sectors per cluster, 33 reserved, 1 FAT, 512 root entries | |
| 0x15 | media F8, 255 FAT sectors, 17 sectors per track, 6 heads | |
| 0x20 | 4 178 112 | inner-volume sectors: the 2 GB maximum, whatever the file's size |
| 0x24 | 1021 | MDFAT start − 1 |
| 0x26 | 9 | |
| 0x27 | 1693 | inner base |
| 0x29 / 0x2B | 288 / 320 | root directory / first data, inner sectors |
| 0x2D | 3 | MDFAT index of cluster 0 (`320 / 64 − 2`) |
| 0x2F | 255 | |
| 0x31 | `00 06` | |
| 0x33 | 0, then 1 at 0x34 | the byte that reads 2 on DriveSpace volumes is 0 here |
| 0x35 | `00 BA 10 00` | |
| 0x3E | `F8 07` | |
| 0x40 | `14 00 00 00 0A 00 00 00` | |

The words not explained are carried as measured: `GenuineDvr3Writer` writes 0x00–0x47 as one set.

Detection: the `MSDBL6.0` OEM name plus 64 sectors per cluster (byte 0x33 cannot tell DriveSpace 3 from
DoubleSpace, which also has 0 there). A DriveSpace 3 volume that reaches the DoubleSpace descriptor by
its `.cvf` name is handed to the DriveSpace 3 code.
