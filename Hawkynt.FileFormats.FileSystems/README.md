# Hawkynt.FileFormats.FileSystems

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.FileFormats.FileSystems.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.FileSystems/)
[![License](https://img.shields.io/badge/license-LGPL--3.0--or--later-blue)](https://www.gnu.org/licenses/lgpl-3.0.html)

> Pure-managed filesystem readers / writers + disk-image container readers extracted from
[CompressionWorkbench](https://github.com/Hawkynt/CompressionWorkbench). Sister package to
`Hawkynt.FileFormats.Audio` / `Hawkynt.FileFormats.Archives` / `Hawkynt.FileFormats.Images`,
all built on top of `Hawkynt.Compression.Core`.

The package bundles every filesystem and disk-image assembly into `lib/`, so consumers add a
single dependency and can mount-and-walk a `.vhd` / `.qcow2` / `.iso` / `.dmg` / `.d64` etc. and
read out its files entirely in managed code — no `libguestfs`, no platform mounts, no
elevated privileges.

## When to use this package

- **Forensic / archival inspection**: open a disk image without mounting it; walk inodes / MFT /
  catalog records / B-trees in process memory
- **Cross-platform tooling**: read NTFS from Linux, ext4 from Windows, HFS+ from anywhere — same
  managed code, no native runtime
- **Retro-computing pipelines**: dump Commodore D64 / Apple DOS / Atari 8-bit / Spectrum / BBC Micro
  / CP/M disks straight from emulator captures
- **Cloud / VM image inspection**: peek inside QCOW2 / VMDK / VDI / VHDX without spinning up a
  hypervisor
- **WORM-creatable images**: build filesystem images for tests / fuzzing / firmware
  packaging — see the source repo's WSL-validated mkfs-parity tests

Every creatable filesystem builds **real nested directory trees** (not flattened paths) of
**arbitrary size** — multi-block / multi-leaf directory growth (B-tree node splitting on
btrfs / xfs / jfs / reiserfs / apfs / hfs+ / ntfs `$INDEX_ALLOCATION`, fat-ZAP on zfs,
multi-segment dentry blocks on f2fs, indirect/extent-backed directories on ext / ufs / minix /
udf / ocfs2 / prodos / bfs / hpfs). Creatable images can also **wipe unused space and
cluster-tip slack** (`IWipeEmpty`), **defragment** (including fusing fragmented directory
extents), and pick optimal cluster/block sizes via the layout optimizer. Where an external
tool exists, image conformance is **validated against the real OS utility** — `fsck.fat`,
`fsck.exfat`, `e2fsck`, `xfs_repair`, `btrfs check`, `fsck.f2fs`, `fsck.hfsplus`, `fsck.jfs`,
`reiserfsck`, `ntfs-3g`, `xorriso`, `mtools` — in the test suite (`setup-test-environment.sh`
installs them).

Skip it when:

- You only need to **read files from a real mounted volume** — `System.IO` does that with the
  OS's filesystem driver, no need for in-process parsers
- You need **block-level write semantics** with journaling guarantees — write support here is
  geared to one-shot image creation, not concurrent runtime mounts

## Quick start — read a disk image

```csharp
using FileSystem.Fat;

var img    = File.ReadAllBytes("disk.img");
using var fs = new FatReader(img);
foreach (var entry in fs.ListRecursive())
  Console.WriteLine($"{entry.Path}  {entry.Size,10}  {entry.Modified:O}");
```

## Quick start — peer inside a virtual disk image

```csharp
using FileFormat.Vhd;
using FileSystem.Ntfs;

using var stream = File.OpenRead("system.vhd");
var inner = new VhdReader().OpenContents(stream);   // exposes the partition table
foreach (var partition in inner.Partitions) {
  if (partition.Type == "NTFS") {
    var ntfs = new NtfsReader(partition.Open());
    foreach (var path in ntfs.WalkPaths())
      Console.WriteLine(path);
  }
}
```

## Contents

State legend:
- **R** — read-only: open the image and walk it.
- **WORM** — Write-Once-Read-Many: read AND can synthesise a fresh image from scratch
  (`IArchiveCreatable`), but cannot modify an existing image in place. Enough to build
  minimal images for tests / fuzzing / firmware packaging; not enough to act as a runtime
  driver.
- **R/W** — read + true in-place modification (`IArchiveModifiable`): add / replace / remove
  files inside an existing image with consistent free-space bookkeeping. The runtime path
  matches what `mkfs` + a real driver would do.

### Disk-image containers (`FileFormat.*`)

| Container           | State | Description                                                     |
| ------------------- | ----- | --------------------------------------------------------------- |
| `FileFormat.Vhd`    | WORM  | Microsoft Virtual Hard Disk v1 — fixed / dynamic / differencing — [more →](https://en.wikipedia.org/wiki/VHD_(file_format)) |
| `FileFormat.Vhdx`   | WORM  | VHD v2 (Win 8+) — 64 TB max, log-based crash consistency — [more →](https://en.wikipedia.org/wiki/VHD_(file_format)) |
| `FileFormat.Vmdk`   | WORM  | VMware Virtual Machine Disk — [more →](https://en.wikipedia.org/wiki/VMDK)        |
| `FileFormat.Vdi`    | WORM  | Oracle VirtualBox Disk Image — [more →](https://en.wikipedia.org/wiki/VirtualBox) |
| `FileFormat.Qcow2`  | WORM  | QEMU Copy-On-Write v2 — [more →](https://en.wikipedia.org/wiki/Qcow)              |
| `FileFormat.Dmg`    | WORM  | Apple Disk Image — [more →](https://en.wikipedia.org/wiki/Apple_Disk_Image)       |
| `FileFormat.Cso`    | R     | Compressed ISO (PSP / homebrew)                                 |
| `FileFormat.BinCue` | WORM  | CD/DVD raw track + `.cue` sheet — [more →](https://en.wikipedia.org/wiki/Cue_sheet_(computing)) |
| `FileFormat.Mdf`    | WORM  | Alcohol 120% Media Disc Format — [more →](https://en.wikipedia.org/wiki/Alcohol_120%25)         |
| `FileFormat.Nrg`    | WORM  | Nero burning ROM image — [more →](https://en.wikipedia.org/wiki/Nero_Burning_ROM)               |
| `FileFormat.Cdi`    | WORM  | DiscJuggler image — [more →](https://en.wikipedia.org/wiki/DiscJuggler)                         |
| `FileFormat.Pfs0`   | WORM  | Nintendo Switch firmware filesystem                             |
| `FileFormat.UImage` | R     | U-Boot uImage (embedded boot images) — [more →](https://en.wikipedia.org/wiki/Das_U-Boot)       |
| `FileFormat.UefiFv` | R     | UEFI firmware volume — [more →](https://en.wikipedia.org/wiki/UEFI)                             |
| `FileFormat.Ipsw`   | R     | Apple iOS / iPadOS firmware archive — [more →](https://en.wikipedia.org/wiki/IPSW)              |
| `FileFormat.Ewf`    | R     | Expert Witness Format / EnCase forensic image — [more →](https://en.wikipedia.org/wiki/EnCase)  |
| `FileFormat.T64`    | R/W   | Commodore 64 tape archive — rebuild-based modify — [more →](https://en.wikipedia.org/wiki/Commodore_DOS)  |
| `FileFormat.Tap`    | R/W   | Sinclair / Commodore tape image — rebuild-based modify — [more →](https://en.wikipedia.org/wiki/Commodore_DOS) |
| `FileFormat.Dtb`    | R     | Device Tree Blob (`.dtb` / `.dtbo`) — FDT v17, walks property tree as pseudo-archive — [more →](https://en.wikipedia.org/wiki/Device_tree) |
| `FileFormat.FirmwareHex` | R | Intel HEX (`.hex` / `.ihex`), Motorola S-Record (`.s19` / `.s28` / `.s37` / `.srec` / `.mot`), TI-TXT (MSP430) — ASCII firmware records decoded to flat `firmware.bin` + metadata — [more →](https://en.wikipedia.org/wiki/Intel_HEX) |

### Filesystem readers / writers (`FileSystem.*`)

#### Microsoft / Windows

| Filesystem               | State | Notes                                                                          |
| ------------------------ | ----- | ------------------------------------------------------------------------------ |
| `FileSystem.Fat`         | R/W   | FAT12 / FAT16 / FAT32, LFN — full BPB, 0x55 0xAA signature, FATGEN-compliant — [more →](https://en.wikipedia.org/wiki/File_Allocation_Table) |
| `FileSystem.ExFat`       | R/W   | Microsoft exFAT — full VBR, boot-checksum sector (§3.1.3) — [more →](https://en.wikipedia.org/wiki/ExFAT) |
| `FileSystem.Ntfs`        | R/W   | NTFS — all 16 system MFT files, USA fixup, LZNT1 compression — [more →](https://en.wikipedia.org/wiki/NTFS) |
| `FileSystem.Refs`        | R     | Resilient File System (Server 2012+) — header + boot sector parse only — [more →](https://en.wikipedia.org/wiki/ReFS) |
| `FileSystem.Hpfs`        | R/W   | OS/2 High Performance File System — rebuild-based add/remove, defragment, extent map — [more →](https://en.wikipedia.org/wiki/High_Performance_File_System) |
| `FileSystem.Htfs`        | WORM  | SCO HTFS (High Throughput FS) — `s_magic=0x012FD15D` LE @sector 1; S5-style superblock + inode array + 16-byte dirent chain; nested subdirectories; 512/1024/2048 B blocks; defrag + purge + fileset optimizer — [more →](https://en.wikipedia.org/wiki/High_Throughput_File_System) |
| `FileSystem.DoubleSpace` | R/W   | DOS 6 DoubleSpace / DriveSpace CVF — stored runs, rebuild-based modify — [more →](https://en.wikipedia.org/wiki/DriveSpace) |

#### Unix / Linux

| Filesystem            | State | Notes                                                                  |
| --------------------- | ----- | ---------------------------------------------------------------------- |
| `FileSystem.Btrfs`    | R/W   | B-tree filesystem — CRC-32C, real chunk tree (SYSTEM/METADATA/DATA) — [more →](https://en.wikipedia.org/wiki/Btrfs) |
| `FileSystem.Ext`      | R/W   | ext2 / ext3 / ext4 — DYNAMIC_REV, FILETYPE feature, files at inode 11 — [more →](https://en.wikipedia.org/wiki/Ext4) |
| `FileSystem.Xfs`      | R/W   | XFS v5 — `xfs_repair`-validated, AGF/AGI/AGFL, full B-tree set — [more →](https://en.wikipedia.org/wiki/XFS) |
| `FileSystem.Ext1`     | R/W   | ext1 — Theodore Ts'o 1992 original (magic 0xEF51, no `mkfs.ext1` exists); add/remove via rebuild — [more →](https://en.wikipedia.org/wiki/Extended_file_system) |
| `FileSystem.ReiserFs` | WORM  | ReiserFS 3.6 — `reiserfsck`-clean; multi-leaf S+tree with an internal node, R5-hashed keys, nested subdirectories of arbitrary size, correct `dc_size` / `.`/`..` keys / `sd_blocks`. In-place Add/Remove (multi-week S+tree split/merge balancing) is the remaining gap to R/W — [more →](https://en.wikipedia.org/wiki/ReiserFS) |
| `FileSystem.Reiser4`  | WORM  | Reiser4 — empty-FS only via 7 byte-exact reference blocks — [more →](https://en.wikipedia.org/wiki/Reiser4) |
| `FileSystem.Jfs`      | WORM  | IBM JFS1 — `fsck.jfs -n` clean; nested subdirectories with external dtree B+ pages (large directories), sorted+sibling-chained pages, correct secondary AIT/AIM. In-place Add/Remove (dmap/IAG mutation) is the gap to R/W — [more →](https://en.wikipedia.org/wiki/JFS_(file_system)) |
| `FileSystem.F2fs`     | WORM  | Flash-Friendly Filesystem — `fsck.f2fs`-clean; superblock + checkpoint + SIT/NAT/SSA + kernel hash-bucket directory blocks (large directories). In-place Add/Remove (NAT/SIT journal mutation, checkpoint CRC recompute) is the gap to R/W — [more →](https://en.wikipedia.org/wiki/F2FS) |
| `FileSystem.Zfs`      | R     | Sun ZFS — read existing pools — [more →](https://en.wikipedia.org/wiki/ZFS)                          |
| `FileSystem.Ufs`      | R     | UNIX File System (BSD) — `fs_magic=0x011954` — [more →](https://en.wikipedia.org/wiki/Unix_File_System) |
| `FileSystem.BcacheFs` | WORM  | bcachefs — superblock-only WORM (fsck parity multi-week, B-trees TODO) — [more →](https://en.wikipedia.org/wiki/Bcachefs) |
| `FileSystem.Ubifs`    | R     | UBIFS — log-structured, no writer (LPT/TNC trees multi-week) — [more →](https://en.wikipedia.org/wiki/UBIFS) |
| `FileSystem.Jffs2`    | R/W   | JFFS2 — log-structured, rebuild-based modify — [more →](https://en.wikipedia.org/wiki/JFFS2)         |
| `FileSystem.Yaffs2`   | R/W   | YAFFS2 — rebuild-based modify, defragment — [more →](https://en.wikipedia.org/wiki/YAFFS)            |
| `FileSystem.Bfs`      | R/W   | BeFS — single-AG B+ tree writer, rebuild-based modify — [more →](https://en.wikipedia.org/wiki/Be_File_System) |
| `FileSystem.Hammer`   | R     | DragonFly HAMMER — DragonFly BSD only, no Linux validator — [more →](https://en.wikipedia.org/wiki/HAMMER_(file_system)) |
| `FileSystem.Hammer2`  | R     | DragonFly HAMMER 2 — [more →](https://en.wikipedia.org/wiki/HAMMER2)                                 |
| `FileSystem.Ocfs2`    | R/W   | Oracle Cluster Filesystem 2 — rebuild-based modify — [more →](https://en.wikipedia.org/wiki/OCFS2)   |
| `FileSystem.Nwfs`     | R     | Novell NetWare — [more →](https://en.wikipedia.org/wiki/Novell_Storage_Services)                     |
| `FileSystem.Efs`      | WORM  | SGI EFS (pre-XFS IRIX FS) — `fs_magic=0x00072959`; spec-keyed superblock + single-CG inode table + per-file single-extent layout; nested subdirectories via inode chain; defrag + purge + fileset optimizer — [more →](https://en.wikipedia.org/wiki/Extent_File_System) |
| `FileSystem.Gfs1`     | WORM  | Sistina GFS (pre-GFS2) — `mh_magic=0x01161970`, `sb_multihost_format=1900`; metaheader-tagged superblock at +65536 with `sb_lockproto`/`sb_locktable`; nested subdirectories via 256-byte GFS dinode; defrag + purge + fileset optimizer (lock_nolock/lock_dlm, journal count knob) — [more →](https://en.wikipedia.org/wiki/GFS2) |
| `FileSystem.Jfs1`     | WORM  | OS/2 IBM JFS1 (pre-Linux JFS2) — `"JFS1"` magic + `s_version=1` (refuses JFS2 to avoid stealing `FileSystem.Jfs` detection); 256-byte dinodes with per-file single-extent layout; nested subdirectories; configurable 1024/2048/4096 block + aggregate block; defrag + purge + fileset optimizer — [more →](https://en.wikipedia.org/wiki/JFS_(file_system)) |

#### Apple / classic Mac

| Filesystem           | State | Notes                                                                 |
| -------------------- | ----- | --------------------------------------------------------------------- |
| `FileSystem.HfsPlus` | R/W   | Mac OS Extended — `fsck.hfsplus`-clean; multi-node catalog B-tree (chained index/leaf nodes), TN1150 case-folding key order, nested subdirectories of arbitrary size; add/remove via read-extract-rebuild — [more →](https://en.wikipedia.org/wiki/HFS_Plus) |
| `FileSystem.Hfs`     | R/W   | Classic Mac OS HFS — real B-tree catalog + extents trees; add/remove via rebuild — [more →](https://en.wikipedia.org/wiki/Hierarchical_File_System_(Apple)) |
| `FileSystem.Apfs`    | WORM  | Apple File System (macOS High Sierra+) — single container/volume, real NXSB/APSB + omap + FS-tree B-tree under Fletcher-64. In-flight Add/Remove needs B-tree split/merge, xid-keyed omap updates, checkpoint advance, spaceman bitmap, per-block Fletcher-64 recompute (multi-week) — [more →](https://en.wikipedia.org/wiki/Apple_File_System) |
| `FileSystem.Mfs`     | R/W   | Macintosh File System (1984) — pre-HFS flat FS, `drSigWord=0xD2D7`; add/remove via rebuild — [more →](https://en.wikipedia.org/wiki/Macintosh_File_System) |

#### Compressed / embedded / flash

| Filesystem            | State | Notes                                                            |
| --------------------- | ----- | ---------------------------------------------------------------- |
| `FileSystem.SquashFs` | R/W   | Compressed FS used in AppImage / live ISOs — zlib + Adler-32, rebuild-based modify — [more →](https://en.wikipedia.org/wiki/SquashFS) |
| `FileSystem.CramFs`   | R/W   | Compressed RAM-FS for embedded — `0x28CD3D45`, CRC-32, zlib, rebuild-based modify — [more →](https://en.wikipedia.org/wiki/Cramfs)   |
| `FileSystem.RomFs`    | R/W   | Read-only ROM FS — `-rom1fs-` magic, BE fields, rebuild-based modify — [more →](https://en.wikipedia.org/wiki/Romfs)                 |
| `FileSystem.MinixFs`  | R/W   | Minix v1/2/3 — superblock magics `0x137F/0x138F/0x2468/...`, rebuild-based modify — [more →](https://en.wikipedia.org/wiki/MINIX_file_system) |
| `FileSystem.Erofs`    | R     | Enhanced Read-Only FS (Android) — variable-length encoded inodes — [more →](https://en.wikipedia.org/wiki/EROFS) |
| `FileSystem.LittleFs` | R     | LittleFS for microcontrollers — superblock surfacing only — [more →](https://github.com/littlefs-project/littlefs) |

#### Optical

| Filesystem       | State | Notes                                                                 |
| ---------------- | ----- | --------------------------------------------------------------------- |
| `FileSystem.Iso` | R/W   | ISO 9660 + Joliet — `xorriso`-validated (all mandatory PVD/SVD fields), Joliet UCS-2 long names, multi-sector directories, L+M path tables; in-place add / remove via read-extract-rebuild (overwrites the source stream, secure-wipes removed bytes) — [more →](https://en.wikipedia.org/wiki/ISO_9660) |
| `FileSystem.Udf` | R/W   | UDF (DVD / Blu-ray) — ECMA-167, VRS@16-18, AVDP@256, CRC-16-XMODEM; add/remove via rebuild — [more →](https://en.wikipedia.org/wiki/Universal_Disk_Format) |
| `FileSystem.Sfs` | R     | Smart File System (Amiga) — root block surfacing only; full R/W requires the object-container B+ tree, bitmap chain, and directory hash table (multi-week Amiga-spec work, no external validator on Windows/WSL) — [more →](https://en.wikipedia.org/wiki/Smart_File_System) |

#### Retro / vintage

| Filesystem                       | State | Notes                                                       |
| -------------------------------- | ----- | ----------------------------------------------------------- |
| `FileSystem.D64` / `D71` / `D81` | R/W   | Commodore 1541 / 1571 / 1581 — directory at T18S1+; add/remove via rebuild — [more →](https://en.wikipedia.org/wiki/Commodore_DOS) |
| `FileSystem.CbmNibble`           | R     | Commodore raw nibble (`.g64` / `.nib`) — [more →](https://en.wikipedia.org/wiki/Commodore_DOS)        |
| `FileSystem.AppleDos`            | R/W   | Apple DOS 3.3 — 143 360 bytes, catalog at T17S15; add/remove via rebuild — [more →](https://en.wikipedia.org/wiki/Apple_DOS) |
| `FileSystem.ProDos`              | R/W   | ProDOS — 143 360 / 819 200, storage-type-3 trees; add/remove via rebuild — [more →](https://en.wikipedia.org/wiki/ProDOS) |
| `FileSystem.Atari8`              | R/W   | Atari 8-bit DOS 2 — 16-byte hdr + VTOC at sector 360; add/remove via rebuild — [more →](https://en.wikipedia.org/wiki/Atari_DOS) |
| `FileSystem.Bbc`                 | R/W   | BBC Micro DFS / ADFS — 102 400 / 204 800 bytes; add/remove via rebuild — [more →](https://en.wikipedia.org/wiki/Disk_Filing_System) |
| `FileSystem.Cpm`                 | R     | CP/M 2.2 — 256 256 bytes, 64-entry flat directory — [more →](https://en.wikipedia.org/wiki/CP/M_file_system) |
| `FileSystem.CpcDsk`              | WORM  | Amstrad CPC DSK — `MV - CPCEMU Disk-File` — [more →](https://en.wikipedia.org/wiki/AMSDOS)            |
| `FileSystem.TrDos`               | WORM  | Soviet ZX Spectrum TR-DOS — 655 360 bytes — [more →](https://en.wikipedia.org/wiki/TR-DOS)            |
| `FileSystem.ZxScl`               | R/W   | Spectrum SCL — `SINCLAIR` magic + LE32 sum; add/remove via rebuild — [more →](https://en.wikipedia.org/wiki/TR-DOS) |
| `FileSystem.Adf`                 | R/W   | Amiga Disk Format — DOS\1 magic, BSDsum checksums; add/remove via rebuild — [more →](https://en.wikipedia.org/wiki/Amiga_Disk_File) |
| `FileSystem.Msa`                 | WORM  | Atari ST Magic Shadow Archive — 0x0E0F BE magic — [more →](https://en.wikipedia.org/wiki/Atari_ST)    |

#### Mainframe / minicomputer

| Filesystem            | State | Notes                                                  |
| --------------------- | ----- | ------------------------------------------------------ |
| `FileSystem.Lif`      | R     | HP Logical Interchange Format — 256-byte sectors — [more →](https://en.wikipedia.org/wiki/Logical_Interchange_Format) |
| `FileSystem.OpenVms`  | R     | OpenVMS Files-11 (ODS-2 / ODS-5) — home block read only — [more →](https://en.wikipedia.org/wiki/Files-11) |
| `FileSystem.Os9Rbf`   | R     | OS-9 Random Block File — Microware OS-9 Tech Reference — [more →](https://en.wikipedia.org/wiki/OS-9) |
| `FileSystem.Rt11`     | R     | DEC RT-11 — 256 KB RX01 8" SSSD — [more →](https://en.wikipedia.org/wiki/RT-11)                       |
| `FileSystem.Vdfs`     | R/W   | Gothic-engine VDFS archive — REGoth/VdfsSharp-documented — [more →](https://en.wikipedia.org/wiki/Gothic_(series)) |

#### Stub-tier (detection-only / opaque-blob surface)

These descriptors recognise the container and surface either the whole image or
the inner payload as opaque entries plus a `metadata.ini`. No directory walk is
attempted — either the on-disk spec is not public, the payload is encrypted, or
the format is a proprietary compressed wrapper whose codec is out of scope.
`CanCreate` / `CanModify` are intentionally **not** advertised; upgrading any of
these to WORM requires the bullet under "what would be needed".

| Filesystem                | State    | Why stub-tier / what would be needed                                                                 |
| ------------------------- | -------- | ---------------------------------------------------------------------------------------------------- |
| `FileSystem.Tfs`          | detection only | BBN Trans-FS has no public on-disk spec; magic `0x54465301`. Upgrade requires BBN-internal docs (multi-week reverse-engineering, no public corpus). |
| `FileSystem.Mfs1`         | detection only | Acorn MFS-1 magic is two-byte heuristic (0x00 0x80); extension-led. Upgrade requires period Acorn manuals (small but obscure spec). |
| `FileSystem.Nwfs386`      | detection only | Novell NetWare 386 raw partitions, magic "NetW". Upgrade requires Novell proprietary spec (no public reference). |
| `FileSystem.Stacker`      | detection only | Stac Electronics Stacker CVF (MS-DOS 5/6); SCB header parsed, inner LZS-compressed FAT surfaced opaque. Upgrade needs Stacker LZS decompressor + inner-FAT delegation. |
| `FileSystem.DriveSpace3`  | detection only | Microsoft DriveSpace 3 CVF (`MS_DSP3`); MDBPB parsed, inner DS LZ77+Huffman region surfaced opaque. Upgrade needs the DS LZ77+Huffman codec + MDFAT walk (proprietary, ~weeks). |
| `FileSystem.GsOs`         | header only    | Apple IIgs 2IMG wrapper; delegates to the inner ProDOS / HFS / DOS 3.3 volume. Already covered by `FileSystem.ProDos` — upgrade is just routing the surfaced payload back through the matching reader. |
| `FileSystem.TahoeLafs`    | detection only | Tahoe-LAFS share buckets (v1 immutable / v2 mutable); payload is capability-encrypted Reed-Solomon ciphertext. Upgrade impossible without the read-cap (by design). |
| `FileSystem.Ecryptfs`     | detection only | eCryptfs per-file containers (marker `0x3C81B7F5`); payload is AES-CBC ciphertext. Decryption requires mount passphrase + EFEK tag-3/tag-11 packets (out of scope). |
| `FileSystem.OrangeFs`     | detection only | OrangeFS / PVFS2 DBPF (`PVFS` / `OGFP`); single distributed-FS server object. Upgrade requires the cluster's `fs.conf` + handle/fsid resolution + striping logic (distributed, multi-node). |

## WSL-validated filesystems

Several filesystems' writers have CI-validated round-trips against the actual `fsck` /
`repair` / `check` binaries shipped with Linux — the same programs the kernel uses to trust
a filesystem before mount.

| FS               | Validation                                          | Tool output                                            |
| ---------------- | --------------------------------------------------- | ------------------------------------------------------ |
| **ext4**         | `fsck.ext4 -fnv`                                    | exit 0, 0 errors                                       |
| **ext4**         | `dumpe2fs -h`                                       | reports magic 0xEF53 + UUID                            |
| **ext4**         | reverse: `mkfs.ext4` image read by our reader       | round-trips                                            |
| **FAT12/16**     | `fsck.fat -n -V`                                    | exit 0                                                 |
| **FAT**          | reverse: `mkfs.vfat` image                          | read by our reader                                     |
| **FAT**          | `Fat_OurImage_FreedosChkdsk` (FreeDOS 1.4 LiveCD in DOSBox-X, `[Explicit]`) | gated, marked Explicit (LiveCD welcome screen races autoexec) |
| **exFAT**        | `fsck.exfat -n`                                     | exit 0                                                 |
| **SquashFS**     | `unsquashfs -s`                                     | valid superblock reported                              |
| **XFS v5**       | `xfs_repair -n -f`                                  | exit 0, all 7 phases clean                             |
| **Btrfs**        | `btrfs check --readonly`                            | exit 0 "no error found"                                |
| **JFS**          | `fsck.jfs -n -f -v`                                 | exit 0 (gated on `jfsutils`)                           |
| **NTFS**         | `ntfsfix --no-action` + `ntfsinfo --mft 0` + `ntfsls -l` + reverse `mkfs.ntfs` | gated on `ntfs-3g` |
| **HFS+**         | `fsck.hfsplus -d -f -n` + reverse `mkfs.hfsplus`    | gated on `hfsprogs`                                    |
| **HFS classic**  | `hmount` / `hls` (hfsutils)                         | ✗ fix-pending: `hmount` reports `malformed b*-tree header node` against our writer |
| **ZFS**          | `zdb -l`                                            | gated on `zfsutils-linux`; parses NVList labels without kernel module |
| **UFS1/FFS**     | Linux `mount -t ufs` + (optional) FreeBSD `fsck_ffs` under QEMU | skips on stock WSL2 (kernel built without `ufs.ko`) |
| **BcacheFS**     | `bcachefs show-super` + gap-witness `fsck` test     | gated on `bcachefs-tools`; SB-only WORM (B-trees missing) |
| **Reiser4**      | `fsck.reiser4 -y` (forward) + `mkfs.reiser4 -fffy` (reverse) | gated on `reiser4progs`; empty-FS only via 7 byte-exact reference blocks |
| **DoubleSpace / DriveSpace CVF** | DOSBox-X + `DBLSPACE.EXE /CHKDSK` and `DRVSPACE.EXE /CHKDSK` | gated on legal MS-DOS staging |
| **HAMMER / HAMMER2**             | DragonFly BSD only — no Linux validator             | manual QEMU+DragonFly path documented in skip-stub tests |
| **ext1**                         | soft `dumpe2fs` magic-rejection witness             | no `mkfs.ext1` exists (1992 magic retired in 1993); round-trip via own reader/writer only |

The matrix is self-documenting via tests in `Compression.Tests/ExternalFsInteropTests.cs` — each
gate skips cleanly with the exact `sudo apt install -y` package name when its tool is missing,
so CI can run on a host with only `e2fsprogs` and the rest skip without failing.

## Disk-image container validation matrix

`qemu-img` is the canonical disk-container validator (it parses VHD / VMDK / QCOW2 / VDI / VHDX
with the same loader path that `qemu-system-*` uses to boot a VM). Tests gate on either the
Windows-native binary or the WSL package — install `sudo apt install -y qemu-utils` (smaller
than `qemu-system-*` — only ships the image tools).

| Container | State                  | Forward (`qemu-img check`) | Round-trip (`qemu-img convert -O raw`) | Reverse (`qemu-img create` → our reader) |
| --------- | ---------------------- | -------------------------- | -------------------------------------- | ---------------------------------------- |
| **VHD**   | WORM                   | accepts                    | preserves content                      | reads back                               |
| **VMDK**  | WORM                   | accepts                    | preserves content                      | reads back                               |
| **QCOW2** | WORM                   | accepts                    | preserves content                      | reads back                               |
| **VDI**   | WORM                   | accepts                    | preserves content                      | reads back                               |
| **VHDX**  | R only (writer pending)| —                          | —                                      | reads back                               |
| **VDFS**  | R                      | n/a — proprietary Gothic   | n/a                                    | n/a                                      |

The full forensic-style chain (`Vmdk_ContainingExt_RoundTripExtractsFiles`) builds an ext FS
in-memory with two known-content files, wraps it in a VMDK, optionally validates with
`qemu-img check`, then walks the chain back via our own readers (`VmdkReader.Extract` →
`ExtReader`) asserting both files come out byte-equal — runs unconditionally; qemu-img is a
bonus, not a gate.

## Why no `R/W` for VHDX yet, why no `mkfs.ext1`?

The state column reflects what the **writer** can do; readers cover everything the table
mentions. WORM means we ship a spec-compliant image creator that round-trips through the real
external tool; R means the writer either is not implemented or does not yet pass external
validation. Where the upstream format has no validator (ext1, HAMMER, MFS, retro 8-bit FSes,
proprietary game FSes), we round-trip through our own reader/writer pair plus byte-level
spec-offset assertion tests.

## Filesystem-aware recovery

`FilesystemCarver` (in `Compression.Analysis`) scans an image for known superblock signatures
at canonical offsets (ext at +1080, FAT at +54 / +82, SquashFS at 0, APFS at +32, Btrfs at
+0x10020 …), validates each hit by asking the matching reader to walk it, and extracts every
readable entry — useful for SD-card / disk-dump recovery when the partition table is gone but
the inner filesystem superblock survives.

```csharp
using var fs = File.OpenRead("sdcard.img");
var hits = new FilesystemCarver().CarveStream(fs);
foreach (var c in hits) {
  var result = FilesystemExtractor.ExtractCarved(fs, c, $"out/{c.FormatId}_0x{c.ByteOffset:X}");
  Console.WriteLine($"{c.FormatId}: {result.FilesExtracted} files, {result.FilesFailed} failed");
}
```

CLI: `cwb recover sdcard.img` (auto), `cwb recover raw.img --mode filesystems --out out/`,
`cwb recover raw.img --mode files --format Jpeg,Png` (photorec-style file carving).

## Versioning

Version-locked 1:1 with `Hawkynt.Compression.Core`. Pin both at the same version.

## License

LGPL-3.0-or-later. See the source repository for the full license text.
