#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using Compression.Registry;

namespace FileSystem.Hammer;

/// <summary>
/// Reads a HAMMER volume's freemap and reports which bytes are in use. HAMMER
/// allocates in 8 MB big-blocks: a layer-2 entry per big-block records which
/// zone owns it and how far into it the allocator has appended. A big-block no
/// zone owns is free outright, and the tail of one past its append point is
/// free as well — which is where a removed file's bytes stay.
/// </summary>
public static class HammerExtentMap {

  private const long BigblockSize = 8192L * 1024;
  private const long BigblockMask = BigblockSize - 1;
  private const int Layer1EntrySize = 32;
  private const int Layer2EntrySize = 16;
  private const long BlockmapLayer2 = (BigblockSize / Layer2EntrySize) * BigblockSize;
  private const long BlockmapLayer1Mask = (1L << (18 + 19 + 23)) - 1;
  private const long BlockmapLayer2Mask = BlockmapLayer2 - 1;
  private const ulong OffShortMask = 0x000FFFFFFFFFFFFFUL;
  private const ulong ZoneRawBuffer = 2;
  private const long BlockmapUnavail = unchecked((long)0xFFFFFFFFFFFFFFFFUL);
  private const byte ZoneFree = 0;

    /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = HammerReader.Open(image);
      if (!reader.Valid) return [];

      using var accessor = new ImageAccessor(image);
      var volBufBeg = reader.VolumeBufferStart;
      var layer1Base = reader.FreemapLayer1Offset;
      if (volBufBeg <= 0 || layer1Base <= 0 || volBufBeg >= accessor.Length) return [];

      // Everything before the buffer area — volume header, boot and memory-log
      // reserves — is structure the freemap does not describe.
      result.Add(new DefragBlockInfo(0, volBufBeg, DefragBlockKind.MetadataReserved));

      // Files first: their records come from the B-tree that names them, which
      // is the only place the ownership is written down. The freemap below
      // accounts per big-block and says nothing about by whom.
      var owned = new List<(long Start, long End)>();
      try {
        foreach (var extent in reader.EnumerateDataExtents()) {
          if (extent.Length <= 0) continue;
          result.Add(new DefragBlockInfo(extent.Offset, extent.Length, DefragBlockKind.Used, extent.Path));
          owned.Add((extent.Offset, extent.Offset + extent.Length));
        }
      } catch {
        // A volume whose tree we cannot walk still gets its allocation reported
        // below; it simply has no owners to attribute.
      }
      owned.Sort((a, b) => a.Start.CompareTo(b.Start));

      for (var offset = 0L; volBufBeg + offset < accessor.Length; offset += BigblockSize) {
        var zone2 = (long)(ZoneRawBuffer << 60) | offset;
        var layer1Offset = layer1Base + Layer1Index(zone2) * Layer1EntrySize;
        if (layer1Offset + Layer1EntrySize > accessor.Length) break;

        var layer1 = accessor.Read(layer1Offset, Layer1EntrySize);
        var layer2Zone2 = (long)BinaryPrimitives.ReadUInt64LittleEndian(layer1.AsSpan(8, 8));
        if (layer2Zone2 == BlockmapUnavail) break;

        var layer2Base = volBufBeg + (long)((ulong)layer2Zone2 & OffShortMask);
        var layer2Offset = layer2Base + Layer2Index(zone2) * Layer2EntrySize;
        if (layer2Offset + Layer2EntrySize > accessor.Length) break;

        var layer2 = accessor.Read(layer2Offset, Layer2EntrySize);
        var zone = layer2[0];
        var appendOff = BinaryPrimitives.ReadUInt32LittleEndian(layer2.AsSpan(4, 4));
        var bytesFree = BinaryPrimitives.ReadInt32LittleEndian(layer2.AsSpan(8, 4));

        var start = volBufBeg + offset;
        var span = Math.Min(BigblockSize, accessor.Length - start);
        if (span <= 0) break;
        if (zone == ZoneFree) continue; // the whole big-block is free

        // How much of the big-block is live. The append point wraps to zero once
        // a big-block is full, so bytes_free is what distinguishes "nothing
        // allocated yet" from "allocated to the last byte".
        var used = Math.Max((long)appendOff, BigblockSize - Math.Max(0, bytesFree));
        used = Math.Min(used, span);
        if (used <= 0) continue;
        AddUnowned(result, owned, start, start + used);
      }
    } catch {
      // A volume whose freemap we cannot walk claims nothing, and a wipe of it
      // would zero live data — so report no extents at all.
      return [];
    }
    return result;
  }

  private static long Layer1Index(long zone2) => (zone2 & BlockmapLayer1Mask) / BlockmapLayer2;

  private static long Layer2Index(long zone2) => (zone2 & BlockmapLayer2Mask) / BigblockSize;

  /// <summary>
  /// Reports the parts of an allocated run that no file claims as the volume's
  /// own structures. Reporting the whole run would describe a file's bytes
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
