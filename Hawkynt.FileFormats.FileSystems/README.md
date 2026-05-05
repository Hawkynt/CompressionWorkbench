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
- **WORM-creatable images**: build minimal filesystem images for tests / fuzzing / firmware
  packaging — see the source repo's WSL-validated mkfs-parity tests

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
| `FileFormat.Vhd`    | WORM  | Microsoft Virtual Hard Disk v1 — fixed / dynamic / differencing |
| `FileFormat.Vhdx`   | WORM  | VHD v2 (Win 8+) — 64 TB max, log-based crash consistency        |
| `FileFormat.Vmdk`   | WORM  | VMware Virtual Machine Disk                                     |
| `FileFormat.Vdi`    | WORM  | Oracle VirtualBox Disk Image                                    |
| `FileFormat.Qcow2`  | WORM  | QEMU Copy-On-Write v2                                           |
| `FileFormat.Dmg`    | WORM  | Apple Disk Image                                                |
| `FileFormat.Cso`    | R     | Compressed ISO (PSP / homebrew)                                 |
| `FileFormat.BinCue` | WORM  | CD/DVD raw track + `.cue` sheet                                 |
| `FileFormat.Mdf`    | WORM  | Alcohol 120% Media Disc Format                                  |
| `FileFormat.Nrg`    | WORM  | Nero burning ROM image                                          |
| `FileFormat.Cdi`    | WORM  | DiscJuggler image                                               |
| `FileFormat.Pfs0`   | WORM  | Nintendo Switch firmware filesystem                             |
| `FileFormat.UImage` | R     | U-Boot uImage (embedded boot images)                            |
| `FileFormat.UefiFv` | R     | UEFI firmware volume                                            |
| `FileFormat.Ipsw`   | R     | Apple iOS / iPadOS firmware archive                             |
| `FileFormat.Ewf`    | R     | Expert Witness Format / EnCase forensic image                   |
| `FileFormat.T64`    | WORM  | Commodore 64 tape archive                                       |
| `FileFormat.Tap`    | WORM  | Sinclair / Commodore tape image                                 |
| `FileFormat.Dtb`    | R     | Device Tree Blob (`.dtb` / `.dtbo`) — FDT v17, walks property tree as pseudo-archive |
| `FileFormat.FirmwareHex` | R | Intel HEX (`.hex` / `.ihex`), Motorola S-Record (`.s19` / `.s28` / `.s37` / `.srec` / `.mot`), TI-TXT (MSP430) — ASCII firmware records decoded to flat `firmware.bin` + metadata |

### Filesystem readers / writers (`FileSystem.*`)

#### Microsoft / Windows

| Filesystem               | State | Notes                                                                          |
| ------------------------ | ----- | ------------------------------------------------------------------------------ |
| `FileSystem.Fat`         | R/W   | FAT12 / FAT16 / FAT32, LFN — full BPB, 0x55 0xAA signature, FATGEN-compliant   |
| `FileSystem.ExFat`       | R/W   | Microsoft exFAT — full VBR, boot-checksum sector (§3.1.3)                      |
| `FileSystem.Ntfs`        | R/W   | NTFS — all 16 system MFT files, USA fixup, LZNT1 compression                   |
| `FileSystem.Refs`        | R     | Resilient File System (Server 2012+) — header + boot sector parse only         |
| `FileSystem.Hpfs`        | R     | OS/2 High Performance File System                                              |
| `FileSystem.DoubleSpace` | WORM  | DOS 6 DoubleSpace / DriveSpace CVF — stored runs only, JM/LZ77 is TODO         |

#### Unix / Linux

| Filesystem            | State | Notes                                                                  |
| --------------------- | ----- | ---------------------------------------------------------------------- |
| `FileSystem.Btrfs`    | R/W   | B-tree filesystem — CRC-32C, real chunk tree (SYSTEM/METADATA/DATA)    |
| `FileSystem.Ext`      | R/W   | ext2 / ext3 / ext4 — DYNAMIC_REV, FILETYPE feature, files at inode 11  |
| `FileSystem.Xfs`      | R/W   | XFS v5 — `xfs_repair`-validated, AGF/AGI/AGFL, full B-tree set         |
| `FileSystem.Ext1`     | WORM  | ext1 — Theodore Ts'o 1992 original (magic 0xEF51, no `mkfs.ext1` exists) |
| `FileSystem.ReiserFs` | WORM  | ReiserFS 3.6 — spec-correct offsets, `s_root_block@+8`                  |
| `FileSystem.Reiser4`  | WORM  | Reiser4 — empty-FS only via 7 byte-exact reference blocks              |
| `FileSystem.Jfs`      | R     | IBM JFS — superblock + inode table parse only                          |
| `FileSystem.F2fs`     | WORM  | Flash-Friendly Filesystem — superblock + checkpoint + SIT + NAT + Main |
| `FileSystem.Zfs`      | R     | Sun ZFS — read existing pools                                          |
| `FileSystem.Ufs`      | R     | UNIX File System (BSD) — `fs_magic=0x011954`                           |
| `FileSystem.BcacheFs` | WORM  | bcachefs — superblock-only WORM (fsck parity multi-week, B-trees TODO) |
| `FileSystem.Ubifs`    | R     | UBIFS — log-structured, no writer (LPT/TNC trees multi-week)           |
| `FileSystem.Jffs2`    | R     | JFFS2 — log-structured node-scanner only                               |
| `FileSystem.Yaffs2`   | R     | YAFFS2 — OOB/ECC layout not emittable                                  |
| `FileSystem.Bfs`      | R     | BeFS — superblock surfacing only                                       |
| `FileSystem.Hammer`   | R     | DragonFly HAMMER — DragonFly BSD only, no Linux validator              |
| `FileSystem.Hammer2`  | R     | DragonFly HAMMER 2                                                     |
| `FileSystem.Ocfs2`    | R     | Oracle Cluster Filesystem 2                                            |
| `FileSystem.Nwfs`     | R     | Novell NetWare                                                         |

#### Apple / classic Mac

| Filesystem           | State | Notes                                                                 |
| -------------------- | ----- | --------------------------------------------------------------------- |
| `FileSystem.HfsPlus` | WORM  | Mac OS Extended — TN1150-compliant catalog file record (248 B)        |
| `FileSystem.Hfs`     | WORM  | Classic Mac OS HFS — real B-tree catalog + extents trees              |
| `FileSystem.Apfs`    | R     | Apple File System (macOS High Sierra+) — single-container/volume read |
| `FileSystem.Mfs`     | WORM  | Macintosh File System (1984) — pre-HFS flat FS, `drSigWord=0xD2D7`    |

#### Compressed / embedded / flash

| Filesystem            | State | Notes                                                            |
| --------------------- | ----- | ---------------------------------------------------------------- |
| `FileSystem.SquashFs` | WORM  | Compressed FS used in AppImage / live ISOs — zlib + Adler-32     |
| `FileSystem.CramFs`   | WORM  | Compressed RAM-FS for embedded — `0x28CD3D45`, CRC-32, zlib      |
| `FileSystem.RomFs`    | WORM  | Read-only ROM FS — `-rom1fs-` magic, BE fields                   |
| `FileSystem.MinixFs`  | WORM  | Minix v1/2/3 — superblock magics `0x137F/0x138F/0x2468/...`      |
| `FileSystem.Erofs`    | R     | Enhanced Read-Only FS (Android) — variable-length encoded inodes |
| `FileSystem.LittleFs` | R     | LittleFS for microcontrollers — superblock surfacing only        |

#### Optical

| Filesystem       | State | Notes                                                                 |
| ---------------- | ----- | --------------------------------------------------------------------- |
| `FileSystem.Iso` | WORM  | ISO 9660 + Joliet — PVD@16, VDST@17, L+M path tables                  |
| `FileSystem.Udf` | WORM  | UDF (DVD / Blu-ray) — ECMA-167, VRS@16-18, AVDP@256, CRC-16-XMODEM    |
| `FileSystem.Sfs` | R     | Smart File System (Amiga) — superblock walk only                      |

#### Retro / vintage

| Filesystem                       | State | Notes                                                       |
| -------------------------------- | ----- | ----------------------------------------------------------- |
| `FileSystem.D64` / `D71` / `D81` | WORM  | Commodore 1541 / 1571 / 1581 — directory at T18S1+          |
| `FileSystem.CbmNibble`           | R     | Commodore raw nibble (`.g64` / `.nib`)                      |
| `FileSystem.AppleDos`            | WORM  | Apple DOS 3.3 — 143 360 bytes, catalog at T17S15            |
| `FileSystem.ProDos`              | WORM  | ProDOS — 143 360 / 819 200, storage-type-3 trees up to 32MB |
| `FileSystem.Atari8`              | WORM  | Atari 8-bit DOS 2 — 16-byte hdr + VTOC at sector 360        |
| `FileSystem.Bbc`                 | WORM  | BBC Micro DFS / ADFS — 102 400 / 204 800 bytes              |
| `FileSystem.Cpm`                 | R     | CP/M 2.2 — 256 256 bytes, 64-entry flat directory           |
| `FileSystem.CpcDsk`              | WORM  | Amstrad CPC DSK — `MV - CPCEMU Disk-File`                   |
| `FileSystem.TrDos`               | WORM  | Soviet ZX Spectrum TR-DOS — 655 360 bytes                   |
| `FileSystem.ZxScl`               | WORM  | Spectrum SCL — `SINCLAIR` magic + LE32 sum                  |
| `FileSystem.Adf`                 | WORM  | Amiga Disk Format — DOS\1 magic, BSDsum checksums           |
| `FileSystem.Msa`                 | WORM  | Atari ST Magic Shadow Archive — 0x0E0F BE magic             |

#### Mainframe / minicomputer

| Filesystem            | State | Notes                                                  |
| --------------------- | ----- | ------------------------------------------------------ |
| `FileSystem.Lif`      | R     | HP Logical Interchange Format — 256-byte sectors       |
| `FileSystem.OpenVms`  | R     | OpenVMS Files-11 (ODS-2 / ODS-5) — home block read only |
| `FileSystem.Os9Rbf`   | R     | OS-9 Random Block File — Microware OS-9 Tech Reference |
| `FileSystem.Rt11`     | R     | DEC RT-11 — 256 KB RX01 8" SSSD                        |
| `FileSystem.Vdfs`     | R     | Gothic-engine FS — proprietary, no public spec         |

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
