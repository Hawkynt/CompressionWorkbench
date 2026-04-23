# CompressionWorkbench Filesystem Coverage

Snapshot: 2026-04-22. 41 filesystems, 37 with read+write, 4 read-only.

Test suite: **8044 passing, 0 failures**.

## Legend

- **R/W**: can both read existing images and create new spec-compliant ones
- **RO**: can read existing images; no writer (or writer not yet spec-compliant)
- **Spec**: the external document/source the writer was validated against
- **Validation**: what external tool, if any, we've wired up to cross-check our output

## Windows / DOS native

| FS                               | State | Spec                                      | Notes                                                                                                                                          |
| -------------------------------- | ----- | ----------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------- |
| **FAT12/16**                     | R/W   | Microsoft EFI FAT specification           | Full BPB, 0x55 0xAA signature, FAT12/16 auto-select by cluster count; **FAT32 branch throws — use FAT16 for images up to 2 GB**                |
| **exFAT**                        | R/W   | Microsoft exFAT File System specification | Full VBR, boot-checksum sector (§3.1.3) populated, Upcase/Bitmap/VolumeLabel root entries                                                      |
| **NTFS**                         | R/W   | MS-NT On-Disk Specification / TSK docs    | All 16 system MFT files ($MFT/$MFTMirr/$LogFile/$Volume/$AttrDef/$Bitmap/$Boot/$BadClus/$Secure/$UpCase/$Extend), USA fixup, LZNT1 compression |
| **DoubleSpace / DriveSpace CVF** | R/W   | MS-DOS 6 Technical Reference              | Full MDBPB with DBLS/DVRS signature, MDFAT + BitFAT regions, inner FAT12/16 with VFAT LFN support. Stored runs only (JM/LZ77 is TODO)          |
| **HPFS**                         | RO    | OS/2 Inside Story                         | Read-only descriptor (no writer)                                                                                                               |

## Unix / Linux

| FS                   | State | Spec                                                                   | Notes                                                                                                                                                             |
| -------------------- | ----- | ---------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **ext2/3/4**         | R/W   | Linux kernel `fs/ext2/ext2.h`                                          | Spec-compliant with random UUID; FS revision 0 GOOD_OLD                                                                                                           |
| **XFS v5**           | R/W   | FreeBSD/Linux `fs/xfs/libxfs/xfs_format.h`                             | Rewritten from v4 toy to v5 with v3 dinodes, `sb_crc` CRC-32C, `sb_features_*`/`sb_meta_uuid`/`sb_pquotino`                                                       |
| **JFS**              | R/W   | Linux kernel `fs/jfs/jfs_superblock.h`                                 | pxd_t bit-packing (24-bit length + 40-bit address), inline dtree root, aggregate inode table with FILESYSTEM_I=16                                                 |
| **ReiserFS 3.6**     | R/W   | Linux kernel `fs/reiserfs/reiserfs.h`                                  | Spec-correct offsets (s_root_block@+8, s_magic="ReIsEr2Fs"@+52), leaf block-head with blk_free_space + blk_right_delim_key. No block CRC (v3.6 doesn't have them) |
| **F2FS**             | R/W   | Linux kernel `include/linux/f2fs_fs.h`                                 | Superblock magic at block-offset 0x400, checkpoint pair + SIT + NAT + SSA + Main, CRC-32C, inline dentries in root inode                                          |
| **Btrfs**            | R/W   | OpenZFS on-disk format docs                                            | Real chunk tree (SYSTEM/METADATA/DATA), sys_chunk_array in superblock, DEV_ITEM, CRC-32C on every block header                                                    |
| **ZFS**              | R/W   | OpenZFS source (`vdev_impl.h`, `uberblock_impl.h`, `dmu.h`, `zap_*.h`) | 4 vdev labels (L0/L1/L2/L3), 128-entry uberblock ring with Fletcher-4, XDR NVList, MOS + DSL dir/dataset + microzap for root, pool version 28                     |
| **UFS1/FFS**         | R/W   | FreeBSD `sys/ufs/ffs/fs.h`                                             | `fs_magic=0x011954` at sb offset 1372, cylinder-group `cg_magic=0x00090255`, `fs_cs` summary block, inode-used + free-frag bitmaps                                |
| **UBIFS**            | RO    | Linux kernel `fs/ubifs/`                                               | Read-only; no writer (LPT/TNC trees are multi-week)                                                                                                               |
| **JFFS2**            | RO    | Linux kernel `fs/jffs2/`                                               | Read-only; log-structured node-scanner only                                                                                                                       |
| **YAFFS2**           | RO    | Aleph One YAFFS2 spec                                                  | Read-only; OOB/ECC layout not emittable                                                                                                                           |
| **BFS (BeOS/Haiku)** | RO    | Haiku OS source                                                        | Read-only; superblock surfacing only                                                                                                                              |

## Apple / classic Mac

| FS                              | State | Spec                                   | Notes                                                                                                                                    |
| ------------------------------- | ----- | -------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- |
| **HFS classic**                 | R/W   | Apple "Inside Macintosh: Files" (1992) | Real B-tree catalog + extents trees, 102-byte file records, 70-byte dir records, 46-byte thread records with correct (parent, name) sort |
| **HFS+**                        | R/W   | Apple Technote TN1150                  | Catalog file record at spec 248 bytes; dataFork at offset 88, resourceFork at offset 168 (coordinated reader+writer rewrite)             |
| **APFS**                        | R/W   | Apple File System Reference PDF        | NX superblock + container OMAP + APSB volume + FS B-tree, Fletcher-64 checksums (mod 2^32-1), single container/single volume WORM        |
| **MFS (Macintosh File System)** | R/W   | Inside Macintosh V (1985)              | Pre-HFS flat FS; `drSigWord=0xD2D7`, spec-correct MDB fields                                                                             |

## Retro / 8-bit

| FS                            | State | Spec                              | Notes                                                                                                 |
| ----------------------------- | ----- | --------------------------------- | ----------------------------------------------------------------------------------------------------- |
| **Commodore 1541 (.d64)**     | R/W   | VICE emulator docs                | Canonical 174,848 bytes, 35 tracks, directory at T18S1+                                               |
| **Commodore 1571 (.d71)**     | R/W   | VICE docs                         | 349,696 bytes, dual-side BAM                                                                          |
| **Commodore 1581 (.d81)**     | R/W   | VICE docs                         | 819,200 bytes, 80 × 40 × 256, DOS "3D" signature                                                      |
| **C64 tape (.t64)**           | R/W   | T64 format spec                   | "C64S tape image file" header                                                                         |
| **Amiga ADF (OFS/FFS)**       | R/W   | Amiga ROS docs                    | 901,120 (DD) / 1,802,240 (HD), "DOS\1" magic, BSDsum checksums                                        |
| **Amiga DMS**                 | R/W   | xDMS source                       | "DMS!" header with CRC16                                                                              |
| **Atari ST MSA**              | R/W   | MSA format spec                   | 0x0E0F BE magic, per-track RLE                                                                        |
| **Atari 8-bit ATR**           | R/W   | AtariDOS 2 VTOC spec              | 16-byte header + 92,160 sector bytes, VTOC @ sector 360, 16-byte directory entries in sectors 361-368 |
| **Apple DOS 3.3**             | R/W   | Apple DOS manual                  | 143,360 bytes, catalog at T17S15 chain, 35-byte entries                                               |
| **ProDOS**                    | R/W   | ProDOS Technical Reference Manual | 143,360 (5.25") or 819,200 (800K), 39-byte entries, storage-type 0xF vol dir header                   |
| **BBC Micro DFS (.ssd)**      | R/W   | Acorn DFS spec                    | 102,400 (40-track) / 204,800 (80-track), 31×8-byte dir entries in S0+S1                               |
| **ZX Spectrum SCL**           | R/W   | TR-DOS .scl spec                  | "SINCLAIR" magic + LE32 trailing sum checksum                                                         |
| **ZX Spectrum TR-DOS (.trd)** | R/W   | TR-DOS spec                       | 655,360 bytes, 160×16×256                                                                             |
| **Amstrad CPC DSK**           | R/W   | CPCEMU disk format                | "MV - CPCEMU Disk-File" magic, per-track metadata                                                     |
| **HP LIF (.lif)**             | R/W   | HP LIF utility manual             | 256-byte sectors, flat directory at sector 2, 32-byte entries with BCD timestamps, 0x8000 BE magic    |
| **CP/M 2.2 (8" SSSD)**        | R/W   | DR CP/M 2.2 BDOS reference        | 256 256 bytes (77×26×128), 2 reserved tracks, 1024-byte allocation blocks, 64-entry flat directory    |
| **DEC RT-11 (.rt11/.rx01)**   | R/W   | DEC RT-11 Volume + File Formats   | RX01 8" SSSD ~256 KB, 512-byte blocks, single 1024-byte directory segment at block 6, RAD-50 6.3 names |
| **OS-9 RBF (.os9/.rbf)**      | R/W   | Microware OS-9 Tech Reference     | CoCo 35-track DSDD ~315 KB, 256-byte sectors, big-endian, identification sector + bitmap + FD chain    |
| **Commodore G64 (.g64)**      | RO    | VICE emulator docs                | GCR-encoded track dump complementing D64/D71/D81; raw GCR bytes per track                              |
| **Commodore NIB (.nib)**      | RO    | nibtools docs                     | Raw 84-half-track nibble dump; one entry per track                                                     |

## Optical / CD / DVD

| FS           | State | Spec     | Notes                                                                                                                                                                                           |
| ------------ | ----- | -------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **ISO 9660** | R/W   | ECMA-119 | PVD at sector 16, VDST terminator at sector 17, L+M path tables, 2 KB blocks, flat directory (no Rock Ridge/Joliet)                                                                             |
| **UDF**      | R/W   | ECMA-167 | VRS (BEA01/NSR02/TEA01) at sectors 16-18, Main VDS at sectors 32-35, AVDP at sector 256. CRC-16-XMODEM (init=0, poly=0x1021) + TagChecksum on every tag (PVD/AVDP/PD/LVD/Terminator/FSD/FE/FID) |

## Disk-image container formats

Note: these are image *containers*, not filesystems — the contained payload is a separate FS. Listed for completeness.

| Format               | State         | Spec                             | Notes                                                                                              |
| -------------------- | ------------- | -------------------------------- | -------------------------------------------------------------------------------------------------- |
| **VHD** (Microsoft)  | R/W           | Microsoft Virtual Hard Disk spec | "conectix" magic, fixed + dynamic                                                                  |
| **VMDK** (VMware)    | R/W           | VMDK spec                        | "KDMV" magic                                                                                       |
| **QCOW2** (QEMU)     | R/W           | QEMU docs                        | Sparse format                                                                                      |
| **VDI** (VirtualBox) | R/W           | VBox source                      | Single-disk format                                                                                 |
| **VDFS**             | R (W=partial) | Gothic game engine reverse-eng   | Proprietary, no public spec; writer is flat-no-checksum (TOY — left in place until spec available) |

## Embedded / read-only flash

| FS           | State | Spec               | Notes                                                      |
| ------------ | ----- | ------------------ | ---------------------------------------------------------- |
| **SquashFs** | R/W   | SquashFs 4.0 spec  | `hsqs` magic, zlib+Adler-32, FlagNoFragments               |
| **CramFs**   | R/W   | Linux `fs/cramfs/` | `0x28CD3D45` magic, CRC-32, zlib blocks                    |
| **RomFs**    | R/W   | Linux `fs/romfs/`  | "-rom1fs-" magic, BE fields, self-correcting checksum      |
| **EROFS**    | RO    | Linux `fs/erofs/`  | Read-only; variable-length encoded inodes (W is follow-up) |

## External tool validation

| Tool                | Present?                                        | Validates                                                                        |
| ------------------- | ----------------------------------------------- | -------------------------------------------------------------------------------- |
| 7-Zip (portable)    | YES                                             | NTFS, FAT, exFAT, ext, HFS, HFS+, ISO 9660, UDF, SquashFS, CramFS (list/extract) |
| qemu-img            | NO (install from https://qemu.weilnetz.de/w64/) | VHD, VMDK, QCOW2, VDI (info + check)                                             |
| DISM                | YES (built-in)                                  | WIM, VHD, ISO                                                                    |
| chkdsk              | YES (built-in; needs admin + mounted volume)    | FAT, exFAT, NTFS                                                                 |
| mtools              | NO (install from Cygwin)                        | FAT (non-admin validation)                                                       |
| WSL + mkfs.*/fsck.* | NO (needs `wsl --install` as admin + reboot)    | ext/XFS/Btrfs/F2FS/JFS/ReiserFS/UDF/UFS                                          |

`ExternalFsInteropTests.cs` contains 18 validation tests that skip cleanly when tools are missing.
