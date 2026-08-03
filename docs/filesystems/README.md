# Filesystems

One page per filesystem, generated from the implementation: the verbs it
offers, how a volume is laid out, what parameters it takes and where the
format is documented. A test regenerates them and fails on drift, so a page
cannot claim something the code does not do.

| Filesystem | Defragments by | Wipes | Edits in place |
|---|---|---|---|
| [ADF](Adf.md) | moving (`AdfBlockMover`) | yes | yes |
| [Acorn ADFS](Adfs.md) | moving (`AdfsBlockMover`) | no | yes |
| [AdvFS (Tru64 UNIX)](AdvFs.md) | moving (`AdvFsBlockMover`) | yes | yes |
| [Amiga Professional FS](AmigaPfs.md) | moving (`AmigaPfsBlockMover`) | yes | yes |
| [APFS](Apfs.md) | rebuilding | yes | yes |
| [Apple DOS 3.3](AppleDos.md) | moving (`AppleDosBlockMover`) | yes | yes |
| [Apple UCSD Pascal](ApplePascal.md) | moving (`ApplePascalBlockMover`) | yes | yes |
| [ATR (Atari 8-bit)](Atari8.md) | moving (`Atari8BlockMover`) | yes | yes |
| [BBC DFS](Bbc.md) | moving (`BbcBlockMover`) | yes | yes |
| [BcacheFS](BcacheFs.md) | moving (`BcacheFsBlockMover`) | yes | yes |
| [BFS](Bfs.md) | moving (`BfsBlockMover`) | yes | yes |
| [Btrfs Filesystem Image](Btrfs.md) | moving (`BtrfsBlockMover`) | yes | yes |
| [Coherent FS](Coherent.md) | rebuilding | no | yes |
| [CPC DSK](CpcDsk.md) | moving (`CpcDskBlockMover`) | yes | yes |
| [CP/M 2.2 (8" SSSD)](Cpm.md) | moving (`CpmBlockMover`) | yes | yes |
| [CramFS](CramFs.md) | moving (`CramFsBlockMover`) | yes | yes |
| [Cromemco RDOS](Cromemco.md) | moving (`CromemcoBlockMover`) | yes | yes |
| [D64](D64.md) | moving (`D64BlockMover`) | yes | yes |
| [D71](D71.md) | moving (`D71BlockMover`) | yes | yes |
| [D81](D81.md) | moving (`D81BlockMover`) | yes | yes |
| [DoubleSpace CVF](DoubleSpace.md) | moving (`DoubleSpaceBlockMover`) | yes | yes |
| [DragonFS](DragonFs.md) | moving (`DragonFsBlockMover`) | yes | yes |
| [DriveSpace CVF](DriveSpace.md) | moving (`DoubleSpaceBlockMover`) | yes | yes |
| [DriveSpace 3 CVF](DriveSpace3.md) | moving (`DriveSpace3FormatDescriptor`) | yes | yes |
| [EFS (SGI Extent File System)](Efs.md) | moving (`EfsBlockMover`) | yes | yes |
| [EROFS](Erofs.md) | moving (`ErofsBlockMover`) | yes | yes |
| [exFAT](ExFat.md) | moving (`ExFatBlockMover`) | yes | yes |
| [ext2/3/4](Ext.md) | moving (`ExtBlockMover`) | yes | yes |
| [ext1](Ext1.md) | moving (`Ext1BlockMover`) | yes | yes |
| [F2FS](F2fs.md) | rebuilding | yes | yes |
| [FAT Filesystem Image](Fat.md) | moving (`FatBlockMover`) | yes | yes |
| [FAT+ Filesystem Image (large-file extension)](FatPlus.md) | rebuilding | yes | yes |
| [FATX (Xbox)](Fatx.md) | moving (`FatxBlockMover`) | yes | yes |
| [G64 (Commodore GCR)](G64.md) | rebuilding | no | yes |
| [GEMDOS (Atari ST)](Gemdos.md) | rebuilding | yes | yes |
| [GFS (Sistina/Red Hat, original)](Gfs1.md) | moving (`Gfs1BlockMover`) | yes | yes |
| [GFS2 (Global File System 2)](Gfs2.md) | moving (`Gfs2BlockMover`) | yes | yes |
| [HAMMER (DragonFly BSD)](Hammer.md) | moving (`HammerBlockMover`) | yes | yes |
| [HAMMER2 (DragonFly BSD)](Hammer2.md) | rebuilding | no | yes |
| [HFS (Classic)](Hfs.md) | moving (`HfsBlockMover`) | yes | yes |
| [HFS+](HfsPlus.md) | moving (`HfsPlusBlockMover`) | yes | yes |
| [HPFS](Hpfs.md) | moving (`HpfsBlockMover`) | yes | yes |
| [HTFS (SCO High Throughput File System)](Htfs.md) | moving (`HtfsBlockMover`) | yes | yes |
| [Sharp X68000 Human68k](Human68k.md) | moving (`Human68kBlockMover`) | yes | yes |
| [ISO 9660](Iso.md) | moving (`IsoBlockMover`) | yes | yes |
| [JFFS2](Jffs2.md) | moving (`Jffs2BlockMover`) | yes | yes |
| [JFS](Jfs.md) | moving (`JfsBlockMover`) | yes | yes |
| [JFS1 (OS/2 original IBM JFS)](Jfs1.md) | moving (`Jfs1BlockMover`) | yes | yes |
| [HP LIF (Logical Interchange Format)](Lif.md) | moving (`LifBlockMover`) | yes | yes |
| [LittleFS](LittleFs.md) | rebuilding | yes | yes |
| [MFS (Macintosh File System)](Mfs.md) | moving (`MfsBlockMover`) | yes | yes |
| [MFS-1 (Acorn Master File System v1)](Mfs1.md) | moving (`Mfs1BlockMover`) | yes | yes |
| [Minix FS](MinixFs.md) | moving (`MinixFsBlockMover`) | yes | yes |
| [Minix V1 FS](MinixV1.md) | moving (`MinixV1BlockMover`) | yes | yes |
| [Minix V2 FS](MinixV2.md) | moving (`MinixV2BlockMover`) | yes | yes |
| [MSA (Magic Shadow Archiver)](Msa.md) | rebuilding | yes | yes |
| [NIB (Commodore nibble dump)](Nib.md) | — | no | no |
| [NILFS v1](Nilfs1.md) | rebuilding | yes | yes |
| [NILFS2](Nilfs2.md) | rebuilding | yes | yes |
| [NSS (Novell Storage Services)](Nss.md) | rebuilding | no | no |
| [NTFS](Ntfs.md) | moving (`NtfsBlockMover`) | yes | yes |
| [NWFS (Novell NetWare 386 Traditional Filesystem)](Nwfs.md) | — | no | no |
| [NWFS386 (Novell NetWare 386 raw)](Nwfs386.md) | — | no | no |
| [OCFS2 (Oracle Cluster Filesystem 2)](Ocfs2.md) | moving (`Ocfs2BlockMover`) | yes | yes |
| [ODS-1 (VAX/VMS Files-11 L1)](Ods1.md) | moving (`Ods1BlockMover`) | yes | yes |
| [OpenVMS Files-11](OpenVms.md) | moving (`OpenVmsBlockMover`) | yes | yes |
| [Microware OS-9 RBF](Os9Rbf.md) | moving (`Os9RbfBlockMover`) | yes | yes |
| [NEC PC-98 DOS](Pc98.md) | moving (`Pc98BlockMover`) | yes | yes |
| [ProDOS](ProDos.md) | moving (`ProDosBlockMover`) | yes | yes |
| [QNX4 FS](Qnx4.md) | moving (`Qnx4BlockMover`) | yes | yes |
| [QNX6 Neutrino FS](Qnx6.md) | moving (`Qnx6BlockMover`) | yes | yes |
| [ReFS](Refs.md) | — | no | no |
| [Reiser4](Reiser4.md) | rebuilding | yes | yes |
| [ReiserFS](ReiserFs.md) | moving (`ReiserFsBlockMover`) | yes | yes |
| [ROMFS](RomFs.md) | moving (`RomFsBlockMover`) | yes | yes |
| [DEC RT-11 (RX01)](Rt11.md) | moving (`Rt11BlockMover`) | yes | yes |
| [Amiga SFS](Sfs.md) | rebuilding | no | no |
| [SmartFS](SmartFs.md) | moving (`SmartFsBlockMover`) | no | no |
| [SquashFS](SquashFs.md) | rebuilding | yes | yes |
| [UNIX System V FS](SysV.md) | moving (`SysVBlockMover`) | yes | yes |
| [Transactional FAT (TFAT)](TFat.md) | rebuilding | yes | yes |
| [TFS (BBN Trans-FS)](Tfs.md) | — | no | no |
| [TI-99/4A DSR](Ti99.md) | moving (`Ti99BlockMover`) | yes | yes |
| [TR-DOS](TrDos.md) | moving (`TrDosBlockMover`) | yes | yes |
| [TRSDOS / LDOS](Trsdos.md) | moving (`TrsdosBlockMover`) | yes | yes |
| [TUX2](Tux2.md) | rebuilding | yes | yes |
| [TUX3](Tux3.md) | rebuilding | yes | yes |
| [UBIFS](Ubifs.md) | rebuilding | no | yes |
| [UDF](Udf.md) | moving (`UdfBlockMover`) | yes | yes |
| [UFS](Ufs.md) | moving (`UfsBlockMover`) | yes | yes |
| [VDFS](Vdfs.md) | moving (`VdfsBlockMover`) | yes | yes |
| [VxFS (Veritas)](VxFs.md) | rebuilding | no | no |
| [Xenix FS](Xenix.md) | moving (`XenixBlockMover`) | yes | yes |
| [XFS](Xfs.md) | moving (`XfsBlockMover`) | yes | yes |
| [YAFFS2](Yaffs2.md) | moving (`Yaffs2BlockMover`) | yes | yes |
| [ZFS](Zfs.md) | rebuilding | no | yes |
| [SCL (ZX Spectrum)](ZxScl.md) | rebuilding | yes | yes |

