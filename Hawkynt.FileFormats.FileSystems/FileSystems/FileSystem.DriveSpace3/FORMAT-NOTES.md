# Microsoft DriveSpace 3 CVF — format notes

> **Verification status.**
> - **Every volume `DriveSpace3FormatDescriptor.Create` writes is mounted by the independent `dmsdos`
>   driver** ("drivespace 3 CVF") and read back byte-exact — for every published codec (stored, JM,
>   SQ, Auto), and again after an add and a remove (`CvfCreateDmsdosTests`,
>   `DriveSpace3GenuineDmsdosTests`).
> - **Cluster codecs:** the real driver writes JM-0-0 (*Normal*), JM-0-1 (*High*) and SQ-0-0 (*Ultra*);
>   only those are created (JM and SQ). DS-0-x clusters are read but never written. All are specified
>   in [`docs/CVF-CODECS-ON-DISK.md`](../../../docs/CVF-CODECS-ON-DISK.md) and decoded by
>   `CvfLzCodec`, pinned to 32 KiB clusters the dmsdos compressors wrote (`CvfReferenceVectorTests`).
> - **Not accepted by MS-DOS 7.1's `DRVSPACE.BIN`.** Booted with `DrvSpace=1` and our volume as
>   `C:\DRVSPACE.000`, the 7.1 driver mounts our DoubleSpace / DriveSpace version-2 volumes but leaves
>   a DriveSpace 3 volume unmounted (stored, JM and SQ alike). No volume a Microsoft tool wrote in this
>   format was available to measure the system-area words against (the version-2 container needed
>   such a set — see `FileSystem.DoubleSpace/FORMAT-NOTES.md`), so the layout below is proven against
>   `dmsdos` only.
> - **One layout only.** The writer produces nothing but the container below; edits (add, remove,
>   defragment, wipe) rebuild the volume and keep its label. Fragmented clusters are not read.

DriveSpace 3 (Windows 95 Plus! Pack, OSR2) is a member of the DOS **`MSDBL6.0`** CVF family — the same
container as DoubleSpace/DriveSpace version 2 (`FileSystem.DoubleSpace.GenuineCvfWriter`) — told apart by:

| Field | DriveSpace 3 | DoubleSpace / DriveSpace |
|---|---|---|
| Sectors per cluster (boot byte 13) | 64 (32 KiB clusters) | 16 (8 KiB) |
| Version (boot byte 51) | 3 | 2 |
| MDFAT entry | 5 bytes, 102 per sector + 2 pad bytes: `pos = (s_dcluster+cl)*5 + ((s_dcluster+cl)/102)*2 + 512*mdfatstart` | 4 bytes |
| Inner volume | FAT16 | FAT12 |

A stored cluster carries flags 3 (used + uncompressed) with on-disk and decoded sizes both 63 (64
sectors). A compressed cluster starts with its 4-byte method header and is decoded by `CvfLzCodec`.

Detection: the `MSDBL6.0` OEM name plus 64 sectors per cluster. A DriveSpace 3 volume that reaches the
DoubleSpace descriptor by its `.cvf` name is handed to the DriveSpace 3 code.

**Confirmed against the dmsdos documentation**
(<https://github.com/sandsmark/dmsdos/tree/master/doc>, `dmsdos.doc`): cluster size is 32 KiB for
DriveSpace 3 against 8 KiB for the other compressed filesystems; per-cluster compression is keyed by
the 4-byte header (`JM-0-0` *Normal*, `JM-0-1` *High*, `SQ-0-0` *Ultra*).
