# Hawkynt.FileFormats.FileSystems

[![NuGet](https://img.shields.io/nuget/v/Hawkynt.FileFormats.FileSystems.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.FileSystems/)
[![NuGet downloads](https://img.shields.io/nuget/dt/Hawkynt.FileFormats.FileSystems.svg)](https://www.nuget.org/packages/Hawkynt.FileFormats.FileSystems/)
[![License](https://img.shields.io/github/license/Hawkynt/CompressionWorkbench)](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE)
[![CI](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Hawkynt/CompressionWorkbench/actions/workflows/ci.yml)
![Target](https://img.shields.io/badge/target-net10.0-blue)

> Pure-managed filesystem handling for .NET, without mounting anything through the host OS. The
> package claims the WHOLE domain — every filesystem and disk-image container, modern, legacy,
> virtual-machine, optical, forensic and retro-computing alike — not a selection of it. Where a
> filesystem is missing, read-only, or write-without-mutation that is a tracked gap, recorded in the
> support matrix below and in
> [`docs/FILESYSTEM-VERIFICATION.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/FILESYSTEM-VERIFICATION.md).

## 📦 Installation

```bash
dotnet add package Hawkynt.FileFormats.FileSystems
```

The package bundles the filesystem and disk-image `FileSystem.*` / `FileFormat.*` assemblies while taking `Hawkynt.Compression.Core` as the shared NuGet dependency.

## ✨ Features

- Inspect filesystems in-process without `libguestfs`, loop mounts, kernel drivers, or elevated privileges.
- Cross-platform parsing: inspect NTFS from Linux, ext filesystems from Windows, HFS+ from either, and so on.
- Fresh filesystem-image creation for many formats, with true modification semantics where implemented.
- True in-place bcachefs add/replace/remove and purge for the supported single-device profile: unchanged file extents stay at the same physical sectors while allocation, freespace, backpointer and accounting metadata is committed in the reserved metadata zone.
- Disk-image container support for VM, optical, forensic, firmware, and emulator workflows.
- Layout/cluster/block-size optimization, defragmentation, and unused-space/slack wiping on supporting filesystems.
- External conformance validation against real filesystem tools where the platform/test environment provides them.

## 🧩 Support matrix

| State | Meaning |
| --- | --- |
| **R** | Open/read/walk only. |
| **WORM** | Read plus create a fresh image; no supported mutation of an existing image. |
| **R/W** | Read plus supported modification semantics. Some implementations rebuild rather than edit blocks in place. |
| **⚠️** | Deliberate structural/profile subset. |

The descriptor's implemented interfaces and `FormatCapabilities` are authoritative for the exact state in a particular build. The tables below document the package surface without inferring capabilities from roadmap text.

### Disk-image / firmware containers

| Container | State | Scope | Reference |
| --- | :---: | --- | --- |
| [VHD](https://en.wikipedia.org/wiki/VHD_(file_format)) | WORM | Fixed, dynamic, and differencing VHD paths | [Microsoft VHD overview](https://learn.microsoft.com/windows-server/virtualization/hyper-v/manage/manage-hyper-v-virtual-hard-disks) |
| [VHDX](https://en.wikipedia.org/wiki/VHD_(file_format)#VHDX) | R | Hyper-V VHDX reader; current writer state follows its descriptor | [MS-VHDX](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-vhdx/) |
| [VMDK](https://en.wikipedia.org/wiki/VMDK) | WORM | VMware virtual disks | [VMware Virtual Disk API](https://developer.broadcom.com/xapis/virtual-disk-api/latest/) |
| [VDI](https://en.wikipedia.org/wiki/VirtualBox#Virtual_disk_image) | WORM | VirtualBox disk images | [VirtualBox storage documentation](https://www.virtualbox.org/manual/ch05.html) |
| [QCOW2](https://en.wikipedia.org/wiki/Qcow) | WORM | QEMU copy-on-write images | [QEMU QCOW2 specification](https://www.qemu.org/docs/master/interop/qcow2.html) |
| [Apple DMG](https://en.wikipedia.org/wiki/Apple_Disk_Image) | WORM | Apple disk-image container | [Apple disk images](https://developer.apple.com/library/archive/documentation/DeveloperTools/Conceptual/SoftwareDistribution4/Concepts/sd_disk_images.html) |
| [BIN/CUE](https://en.wikipedia.org/wiki/Cue_sheet_(computing)) | WORM | Raw optical tracks + cue sheet | [CUE sheet background](https://wiki.hydrogenaud.io/index.php?title=Cue_sheet) |
| [CSO](https://en.wikipedia.org/wiki/CSO_(file_format)) | R | Compressed ISO image | [CSO overview](https://en.wikipedia.org/wiki/CSO_(file_format)) |
| [Expert Witness Format](https://en.wikipedia.org/wiki/EnCase#Expert_Witness_File_Format) | R | EWF/EnCase forensic images | [libewf documentation](https://github.com/libyal/libewf/tree/main/documentation) |
| [UEFI Firmware Volume](https://en.wikipedia.org/wiki/UEFI) | R | Firmware volume / FFS-oriented inspection | [UEFI specification](https://uefi.org/specifications) |
| [Device Tree Blob](https://en.wikipedia.org/wiki/Devicetree) | R | Flattened Device Tree property traversal | [Devicetree specification](https://www.devicetree.org/specifications/) |
| [Intel HEX](https://en.wikipedia.org/wiki/Intel_HEX) / [S-record](https://en.wikipedia.org/wiki/SREC) | R | Firmware records normalized to payload + metadata | [Intel HEX description](https://www.keil.com/support/docs/1584/_hlp_hexfile.htm) / [S-record manual](https://srecord.sourceforge.net/man/man5/srec_motorola.5.html) |

### Microsoft / DOS filesystems

| Filesystem | State | Scope | Reference |
| --- | :---: | --- | --- |
| [FAT12/16/32](https://en.wikipedia.org/wiki/File_Allocation_Table) | R/W | FAT variants + long filenames | [Microsoft FAT specification](https://download.microsoft.com/download/1/6/1/161ba512-40e2-4cc9-843a-923143f3456c/fatgen103.doc) |
| [exFAT](https://en.wikipedia.org/wiki/ExFAT) | R/W | exFAT image creation/read/write | [Microsoft exFAT specification](https://learn.microsoft.com/windows/win32/fileio/exfat-specification) |
| [NTFS](https://en.wikipedia.org/wiki/NTFS) | R/W | MFT/system metadata, supported compression and modification paths | [MS-FSCC](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/) |
| [ReFS](https://en.wikipedia.org/wiki/ReFS) | R ⚠️ | Header/boot-sector oriented subset | [Microsoft ReFS overview](https://learn.microsoft.com/windows-server/storage/refs/refs-overview) |
| [HPFS](https://en.wikipedia.org/wiki/High_Performance_File_System) | R/W | Rebuild-based mutation/extent handling | [OS/2 Museum HPFS](https://www.os2museum.com/wp/the-hpfs-disk-layout/) |
| [DriveSpace / DoubleSpace](https://en.wikipedia.org/wiki/DriveSpace) | R/W | Compressed-volume-file workflows | [DriveSpace overview](https://en.wikipedia.org/wiki/DriveSpace) |

### Unix / Linux filesystems

| Filesystem | State | Scope | Reference |
| --- | :---: | --- | --- |
| [ext2/ext3/ext4](https://en.wikipedia.org/wiki/Ext4) | R/W | Extended filesystem family | [Linux ext4 documentation](https://www.kernel.org/doc/html/latest/filesystems/ext4/) |
| [Btrfs](https://en.wikipedia.org/wiki/Btrfs) | R/W | Tree/chunk-based filesystem paths | [Btrfs on-disk format](https://btrfs.readthedocs.io/en/latest/dev/On-disk-format.html) |
| [XFS](https://en.wikipedia.org/wiki/XFS) | R/W | XFS v5-oriented image workflows | [XFS documentation](https://kernel.org/doc/html/latest/filesystems/xfs/index.html) |
| [ReiserFS](https://en.wikipedia.org/wiki/ReiserFS) | R/W | ReiserFS 3.6; rebuild-based mutation | [Linux ReiserFS documentation](https://www.kernel.org/doc/html/latest/filesystems/reiserfs.html) |
| [JFS](https://en.wikipedia.org/wiki/JFS_(file_system)) | R/W | IBM/Linux JFS paths | [JFS project](http://jfs.sourceforge.net/) |
| [F2FS](https://en.wikipedia.org/wiki/F2FS) | R/W | Flash-Friendly File System | [Linux F2FS documentation](https://www.kernel.org/doc/html/latest/filesystems/f2fs.html) |
| [ZFS](https://en.wikipedia.org/wiki/ZFS) | R/W | Supported OpenZFS-style structures; rebuild-based mutation where documented | [OpenZFS documentation](https://openzfs.github.io/openzfs-docs/) |
| [JFFS2](https://en.wikipedia.org/wiki/JFFS2) | R/W | Log-structured flash filesystem | [Linux JFFS2 documentation](https://www.kernel.org/doc/html/latest/filesystems/jffs2.html) |
| [UBIFS](https://en.wikipedia.org/wiki/UBIFS) | R | Read path only | [Linux UBIFS documentation](https://www.kernel.org/doc/html/latest/filesystems/ubifs.html) |
| [bcachefs](https://en.wikipedia.org/wiki/Bcachefs) | R/W ⚠️ | Native b-trees; true in-place add/replace/remove + purge; in-place defrag/optimize and wipe/clean; alloc/freespace/backpointer/accounting metadata kept consistent. Mutation is limited to the supported single-device regular-extent profile. | [bcachefs](https://bcachefs.org/) |
| [AdvFS](https://en.wikipedia.org/wiki/AdvFS) | R/W | Tru64 UNIX Advanced File System; `.advfs` | [AdvFS technical overview](https://en.wikipedia.org/wiki/AdvFS) |
| [MINIX V1](https://en.wikipedia.org/wiki/MINIX_file_system) | R/W | Original 14-character-name MINIX filesystem; `.minix1` | [MINIX filesystem](https://en.wikipedia.org/wiki/MINIX_file_system) |
| [MINIX V2](https://en.wikipedia.org/wiki/MINIX_file_system) | R/W | 30-character-name MINIX revision; `.minix2` | [MINIX filesystem](https://en.wikipedia.org/wiki/MINIX_file_system) |
| [NILFS2](https://en.wikipedia.org/wiki/NILFS) | R/W | Log-structured filesystem with continuous checkpoints; `.nilfs2` | [NILFS project](https://nilfs.sourceforge.io/) |
| [Tux2](https://en.wikipedia.org/wiki/Tux2) | R/W | Phase-tree filesystem; `.tux2` | [Tux2 design notes](https://en.wikipedia.org/wiki/Tux2) |
| [Tux3](https://en.wikipedia.org/wiki/Tux3) | R/W | Versioning filesystem successor to Tux2; `.tux3` | [Tux3 project](https://github.com/OGAWAHirofumi/tux3) |
| [VxFS](https://en.wikipedia.org/wiki/Veritas_File_System) | WORM | Veritas File System; read and fresh create, no in-place mutation; `.vxfs` | [Veritas File System](https://en.wikipedia.org/wiki/Veritas_File_System) |
| [SmartFS](https://nuttx.apache.org/docs/latest/components/filesystem.html) | WORM | NuttX flash filesystem; read and fresh create, no in-place mutation; `.smartfs` | [NuttX SmartFS](https://nuttx.apache.org/docs/latest/components/filesystem.html) |

### Apple / optical / portable filesystems

| Filesystem | State | Scope | Reference |
| --- | :---: | --- | --- |
| [HFS](https://en.wikipedia.org/wiki/Hierarchical_File_System_(Apple)) | R/W | Classic Macintosh HFS | [Inside Macintosh: Files](https://developer.apple.com/library/archive/documentation/mac/Files/Files-2.html) |
| [HFS+](https://en.wikipedia.org/wiki/HFS_Plus) | R/W | Catalog-tree and nested-directory support | [Apple TN1150](https://developer.apple.com/library/archive/technotes/tn/tn1150.html) |
| [APFS](https://en.wikipedia.org/wiki/Apple_File_System) | R/W | Supported container/filesystem tree and modification paths | [Apple File System Reference](https://developer.apple.com/support/downloads/Apple-File-System-Reference.pdf) |
| [ISO 9660](https://en.wikipedia.org/wiki/ISO_9660) | R/W | Optical-disc filesystem images | [ECMA-119](https://ecma-international.org/publications-and-standards/standards/ecma-119/) |
| [UDF](https://en.wikipedia.org/wiki/Universal_Disk_Format) | R/W | Universal Disk Format images | [OSTA UDF specifications](https://osta.org/specs/) |
| [SquashFS](https://en.wikipedia.org/wiki/SquashFS) | R/W | Compressed filesystem image; mutation is rebuild-oriented | [SquashFS documentation](https://docs.kernel.org/filesystems/squashfs.html) |
| [CramFS](https://en.wikipedia.org/wiki/Cramfs) | R/W | Compressed ROM filesystem; mutation is rebuild-oriented | [Linux cramfs documentation](https://www.kernel.org/doc/html/latest/filesystems/cramfs.html) |
| [EROFS](https://en.wikipedia.org/wiki/EROFS) | WORM | Enhanced read-only filesystem images | [EROFS documentation](https://erofs.docs.kernel.org/) |

### Retro / emulator filesystems

| Family | State | Examples | Reference |
| --- | :---: | --- | --- |
| [Commodore disk images](https://en.wikipedia.org/wiki/Commodore_DOS) | R/W | D64, D71, D81 and related media | [VICE disk image docs](https://vice-emu.sourceforge.io/vice_17.html) |
| [Apple DOS / ProDOS](https://en.wikipedia.org/wiki/Apple_DOS) | R/W | Apple II disk filesystems | [ProDOS technical reference](https://prodos8.com/docs/techref/) |
| [CP/M](https://en.wikipedia.org/wiki/CP/M) | R/W | Canonical CP/M 2.2 8-inch SSSD geometry; descriptor implements create/modify as well as read | [CP/M filesystem notes](https://www.seasip.info/Cpm/format22.html) |
| [TR-DOS](https://en.wikipedia.org/wiki/TR-DOS) | WORM | ZX Spectrum disk images | [TR-DOS notes](https://sinclair.wiki.zxnet.co.uk/wiki/TR-DOS) |
| [RT-11](https://en.wikipedia.org/wiki/RT-11) | R | DEC RT-11 filesystem images | [RT-11 documentation archive](https://bitsavers.org/pdf/dec/pdp11/rt11/) |
| [DragonDOS](https://en.wikipedia.org/wiki/Dragon_32/64) | R/W | Dragon 32/64 disk filesystem; `.dfs` | [Dragon Data archive](https://en.wikipedia.org/wiki/Dragon_32/64) |
| [PlayStation memory card](https://en.wikipedia.org/wiki/PlayStation_technical_specifications#Memory_Card) | R/W | PS1 save blocks; `.mcr` | [PS1 memory card format](https://www.psdevwiki.com/ps3/PS1_Memory_Card) |
| [TFAT](https://learn.microsoft.com/previous-versions/windows/embedded/aa911939(v=msdn.10)) | R/W | Transaction-safe FAT used by Windows CE; `.tfat` | [Transaction-Safe FAT](https://learn.microsoft.com/previous-versions/windows/embedded/aa911939(v=msdn.10)) |

## 🚀 Quick start

### Walk a filesystem image

```csharp
using FileSystem.Fat;

var image = File.ReadAllBytes("disk.img");
using var fs = new FatReader(image);
foreach (var entry in fs.ListRecursive())
  Console.WriteLine($"{entry.Path} {entry.Size,10} {entry.Modified:O}");
```

### Open a virtual disk and inspect its inner filesystem

```csharp
using FileFormat.Vhd;
using FileSystem.Ntfs;

using var stream = File.OpenRead("system.vhd");
var inner = new VhdReader().OpenContents(stream);
foreach (var partition in inner.Partitions) {
  if (partition.Type != "NTFS")
    continue;
  var ntfs = new NtfsReader(partition.Open());
  foreach (var path in ntfs.WalkPaths())
    Console.WriteLine(path);
}
```

## 🧭 When to use this package

Use it for forensic/archival inspection, cross-platform disk analysis, retro-computing media, cloud/VM image inspection, firmware/test-image construction, filesystem layout experiments, recovery, and conversions where mounting through the host OS is undesirable or impossible.

It is not a replacement for `System.IO` when the volume is already mounted, and it is not a kernel filesystem driver with live concurrent journaling guarantees.

Creatable filesystem implementations build real nested directory trees rather than flattening paths. Implementations that expose the relevant interfaces can also wipe unused space/cluster-tip slack, defragment extents, and participate in layout/cluster-size optimization. Those are per-descriptor capabilities, not assumptions applied to every format.

## 📚 Complete disk-image container inventory

| Descriptor | Details | Reference |
| --- | --- | --- |
| `FileFormat.Vhd` | Microsoft VHD v1; fixed/dynamic/differencing paths | [VHD](https://en.wikipedia.org/wiki/VHD_(file_format)) |
| `FileFormat.Vhdx` | VHDX reader; exact writer capability follows the current descriptor | [MS-VHDX](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-vhdx/) |
| `FileFormat.Vmdk` | VMware Virtual Machine Disk | [VMDK](https://en.wikipedia.org/wiki/VMDK) |
| `FileFormat.Vdi` | Oracle VirtualBox Disk Image | [VirtualBox](https://en.wikipedia.org/wiki/VirtualBox) |
| `FileFormat.Qcow2` | QEMU Copy-On-Write v2 | [QCOW](https://en.wikipedia.org/wiki/Qcow) |
| `FileFormat.Dmg` | Apple Disk Image | [Apple Disk Image](https://en.wikipedia.org/wiki/Apple_Disk_Image) |
| `FileFormat.Cso` | Compressed ISO, PSP/homebrew | [CSO](https://en.wikipedia.org/wiki/CSO_(file_format)) |
| `FileFormat.BinCue` | CD/DVD raw optical tracks + cue sheet | [Cue sheet](https://en.wikipedia.org/wiki/Cue_sheet_(computing)) |
| `FileFormat.Mdf` | Alcohol 120% Media Disc Format | [Alcohol 120%](https://en.wikipedia.org/wiki/Alcohol_120%25) |
| `FileFormat.Nrg` | Nero Burning ROM image | [Nero Burning ROM](https://en.wikipedia.org/wiki/Nero_Burning_ROM) |
| `FileFormat.Cdi` | DiscJuggler image | [DiscJuggler](https://en.wikipedia.org/wiki/DiscJuggler) |
| `FileFormat.Pfs0` | Nintendo Switch PartitionFS / firmware packaging |
| `FileFormat.UImage` | U-Boot uImage | [Das U-Boot](https://en.wikipedia.org/wiki/Das_U-Boot) |
| `FileFormat.UefiFv` | UEFI firmware volume | [UEFI](https://en.wikipedia.org/wiki/UEFI) |
| `FileFormat.Ipsw` | Apple iOS/iPadOS firmware archive | [IPSW](https://en.wikipedia.org/wiki/IPSW) |
| `FileFormat.Ewf` | Expert Witness Format / EnCase forensic image | [EnCase](https://en.wikipedia.org/wiki/EnCase) |
| `FileFormat.T64` | Commodore 64 tape archive; modification is rebuild-oriented | [Commodore DOS](https://en.wikipedia.org/wiki/Commodore_DOS) |
| `FileFormat.Tap` | Sinclair/Commodore tape image; modification is rebuild-oriented |
| `FileFormat.Dtb` | Device Tree Blob / overlay; walks FDT properties as pseudo-archive | [Devicetree](https://en.wikipedia.org/wiki/Devicetree) |
| `FileFormat.FirmwareHex` | Intel HEX, Motorola S-Record and TI-TXT normalized to `firmware.bin` + metadata | [Intel HEX](https://en.wikipedia.org/wiki/Intel_HEX) |

## 📚 Complete filesystem inventory

The long-form table preserves the original implementation detail, but avoids freezing stale state letters for every row. For exact current R/W/WORM/R capability, inspect the descriptor's `FormatCapabilities`/implemented interfaces; where the state is material to a curated row above it is stated explicitly.

### Microsoft / Windows

| Descriptor | Implementation detail | Reference |
| --- | --- | --- |
| `FileSystem.Fat` | FAT12/FAT16/FAT32, LFN, BPB, 0x55AA signature, FATGEN-oriented writer | [FAT](https://en.wikipedia.org/wiki/File_Allocation_Table) |
| `FileSystem.ExFat` | exFAT VBR + boot-checksum handling | [exFAT](https://en.wikipedia.org/wiki/ExFAT) |
| `FileSystem.Ntfs` | NTFS MFT/system metadata, USA fixup, LZNT1 paths | [NTFS](https://en.wikipedia.org/wiki/NTFS) |
| `FileSystem.Refs` | ReFS header/boot-sector subset | [ReFS](https://en.wikipedia.org/wiki/ReFS) |
| `FileSystem.Hpfs` | OS/2 HPFS, rebuild-based add/remove, defrag, extent map | [HPFS](https://en.wikipedia.org/wiki/High_Performance_File_System) |
| `FileSystem.Htfs` | SCO HTFS: `s_magic=0x012FD15D`, S5-style superblock/inodes, 16-byte dirents, nested dirs, 512/1024/2048-byte blocks, defrag/purge/layout options | [HTFS](https://en.wikipedia.org/wiki/High_Throughput_File_System) |
| `FileSystem.DoubleSpace` | DOS 6 DoubleSpace/DriveSpace CVF stored-run paths, rebuild-based modify | [DriveSpace](https://en.wikipedia.org/wiki/DriveSpace) |

### Unix / Linux

| Descriptor | Implementation detail | Reference |
| --- | --- | --- |
| `FileSystem.Btrfs` | CRC-32C, chunk tree and SYSTEM/METADATA/DATA structures | [Btrfs](https://en.wikipedia.org/wiki/Btrfs) |
| `FileSystem.Ext` | ext2/ext3/ext4 DYNAMIC_REV/FILETYPE-oriented writer | [ext4](https://en.wikipedia.org/wiki/Ext4) |
| `FileSystem.Xfs` | XFS v5, AGF/AGI/AGFL and B-tree structures | [XFS](https://en.wikipedia.org/wiki/XFS) |
| `FileSystem.Ext1` | 1992 ext1 magic `0xEF51`; rebuild-oriented modification; no current `mkfs.ext1` validator exists | [Extended FS](https://en.wikipedia.org/wiki/Extended_file_system) |
| `FileSystem.ReiserFs` | ReiserFS 3.6, multi-leaf S+tree, R5-hashed keys, nested directories; rebuild mutation | [ReiserFS](https://en.wikipedia.org/wiki/ReiserFS) |
| `FileSystem.Reiser4` | Empty-filesystem creation path based on reference blocks; full object-tree authoring is not implied | [Reiser4](https://en.wikipedia.org/wiki/Reiser4) |
| `FileSystem.Jfs` | IBM JFS, nested dirs with external dtree B+ pages, secondary AIT/AIM handling | [JFS](https://en.wikipedia.org/wiki/JFS_(file_system)) |
| `FileSystem.F2fs` | Superblock/checkpoint/SIT/NAT/SSA and hash-bucket directory blocks | [F2FS](https://en.wikipedia.org/wiki/F2FS) |
| `FileSystem.Zfs` | fat-ZAP directories, Fletcher-4, big-endian XDR labels; rebuild-based supported mutation | [ZFS](https://en.wikipedia.org/wiki/ZFS) |
| `FileSystem.Ufs` | BSD UFS reader, `fs_magic=0x011954` path | [UFS](https://en.wikipedia.org/wiki/Unix_File_System) |
| `FileSystem.BcacheFs` | Native b-tree reader/writer plus true in-place CRUD for the supported single-device regular-extent profile. Unchanged file data stays at its physical sectors; add/replace allocate free buckets; remove/purge zero released extents; alloc/freespace/backpointer/accounting trees are committed in the metadata reservation. In-place defrag/optimize and wipe/clean are supported. | [bcachefs](https://en.wikipedia.org/wiki/Bcachefs) |
| `FileSystem.Ubifs` | UBIFS log-structured read path; LPT/TNC writer complexity is intentionally not guessed | [UBIFS](https://en.wikipedia.org/wiki/UBIFS) |
| `FileSystem.Jffs2` | JFFS2 log-structured paths, rebuild-oriented mutation | [JFFS2](https://en.wikipedia.org/wiki/JFFS2) |
| `FileSystem.Yaffs2` | YAFFS2 rebuild-oriented mutation + defrag paths | [YAFFS](https://en.wikipedia.org/wiki/YAFFS) |
| `FileSystem.Bfs` | BeFS single-AG B+ tree, rebuild-oriented mutation | [Be File System](https://en.wikipedia.org/wiki/Be_File_System) |
| `FileSystem.Hammer` | DragonFly HAMMER reader; validator requires DragonFly environment | [HAMMER](https://en.wikipedia.org/wiki/HAMMER_(file_system)) |
| `FileSystem.Hammer2` | DragonFly HAMMER2 reader | [HAMMER2](https://en.wikipedia.org/wiki/HAMMER2) |
| `FileSystem.Ocfs2` | OCFS2 paths, rebuild-oriented supported mutation | [OCFS2](https://en.wikipedia.org/wiki/OCFS2) |
| `FileSystem.Nwfs` | Novell NetWare filesystem paths | [NSS](https://en.wikipedia.org/wiki/Novell_Storage_Services) |
| `FileSystem.Efs` | SGI EFS: `fs_magic=0x00072959`, single-CG inode table, single-extent files, nested dirs, defrag/purge/layout options | [EFS](https://en.wikipedia.org/wiki/Extent_File_System) |
| `FileSystem.Gfs1` | Sistina GFS pre-GFS2: multihost-format superblock, dinodes, lock protocol/table options | [GFS2](https://en.wikipedia.org/wiki/GFS2) |
| `FileSystem.Jfs1` | OS/2 JFS1 discriminator (`JFS1`, version 1), 256-byte dinodes, configurable blocks, defrag/purge/layout options | [JFS](https://en.wikipedia.org/wiki/JFS_(file_system)) |

### Apple / classic Mac

| Descriptor | Implementation detail | Reference |
| --- | --- | --- |
| `FileSystem.HfsPlus` | HFS+ catalog B-tree, TN1150 case-folding order, nested dirs; rebuild mutation | [HFS+](https://en.wikipedia.org/wiki/HFS_Plus) |
| `FileSystem.Hfs` | Classic HFS catalog/extents trees; rebuild mutation | [HFS](https://en.wikipedia.org/wiki/Hierarchical_File_System_(Apple)) |
| `FileSystem.Apfs` | Single container/volume path with NXSB/APSB, object map and FS-tree B-tree; supported rebuild mutation | [APFS](https://en.wikipedia.org/wiki/Apple_File_System) |
| `FileSystem.Mfs` | Macintosh File System (1984), `drSigWord=0xD2D7`; rebuild mutation | [MFS](https://en.wikipedia.org/wiki/Macintosh_File_System) |

### Compressed / embedded / flash

| Descriptor | Implementation detail | Reference |
| --- | --- | --- |
| `FileSystem.SquashFs` | zlib/compressed SquashFS paths; supported mutation uses rebuild semantics | [SquashFS](https://en.wikipedia.org/wiki/SquashFS) |
| `FileSystem.CramFs` | CramFS `0x28CD3D45`, CRC-32, zlib; rebuild mutation | [cramfs](https://en.wikipedia.org/wiki/Cramfs) |
| `FileSystem.RomFs` | `-rom1fs-` big-endian ROMFS; rebuild mutation despite the on-disk format's read-only role | [romfs](https://en.wikipedia.org/wiki/Romfs) |
| `FileSystem.MinixFs` | Minix v1/v2/v3 superblock families; rebuild mutation | [MINIX FS](https://en.wikipedia.org/wiki/MINIX_file_system) |
| `FileSystem.Erofs` | EROFS compact-inode + FLAT_PLAIN creation path with nested directories | [EROFS](https://en.wikipedia.org/wiki/EROFS) |
| `FileSystem.LittleFs` | LittleFS metadata-pair commit log, CTZ/inline files, nested directories, commit-walking reader | [littlefs](https://github.com/littlefs-project/littlefs) |

### Optical

| Descriptor | Implementation detail | Reference |
| --- | --- | --- |
| `FileSystem.Iso` | ISO 9660 + Joliet, PVD/SVD, UCS-2 long names, multi-sector dirs, L/M path tables; supported mutation uses rebuild and wiping semantics | [ISO 9660](https://en.wikipedia.org/wiki/ISO_9660) |
| `FileSystem.Udf` | ECMA-167/UDF, VRS@16-18, AVDP@256, CRC-16-XMODEM; rebuild mutation | [UDF](https://en.wikipedia.org/wiki/Universal_Disk_Format) |
| `FileSystem.Sfs` | Amiga Smart File System root-block surface; full object-container B+ tree/bitmap/hash-table support is not inferred | [SFS](https://en.wikipedia.org/wiki/Smart_File_System) |

### Retro / vintage

| Descriptor | Implementation detail | Reference |
| --- | --- | --- |
| `FileSystem.D64` / `D71` / `D81` | Commodore 1541/1571/1581 directories and rebuild modification | [Commodore DOS](https://en.wikipedia.org/wiki/Commodore_DOS) |
| `FileSystem.CbmNibble` | Raw G64/NIB, writer GCR-encodes a D64-built 1541 image | [Commodore DOS](https://en.wikipedia.org/wiki/Commodore_DOS) |
| `FileSystem.AppleDos` | Apple DOS 3.3, catalog at T17S15; rebuild mutation | [Apple DOS](https://en.wikipedia.org/wiki/Apple_DOS) |
| `FileSystem.ProDos` | ProDOS storage trees; rebuild mutation | [ProDOS](https://en.wikipedia.org/wiki/ProDOS) |
| `FileSystem.Atari8` | Atari DOS 2 VTOC/sector model; rebuild mutation | [Atari DOS](https://en.wikipedia.org/wiki/Atari_DOS) |
| `FileSystem.Bbc` | BBC DFS/ADFS paths; rebuild mutation | [DFS](https://en.wikipedia.org/wiki/Disk_Filing_System) |
| `FileSystem.Cpm` | CP/M 2.2 canonical 8-inch SSSD geometry; current descriptor implements list/extract/create/modify/test and additional layout/defrag/wipe surfaces | [CP/M](https://en.wikipedia.org/wiki/CP/M) |
| `FileSystem.CpcDsk` | Amstrad CPC DSK / `MV - CPCEMU Disk-File` |
| `FileSystem.TrDos` | ZX Spectrum TR-DOS image | [TR-DOS](https://en.wikipedia.org/wiki/TR-DOS) |
| `FileSystem.ZxScl` | Spectrum SCL, `SINCLAIR` magic + checksum; rebuild mutation | [TR-DOS](https://en.wikipedia.org/wiki/TR-DOS) |
| `FileSystem.Adf` | Amiga Disk Format DOS\1, BSDsum checksums; rebuild mutation | [ADF](https://en.wikipedia.org/wiki/Amiga_Disk_File) |
| `FileSystem.Msa` | Atari ST Magic Shadow Archive, BE magic 0x0E0F | [Atari ST](https://en.wikipedia.org/wiki/Atari_ST) |

### Mainframe / minicomputer and other historical systems

| Descriptor | Implementation detail | Reference |
| --- | --- | --- |
| `FileSystem.Lif` | HP Logical Interchange Format, 256-byte sectors | [LIF](https://en.wikipedia.org/wiki/Logical_Interchange_Format) |
| `FileSystem.OpenVms` | OpenVMS Files-11 ODS-2/ODS-5 home-block path | [Files-11](https://en.wikipedia.org/wiki/Files-11) |
| `FileSystem.Os9Rbf` | Microware OS-9 Random Block File | [OS-9](https://en.wikipedia.org/wiki/OS-9) |
| `FileSystem.Rt11` | DEC RT-11 filesystem path | [RT-11](https://en.wikipedia.org/wiki/RT-11) |
| `FileSystem.Vdfs` | Gothic-engine VDFS archive/filesystem surface | [Gothic](https://en.wikipedia.org/wiki/Gothic_(series)) |

## 🕵️ Detection/header-only and opaque-payload tier

These descriptors intentionally do not advertise creation/modification when the available evidence only supports detection, a header subset, or an opaque encrypted/distributed payload. This table preserves the reasons instead of inventing missing on-disk semantics.

| Descriptor | Current documented scope | Why it stops there |
| --- | --- | --- |
| `FileSystem.Tfs` | Detection | BBN Trans-FS has no usable public on-disk spec in the repository evidence set. |
| `FileSystem.Mfs1` | Detection | Acorn MFS-1 identification is heuristic/extension-led; deeper support needs period documentation. |
| `FileSystem.Nwfs386` | Detection | NetWare 386 raw-partition structures are proprietary and not guessed. |
| `FileSystem.Stacker` | Detection + SCB/opaque inner payload | Full upgrade needs Stacker LZS + inner FAT delegation. |
| `FileSystem.DriveSpace3` | Detection + MDBPB/opaque compressed region | Full upgrade needs the actual DS compression/MDFAT structures. |
| `FileSystem.GsOs` | Header/wrapper | Apple IIgs 2IMG wrapper can delegate to inner ProDOS/HFS/DOS 3.3 readers. |
| `FileSystem.TahoeLafs` | Detection | Share payloads are capability-encrypted by design; no read-cap means no plaintext. |
| `FileSystem.Ecryptfs` | Detection | Payload is encrypted; decryption requires actual key/passphrase/EFEK metadata. |
| `FileSystem.OrangeFs` | Detection | A single PVFS/OrangeFS server object is insufficient to reconstruct a distributed filesystem without cluster config/striping state. |

## 🧪 Filesystem validation matrix

Selected writers/readers are tested against external filesystem utilities where available. Tool absence causes the corresponding external-interop test to skip rather than convert a missing host dependency into a false product failure. The exact current test suite is authoritative; this table retains the long-form verification context.

| Filesystem | External validation | Expected/evidenced behavior |
| --- | --- | --- |
| ext4 | `fsck.ext4 -fnv` | Clean image path; reverse `mkfs.ext4` reader coverage also exists |
| ext4 | `dumpe2fs -h` | Superblock/magic/UUID inspection |
| FAT12/16/32 | `fsck.fat -n -V`, reverse `mkfs.vfat` | Forward and reverse interoperability paths |
| FAT | FreeDOS `CHKDSK` under DOSBox-X (`[Explicit]`) | Optional historical-validator path |
| exFAT | `fsck.exfat -n` | Clean forward validation path |
| SquashFS | `unsquashfs -s` | Superblock accepted |
| XFS v5 | `xfs_repair -n -f` | Repair-tool validation path |
| Btrfs | `btrfs check --readonly` | Read-only checker path |
| JFS | `fsck.jfs -n -f -v` | Gated on `jfsutils` |
| NTFS | `ntfsfix --no-action`, `ntfsinfo`, `ntfsls`, reverse `mkfs.ntfs` | Gated on `ntfs-3g` |
| HFS+ | `fsck.hfsplus -d -f -n`, reverse `mkfs.hfsplus` | Gated on `hfsprogs` |
| HFS classic | `hmount` / `hls` | Historical notes record a malformed B-tree report against the writer; current tests/source decide present status |
| ZFS | `zdb -l` | Label/NVList parsing path, gated on ZFS userland tools |
| UFS1/FFS | Linux mount when kernel supports UFS; optional FreeBSD `fsck_ffs` under QEMU | Often unavailable on stock WSL kernels |
| bcachefs | `bcachefs show-super`, `bcachefs fsck -n`, internal alloc/freespace/backpointer witness tests | Fresh images and supported in-place CRUD/defrag/purge metadata commits are checked for b-tree/allocation consistency; external bcachefs tools remain the authority where installed. |
| Reiser4 | `fsck.reiser4` / `mkfs.reiser4` | Empty-FS/reference-block path, gated on `reiser4progs` |
| DoubleSpace / DriveSpace | DOSBox-X + DOS utilities when legally staged | Optional historical-validator path |
| HAMMER / HAMMER2 | DragonFly BSD | Linux lacks the canonical validator/mount stack |
| ext1 | soft magic/rejection witness | No `mkfs.ext1` exists; internal/spec tests provide evidence instead |

See [`docs/FILESYSTEM-VERIFICATION.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/docs/FILESYSTEM-VERIFICATION.md) and the current `Compression.Tests` external-filesystem tests for the exact executable gates and assertions.

## 🧪 Disk-image container validation

`qemu-img` is used where suitable because it exercises QEMU's real disk-container parsers. The test environment may use the Windows binary or the WSL `qemu-utils` package.

| Container | Forward check | Raw round-trip | Reverse image → package reader |
| --- | --- | --- | --- |
| VHD | `qemu-img check` path | `qemu-img convert -O raw` path | `qemu-img create` coverage |
| VMDK | check path | raw conversion path | reverse-created image coverage |
| QCOW2 | check path | raw conversion path | reverse-created image coverage |
| VDI | check path | raw conversion path | reverse-created image coverage |
| VHDX | Reader interoperability path | Depends on current writer capability | reverse-created image coverage |

A forensic-style integration path builds an inner filesystem with known files, wraps it in a disk container, optionally validates the container externally, then walks it back through the package readers and compares extracted file bytes.

## 🧯 Filesystem-aware recovery

`FilesystemCarver` in `Compression.Analysis` scans raw images for known superblock signatures at canonical offsets, asks the matching reader to validate each candidate, and can then extract readable entries. This is useful when a partition table is lost but an inner filesystem superblock survives.

```csharp
using var fs = File.OpenRead("sdcard.img");
var hits = new FilesystemCarver().CarveStream(fs);
foreach (var c in hits) {
  var result = FilesystemExtractor.ExtractCarved(
    fs,
    c,
    $"out/{c.FormatId}_0x{c.ByteOffset:X}");
  Console.WriteLine($"{c.FormatId}: {result.FilesExtracted} files, {result.FilesFailed} failed");
}
```

CLI examples:

```text
cwb recover sdcard.img
cwb recover raw.img --mode filesystems --out out/
cwb recover raw.img --mode files --format Jpeg,Png
```

## 📚 Write-state model

Filesystem “write support” is deliberately split by what the implementation actually promises.

| State | Practical meaning |
| --- | --- |
| **WORM** | Build a new valid image from files/metadata; useful for tests, firmware, reproducible images, and conversion. |
| **R/W** | Existing contents can be changed through the package's supported mutation model; some implementations extract/rebuild rather than journal blocks live. |
| **R** | Inspection only. |

This package is an image-manipulation toolkit, not a kernel filesystem driver. R/W does **not** imply concurrent mount semantics, crash-consistent journaling under arbitrary interruption, or drop-in replacement for the OS driver.

## 🔖 Versioning

The filesystem package is built against the repository's shared Core version. Release tooling determines concrete package versions; consume mutually compatible package versions rather than relying on a prose prediction.

## 📚 API reference

<!-- API:BEGIN generated by Hawkynt/RepositoryTemplate/package-readme — edit the XML docs in source, not here -->

Every public and protected member of all 833 types, generated from the built assembly and its XML documentation, is in [REFERENCE.md](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.FileFormats.FileSystems/REFERENCE.md).

<!-- API:END -->

## 🔌 Dependencies

| Dependency | Role |
| --- | --- |
| [`Hawkynt.Compression.Core`](https://www.nuget.org/packages/Hawkynt.Compression.Core/) | Compression, checksums, bit I/O, partition helpers, and shared registry primitives |
| Host filesystem drivers / `libguestfs` | **Not required at runtime.** |
| External `fsck`/repair/mkfs/qemu tools | Optional validation dependencies in tests, not runtime package dependencies |

## ⚠️ Limitations

- Some modern filesystems are intentionally partial; a readable superblock or WORM creator is not presented as full R/W support.
- bcachefs mutation is deliberately profile-gated: the in-place writer currently owns single-device, generation-zero, regular pointer extents as emitted by this package. Foreign volumes with extra live b-trees, reused bucket generations, inline/reflink/compressed/other extent-key forms, or unsupported inode/dirent object types are refused for mutation rather than rewritten speculatively; read support remains broader.
- R/W can be rebuild-based rather than live in-place journaling. Block placement, journals, snapshots, reflinks, quotas, encryption and crash consistency are format-specific capabilities. bcachefs' supported R/W profile is an explicit exception here: its CRUD and layout maintenance paths are true in-place operations.
- Disk-image container support and inner-filesystem support are separate capabilities.
- External-validator parity varies by platform and available tooling; tests are the current evidence source.
- Historical deep-reference prose can become stale as capabilities improve. Descriptor interfaces/`FormatCapabilities`, code and tests take precedence over an older state label.
- Unknown proprietary or encrypted structures are not inferred from names or roadmap intent.

## ❤️ Support

If this project saves you time or money, consider supporting its development:

[![GitHub Sponsors](https://img.shields.io/badge/GitHub-Sponsor-EA4AAA?logo=githubsponsors)](https://github.com/sponsors/Hawkynt)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?logo=paypal)](https://www.paypal.me/hawkynt)

## 📜 License

Licensed under LGPL-3.0-or-later — see the repository [LICENSE](https://github.com/Hawkynt/CompressionWorkbench/blob/main/LICENSE).
