#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileSystem.Jfs;

/// <summary>
/// Reads a JFS volume's layout: which blocks are in use, and for the blocks a
/// file's own xtree names, which file they belong to.
/// </summary>
/// <remarks>
/// <para>JFS keeps one dmap page per 8192 blocks; the page's persistent bitmap
/// holds one bit per block, set when the block is allocated. What the bitmap
/// leaves clear is exactly the free space — including the blocks a removed file
/// used to occupy, which still hold its bytes. The map is contiguous from
/// <see cref="FirstDmapBlock" />, and each page states which range it covers in
/// its own header, so the walk validates that against where it expected the
/// page to be rather than assuming the layout.</para>
///
/// <para>The bitmap alone says which blocks are taken and nothing about by
/// whom, which is enough to wipe a volume and not enough to lay one out again:
/// a run with no owner has nothing to repoint. So each file's extents are read
/// from its xtree and reported under its name, and what the bitmap claims
/// beyond them is the volume's own structures.</para>
/// </remarks>
public static class JfsExtentMap {

  private const int BlockSize = JfsWriter.BlockSize;
  private const int SuperblockOffset = 0x8000;
  private const int FirstDmapBlock = 20;
  private const int BlocksPerDmap = 8192;
  private const int PmapOffset = 3072;
  private const int LeavesPerDmap = 256;

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var accessor = new ImageAccessor(image);
      var volumeBlocks = accessor.Length / BlockSize;
      if (volumeBlocks <= FirstDmapBlock) return [];

      // The allocation map only describes the aggregate's usable blocks. What
      // follows them — the fsck workspace and the inline log — is structure the
      // map never mentions, and leaving it unreported reads as free space: a
      // layout pass then packed files straight over the log, which fsck.jfs
      // rejects.
      var usableBlocks = ReadUsableBlocks(accessor, volumeBlocks);

      // Files first: their extents come from their own xtrees, which is the
      // only place the ownership is written down.
      var owned = new List<(long Start, long End)>();
      try {
        image.Position = 0;
        using var reader = new JfsReader(image);
        foreach (var entry in reader.Entries) {
          if (entry.IsDirectory) continue;
          foreach (var (offset, length, _) in reader.EnumerateDataExtents(entry)) {
            if (length <= 0) continue;
            result.Add(new DefragBlockInfo(offset, length, DefragBlockKind.Used, entry.Name));
            owned.Add((offset, offset + length));
          }
        }
      } catch {
        // A volume whose directory we cannot walk still gets its allocation
        // reported below; it simply has no owners to attribute.
      }
      owned.Sort((a, b) => a.Start.CompareTo(b.Start));

      long runStart = -1;
      var pages = (usableBlocks + BlocksPerDmap - 1) / BlocksPerDmap;
      for (var page = 0L; page < pages; ++page) {
        var pageBlock = FirstDmapBlock + page;
        var pageOffset = pageBlock * BlockSize;
        if (pageOffset + BlockSize > accessor.Length) break;

        var dmap = accessor.Read(pageOffset, BlockSize);
        var nblocks = BinaryPrimitives.ReadInt32LittleEndian(dmap.AsSpan(0, 4));
        var start = BinaryPrimitives.ReadInt64LittleEndian(dmap.AsSpan(8, 8));
        // A page that does not describe the range it should is not a dmap, and
        // guessing past it would put the wipe onto live data.
        if (start != page * BlocksPerDmap || nblocks <= 0 || nblocks > BlocksPerDmap) break;

        for (var leaf = 0; leaf < LeavesPerDmap; ++leaf) {
          var word = BinaryPrimitives.ReadUInt32LittleEndian(dmap.AsSpan(PmapOffset + leaf * 4, 4));
          for (var bit = 0; bit < 32; ++bit) {
            var block = start + leaf * 32 + bit;
            if (block >= usableBlocks) break;
            var allocated = (word & (0x80000000u >> bit)) != 0;
            if (allocated) {
              if (runStart < 0) runStart = block;
              continue;
            }
            if (runStart >= 0) {
              AddUnowned(result, owned, runStart * BlockSize, block * BlockSize);
              runStart = -1;
            }
          }
        }
      }
      if (runStart >= 0)
        AddUnowned(result, owned, runStart * BlockSize, usableBlocks * BlockSize);

      if (usableBlocks < volumeBlocks)
        result.Add(new DefragBlockInfo(usableBlocks * BlockSize,
          (volumeBlocks - usableBlocks) * BlockSize, DefragBlockKind.MetadataReserved,
          "fsck workspace and log"));
    } catch {
      // A volume whose map we cannot read claims nothing, and a wipe of it would
      // zero live data — so report no extents at all.
      return [];
    }
    return result;
  }

  /// <summary>
  /// How many blocks the aggregate actually uses, from the superblock's size in
  /// hardware blocks. Everything past them belongs to the fsck workspace and
  /// the log.
  /// </summary>
  private static long ReadUsableBlocks(ImageAccessor image, long volumeBlocks) {
    if (SuperblockOffset + 16 > image.Length) return volumeBlocks;
    var sizeInHardwareBlocks = (long)BinaryPrimitives.ReadUInt64LittleEndian(
      image.Read(SuperblockOffset + 8, 8));
    var usable = sizeInHardwareBlocks / (BlockSize / JfsWriter.SectorSize);
    return usable > 0 && usable <= volumeBlocks ? usable : volumeBlocks;
  }

  /// <summary>
  /// Reports the parts of an allocated run that no file claims as the volume's
  /// own structures. Reporting the whole run would describe a file's blocks
  /// twice — once under its name and once as immovable — and a layout pass
  /// would then refuse to move anything.
  /// </summary>
  private static void AddUnowned(List<DefragBlockInfo> result,
      List<(long Start, long End)> owned, long start, long end) {
    var cursor = start;
    foreach (var (ownedStart, ownedEnd) in owned) {
      if (ownedEnd <= cursor) continue;
      if (ownedStart >= end) break;
      if (ownedStart > cursor)
        result.Add(new DefragBlockInfo(cursor, ownedStart - cursor, DefragBlockKind.MetadataReserved));
      cursor = Math.Max(cursor, ownedEnd);
      if (cursor >= end) return;
    }
    if (cursor < end)
      result.Add(new DefragBlockInfo(cursor, end - cursor, DefragBlockKind.MetadataReserved));
  }
}
