#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileSystem.Gfs2;

/// <summary>
/// Reads a GFS2 volume's resource-group bitmaps and reports which blocks are in
/// use. GFS2 accounts for allocation two bits per block: 00 is free, and every
/// other state (data, unlinked, dinode) means the block is live. What the
/// bitmaps leave clear is exactly the free space — including the blocks a
/// removed file used to occupy, which still hold its bytes.
/// </summary>
/// <remarks>
/// <para>The resource groups are found by walking the chain rather than the
/// rindex: each rgrp header carries <c>rg_skip</c>, the distance to the next
/// one, and the last carries zero. A group's bitmap covers its data blocks
/// only — the header block and the RB blocks that follow it are structure and
/// are always in use — so the number of those blocks is recovered from the same
/// relation the writer used to size them.</para>
/// </remarks>
public static class Gfs2ExtentMap {

  private const uint MetaMagic = 0x01161970u;
  private const uint MetaTypeRg = 2;
  private const int RgrpHeaderBytes = 128;
  private const int RbHeaderBytes = 24;

  /// <summary>How far the search for the first resource group runs, in blocks.</summary>
  private const long FirstRgrpSearchBlocks = 1 << 16;

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var accessor = new ImageAccessor(image);
      var reader = new Gfs2Reader(image);
      var blockSize = (long)reader.BlockSize;
      if (blockSize < 512 || accessor.Length < blockSize * 2) return [];
      var volumeBlocks = accessor.Length / blockSize;

      var first = FindFirstRgrp(accessor, blockSize, volumeBlocks);
      if (first < 0) return [];

      // Files first: their runs come from the metadata trees that name them,
      // which is the only place the ownership is written down. The bitmaps
      // below say which blocks are taken and nothing about by whom.
      var owned = new List<(long Start, long End)>();
      try {
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

      // Everything ahead of the first resource group — superblock, master
      // directory, rindex, journals — is structure.
      result.Add(new DefragBlockInfo(0, first * blockSize, DefragBlockKind.MetadataReserved));

      var header = first;
      var guard = 0;
      while (header >= 0 && header < volumeBlocks && guard++ < 1_000_000) {
        var head = accessor.Read(header * blockSize, RgrpHeaderBytes);
        if (BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0, 4)) != MetaMagic) break;
        if (BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4, 4)) != MetaTypeRg) break;

        var skip = (long)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(36, 4));
        var span = skip > 0 ? skip : volumeBlocks - header;
        if (span <= 1) break;

        var riLength = ResolveHeaderBlocks(span, blockSize);
        if (riLength <= 0 || riLength >= span) break;
        var data0 = header + riLength;
        var data = span - riLength;

        // The header and its RB blocks hold the bitmap itself.
        result.Add(new DefragBlockInfo(header * blockSize, riLength * blockSize,
          DefragBlockKind.MetadataReserved));

        long runStart = -1;
        for (var i = 0L; i < data; ++i) {
          var used = ReadBitmapState(accessor, header, i, blockSize) != 0;
          if (used) {
            if (runStart < 0) runStart = i;
            continue;
          }
          if (runStart >= 0) {
            AddUnowned(result, owned, (data0 + runStart) * blockSize, (data0 + i) * blockSize);
            runStart = -1;
          }
        }
        if (runStart >= 0)
          AddUnowned(result, owned, (data0 + runStart) * blockSize, (data0 + data) * blockSize);

        if (skip == 0) break;
        header += skip;
      }

      // What no resource group covers is not free space — it is outside what
      // the filesystem accounts for — and leaving it unreported invites a
      // layout pass to put files there.
      var described = header * blockSize;
      if (described > 0 && described < accessor.Length)
        result.Add(new DefragBlockInfo(described, accessor.Length - described,
          DefragBlockKind.MetadataReserved, "past the last resource group"));
    } catch {
      // A volume whose groups we cannot walk claims nothing, and a wipe of it
      // would zero live data — so report no extents at all.
      return [];
    }
    return result;
  }

  /// <summary>
  /// Locates the first resource group. It sits past the superblock, the master
  /// directory and the journals, all of which are near the start of the volume.
  /// </summary>
  private static long FindFirstRgrp(ImageAccessor image, long blockSize, long volumeBlocks) {
    var limit = Math.Min(volumeBlocks, FirstRgrpSearchBlocks);
    for (var block = 0L; block < limit; ++block) {
      var head = image.Read(block * blockSize, 8);
      if (BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0, 4)) != MetaMagic) continue;
      if (BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4, 4)) == MetaTypeRg) return block;
    }
    return -1;
  }

  /// <summary>
  /// How many blocks of a group hold its bitmap. A group's bitmap is four
  /// blocks per byte, the first <c>blockSize - 128</c> bytes living in the
  /// header block and the rest in the RB blocks that follow it; the count that
  /// satisfies that relation for a group of <paramref name="span" /> blocks is
  /// the one the volume was written with.
  /// </summary>
  private static long ResolveHeaderBlocks(long span, long blockSize) {
    var rgrpBitmapBytes = blockSize - RgrpHeaderBytes;
    var rbBitmapBytes = blockSize - RbHeaderBytes;
    for (var length = 1L; length < span && length < 4096; ++length) {
      var data = span - length;
      var bitmapBytes = (data + 3) / 4;
      var need = bitmapBytes <= rgrpBitmapBytes
        ? 1L
        : 1L + (bitmapBytes - rgrpBitmapBytes + rbBitmapBytes - 1) / rbBitmapBytes;
      if (need == length) return length;
    }
    return -1;
  }

  /// <summary>Reads the two-bit allocation state of a group's data block.</summary>
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

  private static int ReadBitmapState(ImageAccessor image, long rgHeader, long dataIndex, long blockSize) {
    var rgrpBitmapBytes = blockSize - RgrpHeaderBytes;
    var rbBitmapBytes = blockSize - RbHeaderBytes;
    var byteOffset = dataIndex / 4;
    var shift = (int)(dataIndex % 4) * 2;

    long absolute;
    if (byteOffset < rgrpBitmapBytes) {
      absolute = rgHeader * blockSize + RgrpHeaderBytes + byteOffset;
    } else {
      var rest = byteOffset - rgrpBitmapBytes;
      var rbIndex = rest / rbBitmapBytes;
      var inRb = rest % rbBitmapBytes;
      absolute = (rgHeader + 1 + rbIndex) * blockSize + RbHeaderBytes + inRb;
    }
    if (absolute < 0 || absolute >= image.Length) return 1; // unreadable → treat as live
    return (image.ReadByte(absolute) >> shift) & 0x3;
  }
}
