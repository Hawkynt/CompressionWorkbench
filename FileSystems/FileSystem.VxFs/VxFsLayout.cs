#pragma warning disable CS1591
namespace FileSystem.VxFs;

/// <summary>
/// The constants and byte offsets of the VxFS structures this writes and reads.
/// </summary>
/// <remarks>
/// <para>These track <c>fs/freevxfs/</c> in the Linux kernel — Christoph
/// Hellwig's read-only driver, as revised by Krzysztof Blaszkowski in 2016 —
/// because that driver is what decides whether a volume we emit is a VxFS
/// volume or merely one that resembles it. Every offset below is a field the
/// driver reads.</para>
///
/// <para>Two of them were wrong here before. The superblock carries two unused
/// words between <c>vs_cutime</c> and <c>vs_old_logstart</c>, which the older
/// notes in this project omitted, so every field from there on was read eight
/// bytes early — the block size came out of <c>vs_old_logstart</c>. Nothing
/// noticed, because nothing read past the superblock.</para>
/// </remarks>
internal static class VxFsLayout {

  /// <summary>Where the superblock sits, and the unit the driver first reads in.</summary>
  internal const int SuperblockOffset = 1024;

  /// <summary>
  /// The block size volumes are written with.
  /// </summary>
  /// <remarks>
  /// The driver locates the object location table at
  /// <c>block * (s_blocksize / bsize)</c>, where <c>bsize</c> is the 1024 bytes
  /// it mounted with before it read our block size. Those agree only when the
  /// volume's own block size is 1024 as well, so that is the one this writes.
  /// </remarks>
  internal const int BlockSize = 1024;

  /// <summary>An inode on disk, and the stride of every inode list.</summary>
  internal const int InodeSize = 0x100;

  internal const uint SuperMagic = 0xA501FCF5;
  internal const uint OltMagic = 0xA504FCF5;

  /// <summary>The inode the driver mounts as the root directory.</summary>
  internal const uint RootInode = 2;

  internal const int DirectExtents = 10;    // VXFS_NDADDR
  internal const int ImmediateBytes = 96;   // VXFS_NIMMED

  // ── superblock field offsets ───────────────────────────────────────────
  internal const int SbMagic = 0;
  internal const int SbVersion = 4;
  internal const int SbCtime = 8;
  internal const int SbCutime = 12;
  internal const int SbBsize = 32;
  internal const int SbSize = 36;
  internal const int SbDsize = 40;
  internal const int SbOldNinode = 44;
  internal const int SbImmedlen = 64;
  internal const int SbNdaddr = 68;
  internal const int SbFirstau = 72;
  internal const int SbIstart = 88;
  internal const int SbBstart = 92;
  internal const int SbNindir = 116;
  internal const int SbInopb = 148;
  internal const int SbBshift = 168;
  internal const int SbInoshift = 172;
  internal const int SbBmask = 176;
  internal const int SbBoffmask = 180;
  internal const int SbFree = 192;
  internal const int SbIfree = 196;
  internal const int SbFlags = 328;
  internal const int SbClean = 333;
  internal const int SbWtime = 340;
  internal const int SbFname = 348;
  internal const int SbFpack = 354;
  internal const int SbLogversion = 360;
  internal const int SbOltext = 368;
  internal const int SbOltsize = 376;
  internal const int SbDinosize = 388;
  internal const int SuperblockBytes = 400;

  // ── object location table ──────────────────────────────────────────────
  /// <summary>The OLT header, past which its entries begin.</summary>
  internal const int OltHeaderBytes = 56;
  internal const uint OltFree = 1;
  internal const uint OltFsHead = 2;
  internal const uint OltIlist = 4;

  // ── inode field offsets ────────────────────────────────────────────────
  internal const int InMode = 0x00;
  internal const int InNlink = 0x04;
  internal const int InUid = 0x08;
  internal const int InGid = 0x0C;
  internal const int InSize = 0x10;
  internal const int InAtime = 0x18;
  internal const int InMtime = 0x20;
  internal const int InCtime = 0x28;
  internal const int InAflags = 0x30;
  internal const int InOrgtype = 0x31;
  /// <summary>The union the driver reads a directory's parent out of.</summary>
  internal const int InFtarea = 0x38;
  internal const int InBlocks = 0x40;
  internal const int InGen = 0x44;
  internal const int InVersion = 0x48;
  internal const int InOrg = 0x50;
  internal const int InIattrino = 0xB0;

  /// <summary>Where the direct extents start inside an <c>ORG_EXT4</c> inode.</summary>
  internal const int Ext4Direct = InOrg + 16;

  // ── organisation types ─────────────────────────────────────────────────
  internal const byte OrgNone = 0;
  internal const byte OrgExt4 = 1;
  internal const byte OrgImmed = 2;
  internal const byte OrgTyped = 3;

  // ── mode bits the driver switches on ───────────────────────────────────
  internal const uint TypeMask = 0xFFFFF000;
  internal const uint ModeDir = 0x00004000;
  internal const uint ModeReg = 0x00008000;
  /// <summary>The fileset-header list, which the driver insists on by type.</summary>
  internal const uint ModeFsh = 0x10000000;
  /// <summary>An inode list, likewise checked by type.</summary>
  internal const uint ModeIlt = 0x20000000;

  // ── directory blocks ───────────────────────────────────────────────────
  /// <summary>Bytes of <c>vxfs_direct</c> before the name.</summary>
  internal const int DirNameOffset = 10;

  /// <summary>What a directory entry occupies, names being padded to four bytes.</summary>
  internal static int DirEntryLength(int nameLength) => (DirNameOffset + nameLength + 3) & ~3;

  /// <summary>
  /// The header every directory block opens with: a free count, a hash chain
  /// count, and that many chains. We write no chains, so it is four bytes.
  /// </summary>
  internal const int DirBlockHeaderBytes = 4;

  /// <summary>Rounds a length the way the driver rounds a directory's size.</summary>
  internal static long DirRound(long length) => (length + 3) & ~3L;
}
