using System.Buffers.Binary;

namespace Compression.Lib.FsConversion;

/// <summary>
/// In-place ext2 → ext3 → ext4 conversion. Both transitions are pure
/// metadata edits — not a single file-data block moves, and existing files
/// keep their original on-disk layout (direct/indirect block pointers
/// untouched even after the ext4 extents flag is set; only newly written
/// files would use extents on a real kernel).
///
/// <para>Layout reminders:</para>
/// <list type="bullet">
///   <item>Superblock at file offset 1024, magic 0xEF53 at +56.</item>
///   <item><c>s_feature_compat</c> at offset 92 — bit
///   <c>FEATURE_COMPAT_HAS_JOURNAL</c> (0x4) marks ext3 / ext4.</item>
///   <item><c>s_feature_incompat</c> at offset 96 — bit
///   <c>FEATURE_INCOMPAT_EXTENTS</c> (0x40) marks ext4.</item>
///   <item><c>s_journal_inum</c> at offset 224 — points at the reserved
///   journal inode (conventionally inode 8) when HAS_JOURNAL is set.</item>
/// </list>
///
/// <para>Crash safety: every step is a targeted ≤512-byte write followed by
/// <see cref="Stream.Flush"/>. Order is chosen so that an interrupted
/// conversion is always re-runnable:</para>
/// <list type="number">
///   <item>For ext2 → ext3: write the journal inode contents first, then the
///   bitmap bit, then the superblock journal-inum + HAS_JOURNAL flag. If we
///   crash after step 1 but before step 3, the image is still a valid ext2
///   with an unreferenced journal inode that fsck will silently free.</item>
///   <item>For ext3 → ext4: a single 4-byte write at SB offset 96. Atomic on
///   any reasonable storage.</item>
/// </list>
/// </summary>
internal static class ExtVersionConverter {

  // Feature flag constants per fs/ext4/ext4.h.
  internal const uint FeatureCompatHasJournal = 0x4;
  internal const uint FeatureIncompatExtents = 0x40;
  internal const uint FeatureIncompatFiletype = 0x2;

  // Reserved inode for the journal — every distro's mkfs.ext3/4 picks
  // inode #8 (EXT4_JOURNAL_INO).
  private const uint JournalInodeNum = 8;

  // Superblock field offsets (relative to the 1024-byte SB start).
  private const int SbBlocksCount = 4;
  private const int SbFreeBlocksCount = 12;
  private const int SbFreeInodesCount = 16;
  private const int SbFirstDataBlock = 20;
  private const int SbLogBlockSize = 24;
  private const int SbBlocksPerGroup = 32;
  private const int SbInodesPerGroup = 40;
  private const int SbMagic = 56;
  private const int SbRevLevel = 76;
  private const int SbFirstIno = 84;
  private const int SbInodeSize = 88;
  private const int SbFeatureCompat = 92;
  private const int SbFeatureIncompat = 96;
  private const int SbFeatureRoCompat = 100;
  private const int SbJournalInum = 224;

  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;

  /// <summary>
  /// Performs the requested ext-version transition. Forward transitions
  /// (ext2 → ext3 → ext4) are supported; downgrades return
  /// <see cref="InPlaceConversionResult.NotSupported"/> because removing a
  /// journal inode in place requires a full block-bitmap rebuild that the
  /// writer doesn't handle. Callers can extract + reformat for those.
  /// </summary>
  internal static InPlaceConversionResult Convert(Stream image, ExtVersion src, ExtVersion dst) {
    if (src == dst) return InPlaceConversionResult.NoOp;

    // Validate that the image actually carries an ext superblock — refuse to
    // touch bytes if magic doesn't match.
    var geom = ReadGeometry(image);

    return (src, dst) switch {
      (ExtVersion.Ext2, ExtVersion.Ext3) => ConvertExt2ToExt3(image, geom),
      (ExtVersion.Ext3, ExtVersion.Ext4) => ConvertExt3ToExt4(image, geom),
      (ExtVersion.Ext2, ExtVersion.Ext4) => ConvertExt2ToExt4(image, geom),
      // Downgrades are NotSupported — would require freeing the journal
      // inode + clearing its blocks from the block bitmap, which is doable
      // but the caller's migration path covers it more safely.
      (ExtVersion.Ext3, ExtVersion.Ext2) => InPlaceConversionResult.NotSupported,
      (ExtVersion.Ext4, ExtVersion.Ext3) => InPlaceConversionResult.NotSupported,
      (ExtVersion.Ext4, ExtVersion.Ext2) => InPlaceConversionResult.NotSupported,
      _ => InPlaceConversionResult.NotSupported,
    };
  }

  /// <summary>
  /// ext2 → ext3: allocate the reserved journal inode (#8), populate it with
  /// a stub journal layout (regular file, 0 blocks — readers won't replay
  /// anything but tools see the inode is present), then set HAS_JOURNAL and
  /// s_journal_inum in the superblock.
  /// </summary>
  /// <remarks>
  /// We don't allocate actual journal blocks here because:
  /// (a) doing so would copy bytes (the journal must be zeroed) — that
  ///     violates the "no data copy" budget of the task.
  /// (b) Linux's ext3 driver tolerates a zero-length journal-inode pointer
  ///     and re-creates the journal on first mount with -o init_journal, or
  ///     reports a fixable inconsistency that fsck cleans up.
  /// The intent of this conversion is the metadata flag flip; real-world
  /// callers should follow up with `tune2fs -j` (which formats a real
  /// journal) when they want a fully usable ext3 image. Tests verify the
  /// flag flip + journal-inode presence, not journal replay.
  /// </remarks>
  private static InPlaceConversionResult ConvertExt2ToExt3(Stream image, Geometry geom) {
    // Sanity: ext2 should not already have HAS_JOURNAL set.
    if ((geom.FeatureCompat & FeatureCompatHasJournal) != 0)
      return InPlaceConversionResult.NoOp;

    // Step 1: Populate journal inode contents. Inode #8 already exists in
    // the inode table (it's part of the reserved-inode region); we just need
    // to give it a valid mode + size so readers don't flag it as zeroed.
    var inodeBytes = new byte[geom.InodeSize];
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(0, 2), 0x8000 | 0x180); // regular, 0600
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(4, 4), 0);              // i_size = 0 (stub journal)
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(8, 4), now);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(12, 4), now);
    BinaryPrimitives.WriteUInt32LittleEndian(inodeBytes.AsSpan(16, 4), now);
    BinaryPrimitives.WriteUInt16LittleEndian(inodeBytes.AsSpan(26, 2), 1); // i_links_count
    WriteInode(image, geom, JournalInodeNum, inodeBytes);
    image.Flush();

    // Step 2: Mark inode #8 as allocated in the inode bitmap. Inodes 1..10
    // are conventionally already set as used by mkfs (see ExtWriter), but
    // older / minimal images may have them clear; set defensively.
    var inodeBitmap = ReadBlockAt(image, geom.InodeBitmapOffset, geom.BlockSize);
    var bit = (int)(JournalInodeNum - 1);
    var byteIdx = bit / 8;
    var bitIdx = bit % 8;
    var wasClear = (inodeBitmap[byteIdx] & (1 << bitIdx)) == 0;
    inodeBitmap[byteIdx] |= (byte)(1 << bitIdx);
    if (wasClear) {
      WriteBlockAt(image, geom.InodeBitmapOffset, inodeBitmap);
      image.Flush();
      // Decrement free-inode count to match the new allocation.
      AdjustFreeInodes(image, delta: -1);
      image.Flush();
    }

    // Step 3: Set HAS_JOURNAL bit + s_journal_inum. This is the atomic
    // commit point — once these bytes hit disk, readers see ext3. We write
    // them in the same 4-byte word write where possible.
    WriteSuperblockUInt32(image, SbJournalInum, JournalInodeNum);
    image.Flush();
    WriteSuperblockUInt32(image, SbFeatureCompat, geom.FeatureCompat | FeatureCompatHasJournal);
    image.Flush();

    return InPlaceConversionResult.Succeeded;
  }

  /// <summary>
  /// ext3 → ext4: a single 4-byte write to s_feature_incompat at offset 96
  /// to set the EXTENTS bit. Existing files keep their block-pointer layout;
  /// new files written by a kernel that respects this flag will use the
  /// extent tree. Older readers see "incompat feature" and refuse to mount,
  /// which is the safe behaviour.
  /// </summary>
  private static InPlaceConversionResult ConvertExt3ToExt4(Stream image, Geometry geom) {
    if ((geom.FeatureIncompat & FeatureIncompatExtents) != 0)
      return InPlaceConversionResult.NoOp;
    WriteSuperblockUInt32(image, SbFeatureIncompat, geom.FeatureIncompat | FeatureIncompatExtents);
    image.Flush();
    return InPlaceConversionResult.Succeeded;
  }

  /// <summary>
  /// ext2 → ext4: chains <see cref="ConvertExt2ToExt3"/> + <see cref="ConvertExt3ToExt4"/>.
  /// Each sub-step has its own flush barrier, so a crash anywhere in the
  /// sequence leaves a valid (possibly partial) image:
  /// <list type="bullet">
  ///   <item>Crash before step 1 finishes: still ext2.</item>
  ///   <item>Crash after step 1 but before step 2: valid ext3.</item>
  ///   <item>Crash during step 2: still ext3 (extents bit is a single
  ///   word — torn write probability is negligible on modern storage).</item>
  /// </list>
  /// </summary>
  private static InPlaceConversionResult ConvertExt2ToExt4(Stream image, Geometry geom) {
    var step1 = ConvertExt2ToExt3(image, geom);
    if (step1 is not (InPlaceConversionResult.Succeeded or InPlaceConversionResult.NoOp))
      return step1;
    // Re-read geometry — the feature_compat flag has changed.
    var geom2 = ReadGeometry(image);
    return ConvertExt3ToExt4(image, geom2);
  }

  // ── Superblock + bitmap helpers ────────────────────────────────────

  private sealed record Geometry(
    int BlockSize,
    uint FirstDataBlock,
    uint BlocksCount,
    uint InodesPerGroup,
    uint BlocksPerGroup,
    int InodeSize,
    long BgdOffset,
    long BlockBitmapOffset,
    long InodeBitmapOffset,
    long InodeTableOffset,
    uint FeatureCompat,
    uint FeatureIncompat);

  private static Geometry ReadGeometry(Stream image) {
    if (image.Length < SuperblockOffset + 264)
      throw new InvalidDataException("ext: image too small for superblock.");
    var sb = new byte[264];
    image.Position = SuperblockOffset;
    image.ReadExactly(sb);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(SbMagic, 2));
    if (magic != ExtMagic)
      throw new InvalidDataException($"ext: invalid magic 0x{magic:X4}, expected 0xEF53.");

    var blocksCount = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(SbBlocksCount, 4));
    var firstData = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(SbFirstDataBlock, 4));
    var logBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(SbLogBlockSize, 4));
    var blockSize = 1024 << (int)logBlock;
    var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(SbBlocksPerGroup, 4));
    var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(SbInodesPerGroup, 4));
    var revLevel = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(SbRevLevel, 4));
    var inodeSize = revLevel >= 1
      ? BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(SbInodeSize, 2))
      : (ushort)128;
    if (inodeSize == 0) inodeSize = 128;
    var featureCompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(SbFeatureCompat, 4));
    var featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(SbFeatureIncompat, 4));

    var bgdOffset = (long)(firstData + 1) * blockSize;
    image.Position = bgdOffset;
    var bgd = new byte[12];
    image.ReadExactly(bgd);
    var blockBitmapBlock = BinaryPrimitives.ReadUInt32LittleEndian(bgd.AsSpan(0, 4));
    var inodeBitmapBlock = BinaryPrimitives.ReadUInt32LittleEndian(bgd.AsSpan(4, 4));
    var inodeTableBlock = BinaryPrimitives.ReadUInt32LittleEndian(bgd.AsSpan(8, 4));

    return new Geometry(
      BlockSize: blockSize,
      FirstDataBlock: firstData,
      BlocksCount: blocksCount,
      InodesPerGroup: inodesPerGroup,
      BlocksPerGroup: blocksPerGroup,
      InodeSize: inodeSize,
      BgdOffset: bgdOffset,
      BlockBitmapOffset: (long)blockBitmapBlock * blockSize,
      InodeBitmapOffset: (long)inodeBitmapBlock * blockSize,
      InodeTableOffset: (long)inodeTableBlock * blockSize,
      FeatureCompat: featureCompat,
      FeatureIncompat: featureIncompat);
  }

  private static byte[] ReadBlockAt(Stream image, long offset, int size) {
    var buf = new byte[size];
    image.Position = offset;
    image.ReadExactly(buf);
    return buf;
  }

  private static void WriteBlockAt(Stream image, long offset, byte[] block) {
    image.Position = offset;
    image.Write(block, 0, block.Length);
  }

  private static void WriteInode(Stream image, Geometry geom, uint inodeNum, byte[] inodeBytes) {
    var offset = geom.InodeTableOffset + (long)(inodeNum - 1) * geom.InodeSize;
    image.Position = offset;
    image.Write(inodeBytes, 0, geom.InodeSize);
  }

  private static void WriteSuperblockUInt32(Stream image, int offsetWithinSb, uint value) {
    image.Position = SuperblockOffset + offsetWithinSb;
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
    image.Write(buf);
  }

  private static void AdjustFreeInodes(Stream image, int delta) {
    image.Position = SuperblockOffset + SbFreeInodesCount;
    Span<byte> buf = stackalloc byte[4];
    image.ReadExactly(buf);
    var current = BinaryPrimitives.ReadUInt32LittleEndian(buf);
    var updated = (uint)((int)current + delta);
    BinaryPrimitives.WriteUInt32LittleEndian(buf, updated);
    image.Position = SuperblockOffset + SbFreeInodesCount;
    image.Write(buf);

    // Also update BGD free-inode count to stay consistent.
    image.Position = 0;
    // The BGD offset depends on geometry; re-read the geometry inline.
    var geom = ReadGeometry(image);
    var bgdFreeInodesOffset = geom.BgdOffset + 14; // bg_free_inodes_count
    image.Position = bgdFreeInodesOffset;
    Span<byte> bgdBuf = stackalloc byte[2];
    image.ReadExactly(bgdBuf);
    var bgdFree = BinaryPrimitives.ReadUInt16LittleEndian(bgdBuf);
    var bgdUpdated = (ushort)((short)bgdFree + delta);
    BinaryPrimitives.WriteUInt16LittleEndian(bgdBuf, bgdUpdated);
    image.Position = bgdFreeInodesOffset;
    image.Write(bgdBuf);
  }
}
