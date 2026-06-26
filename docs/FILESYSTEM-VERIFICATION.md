# Filesystem verification matrix

Every filesystem format in the repository, with how its on-disk artefact is proven. The bar: a *real* external program (a live OS kernel under QEMU, the filesystem’s own fsck/inspect utility, or its canonical third-party reader/writer) must accept what we produce — not just our own round-trip.

Re-run by `QemuLinuxMountTests` (kernel mounts), `ExternalRetroToolTests` (vintage tools), and the `ExternalConformance`/`ExternalFsInterop` suites (host fsck/inspect tools).

## Tiers

| Tier | Meaning |
|------|---------|
| **RW** | Read/write proven by a real OS kernel under QEMU: it mounts our image read-write, reads our files byte-exact, and writes new files our reader recovers. |
| **RO** | Read-only proven by a real external program: a live OS kernel (under QEMU) mounts our image and reads it byte-exact, OR the filesystem’s canonical third-party tool writes an image our reader extracts byte-exact. Automated by QemuLinuxMountTests / ExternalRetroToolTests / the BSD oracle. |
| **FSCK** | Validated by the filesystem’s own real check/inspect tool (e2fsck, btrfs check, reiserfsck, fsck.minix, fsck.hfsplus, hfsutils, ntfs-3g ntfsls/ntfsinfo, mke2fs/mkfs round-trip) in the host conformance suite. |
| **SPEC** | Real on-disk format with a faithful writer, but NO external tool exists in any reachable environment (mainframe, Apple MFS/APFS, and assorted retro/embedded formats with no extractable utility). Validated by our reader + on-disk struct-parity unit tests — the only proof possible. |
| **DETECT** | Detection-only: recognizes the real on-disk or on-the-wire signature of an existing filesystem and surfaces metadata + raw bytes. No image writer (network/distributed/pseudo protocols, or read-only on-disk readers). There is no self-contained image to mount. |
| **SIM** | Simplified / NOT tool-validated: a writer that stamps the authentic superblock magic over a simplified layout no external tool reads. Honest caveat — representations, not faithful images. Candidates for a real writer or kept as documented approximations. |

## Summary

| Tier | Count |
|------|-------|
| RW | 9 |
| RO | 31 |
| FSCK | 10 |
| SPEC | 39 |
| DETECT | 142 |
| SIM | 11 |
| **Total** | **242** |

**50 of 242 formats are proven by a real external tool.** 39 are real formats with no reachable external tool (reader + struct-parity only); 142 are detection-only (no writable image); 11 are simplified/not-tool-validated.

## RW — 9 filesystems

| Id | Project | Proof |
|----|---------|-------|
| DoubleSpace | FileSystem.DoubleSpace | real MS-DOS 6.22 DRVSPACE driver (QEMU) mounts our GenuineCvfWriter CVF and reads our files byte-exact; driver-written files our reader recovers byte-exact |
| DriveSpace | FileSystem.DoubleSpace | real MS-DOS 6.22 DRVSPACE driver (QEMU) mounts our genuine CVF and reads single+multi-cluster files byte-exact; we also read real DRVSPACE-created CVFs byte-exact |
| ExFat | FileSystem.ExFat | Linux kernel exfat mount r/w + fsck.exfat |
| F2fs | FileSystem.F2fs | Linux kernel f2fs mount r/w + fsck.f2fs |
| Fat | FileSystem.Fat | Linux kernel vfat mount r/w + fsck.fat + mtools |
| Hammer | FileSystem.Hammer | DragonFly kernel HAMMER mount r/w (qemu) |
| Hammer2 | FileSystem.Hammer2 | DragonFly kernel HAMMER2 mount r/w (qemu) |
| Ufs | FileSystem.Ufs | FreeBSD kernel UFS mount r/w + fsck_ffs (qemu) |
| Xfs | FileSystem.Xfs | Linux kernel xfs mount r/w + xfs_repair |

## RO — 31 filesystems

| Id | Project | Proof |
|----|---------|-------|
| Adf | FileSystem.Adf | amitools xdftool: canonical Amiga FFS image read by our AdfReader |
| AppleDos | FileSystem.AppleDos | dos33fsprogs DOS 3.3 disk read by our AppleDosReader |
| ApplePascal | FileSystem.ApplePascal | AppleCommander Pascal image read by our ApplePascalReader |
| Atari8 | FileSystem.Atari8 | atari-tools ATR (Atari DOS 2.0s) read by our Atari8Reader |
| Coherent | FileSystem.Coherent | Linux kernel sysv mount (detect_coherent, PDP-endian) reads our image byte-exact (qemu) |
| Cpm | FileSystem.Cpm | cpmtools mkfs.cpm/cpmcp IBM-3740 image read by our CpmReader |
| CramFs | FileSystem.CramFs | util-linux mkfs.cramfs image read by our CramFsReader |
| D64 | FileSystem.D64 | cbmconvert: 1541 image read by our D64Reader |
| D71 | FileSystem.D71 | cbmconvert: 1571 image read by our D71Reader |
| D81 | FileSystem.D81 | cbmconvert: 1581 image read by our D81Reader |
| Efs | FileSystem.Efs | Linux kernel efs mount, reads byte-exact (qemu) |
| Erofs | FileSystem.Erofs | erofs-utils mkfs.erofs image read by our ErofsReader |
| FatPlus | FileSystem.FatPlus | Linux kernel vfat mount reads our image byte-exact (qemu) |
| Gemdos | FileSystem.Gemdos | Linux kernel msdos mount reads our image byte-exact (qemu) |
| Iso | FileSystem.Iso | Linux kernel iso9660 mount + isoinfo/xorriso (qemu) |
| Jffs2 | FileSystem.Jffs2 | mtd-utils mkfs.jffs2 image read by our Jffs2FileReader |
| Jfs | FileSystem.Jfs | Linux kernel jfs mount + fsck.jfs (qemu) |
| Lif | FileSystem.Lif | lifutils LIF volume read by our LifReader |
| LittleFs | FileSystem.LittleFs | mklittlefs (littlefs v2.11) image read by our LittleFsReader |
| Nilfs2 | FileSystem.Nilfs2 | Linux kernel nilfs2 mount reads our writer's image byte-exact (host loop-mount); our reader also re-validates a real mkfs.nilfs2 superblock (crc32_le) |
| Ocfs2 | FileSystem.Ocfs2 | mkfs.ocfs2 + kernel-written image (qemu) read by our Ocfs2 descriptor |
| Os9Rbf | FileSystem.Os9Rbf | toolshed os9 RBF disk read by our Os9RbfReader |
| ProDos | FileSystem.ProDos | AppleCommander ProDOS image read by our ProDosReader |
| RomFs | FileSystem.RomFs | genromfs image read by our RomFsReader |
| Spiffs | FileSystem.Spiffs | igrr mkspiffs image read by our SpiffsReader |
| SquashFs | FileSystem.SquashFs | Linux kernel squashfs mount + unsquashfs (qemu) |
| SysV | FileSystem.SysV | Linux kernel sysv mount, reads byte-exact (qemu) |
| TFat | FileSystem.TFat | Linux kernel vfat mount reads our image byte-exact (qemu) |
| Udf | FileSystem.Udf | Linux kernel udf mount + mkudffs round-trip (qemu) |
| Xenix | FileSystem.Xenix | Linux kernel sysv mount (detect_xenix) reads our image byte-exact (qemu) |
| Yaffs2 | FileSystem.Yaffs2 | mkyaffs2image NAND image read by our Yaffs2 scanner |

## FSCK — 10 filesystems

| Id | Project | Proof |
|----|---------|-------|
| Btrfs | FileSystem.Btrfs | btrfs check + mutate-clean |
| Ext | FileSystem.Ext | e2fsck/dumpe2fs/debugfs + mke2fs round-trip |
| Ext1 | FileSystem.Ext1 | dumpe2fs accepts as ext2 |
| Hfs | FileSystem.Hfs | hfsutils hls lists our files |
| HfsPlus | FileSystem.HfsPlus | fsck.hfsplus + hfsutils |
| MinixFs | FileSystem.MinixFs | mkfs.minix round-trip |
| MinixV1 | FileSystem.MinixV1 | fsck.minix clean on our in-place-modified image + Linux kernel minix mount reads the in-place-added file byte-exact (host loop-mount) |
| MinixV2 | FileSystem.MinixV2 | fsck.minix clean on our in-place-modified image + Linux kernel minix mount reads the in-place-added file byte-exact (host loop-mount) |
| Ntfs | FileSystem.Ntfs | ntfs-3g ntfsls/ntfsinfo + ntfsfix |
| ReiserFs | FileSystem.ReiserFs | reiserfsck + mutate-clean |

## SPEC — 39 filesystems

| Id | Project |
|----|---------|
| Adfs | FileSystem.Adfs |
| AmigaPfs | FileSystem.AmigaPfs |
| Apfs | FileSystem.Apfs |
| Bbc | FileSystem.Bbc |
| BcacheFs | FileSystem.BcacheFs |
| Bfs | FileSystem.Bfs |
| BootFs | FileSystem.BootFs |
| CpcDsk | FileSystem.CpcDsk |
| Cromemco | FileSystem.Cromemco |
| DragonFs | FileSystem.DragonFs |
| DriveSpace3 | FileSystem.DriveSpace3 |
| Fatx | FileSystem.Fatx |
| Flex | FileSystem.Flex |
| G64 | FileSystem.CbmNibble |
| Gfs1 | FileSystem.Gfs1 |
| Hpfs | FileSystem.Hpfs |
| Htfs | FileSystem.Htfs |
| Human68k | FileSystem.Human68k |
| Jfs1 | FileSystem.Jfs1 |
| Lfs | FileSystem.Lfs |
| Mfs | FileSystem.Mfs |
| Mfs1 | FileSystem.Mfs1 |
| Msa | FileSystem.Msa |
| Nib | FileSystem.CbmNibble |
| Nilfs1 | FileSystem.Nilfs1 |
| Ods1 | FileSystem.Ods1 |
| Omfs | FileSystem.Omfs |
| Pc98 | FileSystem.Pc98 |
| Qnx4 | FileSystem.Qnx4 |
| Qnx6 | FileSystem.Qnx6 |
| Reiser4 | FileSystem.Reiser4 |
| Rt11 | FileSystem.Rt11 |
| Sfs | FileSystem.Sfs |
| Ti99 | FileSystem.Ti99 |
| TrDos | FileSystem.TrDos |
| Trsdos | FileSystem.Trsdos |
| Vdfs | FileSystem.Vdfs |
| Zfs | FileSystem.Zfs |
| ZxScl | FileSystem.ZxScl |

## DETECT — 142 filesystems

| Id | Project |
|----|---------|
| AdvFs | FileSystem.AdvFs |
| Alluxio | FileSystem.NetFs |
| AmazonS3 | FileSystem.NetFs |
| AndrewFs | FileSystem.NetFs |
| Aufs | FileSystem.NetFs |
| Avere | FileSystem.NetFs |
| Axfs | FileSystem.NetFs |
| Barracuda | FileSystem.NetFs |
| BeeGfs | FileSystem.BeeGfs |
| Bpam | FileSystem.NetFs |
| Bsam | FileSystem.NetFs |
| Bwfs | FileSystem.NetFs |
| Cdfs | FileSystem.NetFs |
| CephFs | FileSystem.CephFs |
| Cfs | FileSystem.NetFs |
| ChironFs | FileSystem.NetFs |
| CloudStore | FileSystem.NetFs |
| Cloudian | FileSystem.NetFs |
| Cms | FileSystem.NetFs |
| Coda | FileSystem.NetFs |
| Configfs | FileSystem.NetFs |
| Cxfs | FileSystem.Cxfs |
| DCacheFs | FileSystem.NetFs |
| Davfs2 | FileSystem.NetFs |
| DceDfs | FileSystem.NetFs |
| Debugfs | FileSystem.NetFs |
| DellFluid | FileSystem.NetFs |
| Devfs | FileSystem.NetFs |
| Ecryptfs | FileSystem.Ecryptfs |
| EmFile | FileSystem.NetFs |
| EmcHighRoad | FileSystem.NetFs |
| EncFs | FileSystem.NetFs |
| ExtremeFfs | FileSystem.NetFs |
| Fal | FileSystem.NetFs |
| Ffs2 | FileSystem.NetFs |
| Freenet | FileSystem.NetFs |
| FtpFs | FileSystem.NetFs |
| FuseFs | FileSystem.NetFs |
| Gfarm | FileSystem.NetFs |
| Gfs2 | FileSystem.Gfs2 |
| GlusterFs | FileSystem.GlusterFs |
| GmailFs | FileSystem.NetFs |
| GoogleGfs | FileSystem.NetFs |
| Gpfs | FileSystem.Gpfs |
| GridFs | FileSystem.NetFs |
| GsOs | FileSystem.GsOs |
| Hdfs | FileSystem.NetFs |
| Hmdfs | FileSystem.NetFs |
| Ibm4690 | FileSystem.NetFs |
| IbmCos | FileSystem.NetFs |
| IbmSan | FileSystem.NetFs |
| Ibrix | FileSystem.NetFs |
| Intermezzo | FileSystem.NetFs |
| Ipfs | FileSystem.NetFs |
| JesFs | FileSystem.NetFs |
| Jffs1 | FileSystem.NetFs |
| JuiceFs | FileSystem.JuiceFs |
| Kernfs | FileSystem.NetFs |
| LizardFs | FileSystem.NetFs |
| Lnfs | FileSystem.NetFs |
| LogFs | FileSystem.NetFs |
| Lsfs | FileSystem.NetFs |
| Ltfs | FileSystem.NetFs |
| Lufs | FileSystem.NetFs |
| Lustre | FileSystem.Lustre |
| Magma | FileSystem.NetFs |
| MaprFs | FileSystem.NetFs |
| MooseFs | FileSystem.MooseFs |
| MsDfs | FileSystem.NetFs |
| Mts | FileSystem.NetFs |
| Mvfs | FileSystem.NetFs |
| Nasan | FileSystem.NetFs |
| Ncp | FileSystem.NetFs |
| Nexfs | FileSystem.NetFs |
| Nfs | FileSystem.NetFs |
| NineP | FileSystem.NetFs |
| Nss | FileSystem.Nss |
| Nwfs | FileSystem.Nwfs |
| Nwfs386 | FileSystem.Nwfs386 |
| ObjectiveFs | FileSystem.NetFs |
| OfficeGroove | FileSystem.NetFs |
| OioFs | FileSystem.NetFs |
| OneFs | FileSystem.OneFs |
| OpenVms | FileSystem.OpenVms |
| OracleAcfs | FileSystem.NetFs |
| OrangeFs | FileSystem.OrangeFs |
| Os4000 | FileSystem.NetFs |
| Os4000Linked | FileSystem.NetFs |
| OverlayFs | FileSystem.NetFs |
| PalmNvfs | FileSystem.NetFs |
| PanFs | FileSystem.NetFs |
| Pick | FileSystem.NetFs |
| Procfs | FileSystem.NetFs |
| Puffs | FileSystem.NetFs |
| Pvfs | FileSystem.NetFs |
| Qfs | FileSystem.NetFs |
| Qsam | FileSystem.NetFs |
| Quobyte | FileSystem.NetFs |
| Refs | FileSystem.Refs |
| Reliance | FileSystem.NetFs |
| RelianceEdge | FileSystem.NetFs |
| RelianceNitro | FileSystem.NetFs |
| RelianceVelocity | FileSystem.NetFs |
| Rfs | FileSystem.NetFs |
| RozoFs | FileSystem.NetFs |
| Scality | FileSystem.NetFs |
| Scfs | FileSystem.NetFs |
| ScoutFs | FileSystem.NetFs |
| SmartFs | FileSystem.SmartFs |
| Smb | FileSystem.NetFs |
| Smb2 | FileSystem.NetFs |
| Soup | FileSystem.NetFs |
| SshFs | FileSystem.NetFs |
| Stacker | FileSystem.Stacker |
| StorNext | FileSystem.NetFs |
| Sysctlfs | FileSystem.NetFs |
| Sysfs | FileSystem.NetFs |
| TahoeLafs | FileSystem.TahoeLafs |
| Tfs | FileSystem.Tfs |
| ThreeFs | FileSystem.NetFs |
| Tmpfs | FileSystem.NetFs |
| TrueFfs | FileSystem.NetFs |
| Tux2 | FileSystem.Tux2 |
| Tux3 | FileSystem.Tux3 |
| Ubifs | FileSystem.Ubifs |
| Umsdos | FileSystem.NetFs |
| UnionFs | FileSystem.NetFs |
| Uvfat | FileSystem.NetFs |
| Vfs | FileSystem.NetFs |
| Vmfs | FileSystem.NetFs |
| Vsam | FileSystem.NetFs |
| Vtoc | FileSystem.NetFs |
| VxFs | FileSystem.VxFs |
| Wafl | FileSystem.Wafl |
| Wikifs | FileSystem.NetFs |
| WinFs | FileSystem.NetFs |
| WindowsEfs | FileSystem.NetFs |
| Xsan | FileSystem.NetFs |
| XtreemFs | FileSystem.NetFs |
| ZosHfs | FileSystem.NetFs |
| ZosZfs | FileSystem.NetFs |
| ZvmByteFs | FileSystem.NetFs |

## SIM — 11 filesystems

| Id | Project |
|----|---------|
| AthFs | FileSystem.DiskVariants |
| Chfs | FileSystem.DiskVariants |
| Ext3Cow | FileSystem.DiskVariants |
| Fossil | FileSystem.Fossil |
| Next3 | FileSystem.DiskVariants |
| NextFs | FileSystem.DiskVariants |
| Nova | FileSystem.Nova |
| SkyFs | FileSystem.DiskVariants |
| TivoMfs | FileSystem.DiskVariants |
| Venti | FileSystem.Venti |
| Yaffs1 | FileSystem.DiskVariants |


## Pending proof upgrades

- **Ntfs** — mounts under `ntfs3` (RC=0) but readdir lists an empty root; root `$I30` index fix needed. Already proven RO via `ntfs-3g` (FSCK tier).
- **CpcDsk** — cpmtools+libdsk round-trips a CPC `.dsk` but our reader expects a different EDSK geometry.
- **Pc98** — its NEC IPL stores the FAT BPB at offset 0x80 (not the standard 0x0B), so the kernel msdos driver does not accept it; proving it RO needs either a standard-BPB variant writer or a real PC-98 disk-image oracle (Xenix and Coherent are now proven RO via the sysv driver's detect_xenix / detect_coherent).
- **DoubleSpace / DriveSpace are now RW** — `GenuineCvfWriter` emits a real MSDBL6.0 CVF the genuine MS-DOS 6.22 DRVSPACE driver (QEMU) mounts and reads byte-exact (single- and multi-cluster files), the driver writes new files our reader recovers byte-exact, and we read real DRVSPACE-created CVFs byte-exact. **DriveSpace3** (Win95 Plus! Pack, MS-LZH codec) still needs a Win95 guest to produce an oracle; **Stacker** is a separate proprietary format (Stac LZS) with no DOS-bundled tool — both remain SPEC pending sourced software.
- **Reiser4 / BcacheFs / Zfs** — need `reiser4progs` / `bcachefs-tools` / `zdb` (none installable here; ZFS has no kernel module either).
- **Jfs1** — the only reachable JFS tool/driver is JFS2 (Linux `jfs`). It does NOT accept our JFS1 (OS/2) image: the kernel mount fails (`mount -t jfs` → "wrong fs type, bad superblock"), and `fsck.jfs 1.1.15` reports *"Unable to read primary superblock … Superblock is corrupt"* — it probes the JFS2 superblock offsets (32768/61440) which JFS1 does not use. No JFS1-aware external tool exists in any reachable environment; stays SPEC (reader + struct-parity).
- **Gfs1** — the only reachable GFS tool/driver is GFS2 (Linux `gfs2`). It rejects our GFS1 image: `mount -t gfs2` fails with kernel log *"gfs2: Unknown on-disk format, unable to mount"* (GFS1 `sb_fmt`/`sb_multihost_format` differ from GFS2). No GFS1-aware tool exists; stays SPEC.
- **Adfs** — Linux `adfs` driver not in the Alpine guest module set.
- Remaining SPEC formats — elevate where an extractable third-party tool exists; otherwise none does.

## How the proofs run

- **Kernel-mount (RW/RO):** `Compression.Tests/QemuLinuxMountTests.cs` boots a headless Alpine guest (`Support/QemuLinuxRunner.cs`), mounts each image with the real kernel, asserts an unforgeable per-fs content marker. UFS/HAMMER/HAMMER2 use the BSD oracle (`Support/QemuRunner.cs`). On a Linux host whose kernel carries the module, `Compression.Tests/KernelMount/InPlaceRwKernelMountTests.cs` loop-mounts our writer's image directly with the host driver (no QEMU) and reads the in-place-added file byte-exact — currently proving MinixV1, MinixV2 (`minix`) and Nilfs2 (`nilfs2`); it skips cleanly when sudo/losetup/mount or the module is unavailable.
- **Vintage/embedded tools (RO):** `Compression.Tests/ExternalRetroToolTests.cs` drives `xdftool` (amitools), `cbmconvert`, `cpmtools` and `mkfs.cramfs` — the real tool writes a canonical image our reader extracts byte-exact.
- **fsck/inspect (FSCK):** `ExternalConformance*` / `ExternalFsInteropTests` drive host `e2fsck`, `xfs_repair`, `btrfs check`, `fsck.f2fs/jfs`, `reiserfsck`, `fsck.minix`, `fsck.hfsplus`, `hfsutils`, `ntfs-3g`, `mkudffs`, `unsquashfs`, `mtools`, `qemu-img`.
- **SPEC:** each filesystem's own reader + on-disk struct-parity unit tests — the only proof possible when no external tool for that format exists anywhere.
