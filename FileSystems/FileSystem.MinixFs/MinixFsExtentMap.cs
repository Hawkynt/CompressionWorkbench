#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.MinixFs;

/// <summary>
/// Walks a Minix filesystem image (v1/v2/v3) and yields the <em>actual</em>
/// on-disk byte layout: the boot block + superblock + inode/zone bitmaps +
/// inode table as a single <see cref="DefragBlockKind.MetadataReserved"/> run,
/// each directory's zones as <see cref="DefragBlockKind.MetadataReserved"/>
/// (they hold directory entries, not file payload), and each regular file's
/// data zones as <see cref="DefragBlockKind.Used"/> runs. Unclaimed zones are
/// reported as <see cref="DefragBlockKind.Free"/>.
///
/// <para>This replaces the earlier synthetic layout that fabricated
/// <c>offset += size</c> extents — those never matched the real zone offsets,
/// so any consumer that zero-filled the gaps (e.g. the unused-space wiper)
/// would have corrupted live file data, inode tables and bitmaps.</para>
///
/// <para>For a regular file whose data zones are contiguous, a single Used
/// extent is emitted with the file's name as <see cref="DefragBlockInfo.FileName"/>
/// so the wiper can locate the zone tip via a size lookup. A file split across
/// non-contiguous zone runs emits one Used extent per run with a name that is
/// deliberately absent from the size lookup, so tip trimming never misfires on
/// a run that does not start at file offset zero.</para>
/// </summary>
public static class MinixFsExtentMap {

  private const int SuperblockOffset = 1024;
  private const ushort MagicV1_14 = 0x137F;
  private const ushort MagicV1_30 = 0x138F;
  private const ushort MagicV2_14 = 0x2468;
  private const ushort MagicV2_30 = 0x2478;
  private const ushort MagicV3 = 0x4D5A;
  private const int V3InodeSize = 64;
  private const int V1InodeSize = 32;

  private enum Version { V1_14, V1_30, V2_14, V2_30, V3 }

    /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    return EnumerateCore(data);
  }

  private static List<DefragBlockInfo> EnumerateCore(byte[] data) {
    var result = new List<DefragBlockInfo>();
    if (data.Length < SuperblockOffset + 32) return result;

    var sb = data.AsSpan(SuperblockOffset);
    var magic16 = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(16));
    var magic24 = data.Length >= SuperblockOffset + 30
      ? BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(24))
      : (ushort)0;

    Version version;
    uint ninodes;
    ushort imapBlocks, zmapBlocks, firstdatazone;
    int blockSize;

    if (magic24 == MagicV3) {
      version = Version.V3;
      ninodes = BinaryPrimitives.ReadUInt32LittleEndian(sb);
      imapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6));
      zmapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(8));
      firstdatazone = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(10));
      var bsf = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(28));
      blockSize = bsf == 0 ? 1024 : bsf;
    } else if (magic16 is MagicV1_14 or MagicV1_30 or MagicV2_14 or MagicV2_30) {
      version = magic16 switch {
        MagicV1_14 => Version.V1_14,
        MagicV1_30 => Version.V1_30,
        MagicV2_14 => Version.V2_14,
        _ => Version.V2_30,
      };
      ninodes = BinaryPrimitives.ReadUInt16LittleEndian(sb);
      imapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(4));
      zmapBlocks = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6));
      firstdatazone = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(8));
      blockSize = 1024;
    } else {
      return result;
    }

    var inodeSize = version == Version.V3 ? V3InodeSize : V1InodeSize;
    var inodeTableOffset = 2L * blockSize + (long)imapBlocks * blockSize + (long)zmapBlocks * blockSize;

    // Metadata region: boot + superblock + bitmaps + inode table, up to the
    // first data zone. firstdatazone is a block index.
    var metadataEnd = (long)firstdatazone * blockSize;
    if (metadataEnd <= 0 || metadataEnd > data.Length) metadataEnd = Math.Min(inodeTableOffset + (long)ninodes * inodeSize, data.Length);
    if (metadataEnd > 0)
      result.Add(new DefragBlockInfo(0, Math.Min(metadataEnd, data.Length), DefragBlockKind.MetadataReserved, "boot+superblock+inode table"));

    var totalZones = data.Length / blockSize;
    var owned = new bool[Math.Max(totalZones, 1)];
    for (var z = 0; z < firstdatazone && z < owned.Length; z++) owned[z] = true;

    // Map inode number → file name via the reader (root inode 1 is implicit and
    // not listed; directory inodes carry no payload name we need).
    var nameByInode = NamesByInode(data);

    // Scan EVERY allocated inode directly from the inode table. Relying on the
    // reader's entry list alone would miss the root directory (inode 1) and any
    // indirect-block zones, leaving their zones marked Free — the wiper would
    // then zero live directory data and corrupt the volume.
    for (uint ino = 1; ino <= ninodes; ino++) {
      var inodeOff = inodeTableOffset + (long)(ino - 1) * inodeSize;
      if (inodeOff < 0 || inodeOff + inodeSize > data.Length) break;
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)inodeOff));
      if (mode == 0) continue; // free inode slot

      var isDir = (mode & 0xF000) == 0x4000;
      var isReg = (mode & 0xF000) == 0x8000;
      if (!isDir && !isReg) continue; // ignore symlinks/devices for layout purposes

      var (zones, pointerZones) = CollectZones(data, inodeTableOffset, inodeSize, version, blockSize, ino);
      if (zones.Count == 0 && pointerZones.Count == 0) continue;

      foreach (var z in zones)
        if (z < owned.Length) owned[(int)z] = true;

      // An indirect block is the volume's own bookkeeping, not part of any
      // file's contents, and it is claimed here so that it is neither handed
      // out as free nor zeroed as unused. It is left where it is: moving one
      // means repointing the block that names it, which the mover does not do.
      foreach (var z in pointerZones) {
        if (z >= owned.Length) continue;
        owned[(int)z] = true;
        var pointerOffset = (long)z * blockSize;
        if (pointerOffset < data.Length)
          result.Add(new DefragBlockInfo(pointerOffset, Math.Min(blockSize, data.Length - pointerOffset),
            DefragBlockKind.MetadataReserved, "indirect:" + ino));
      }

      if (isDir) {
        // Directory zones hold dirents — protect them as metadata.
        foreach (var run in GroupRuns(zones)) {
          var off = (long)run.first * blockSize;
          var len = (long)run.count * blockSize;
          if (off >= data.Length) continue;
          len = Math.Min(len, data.Length - off);
          if (len > 0) result.Add(new DefragBlockInfo(off, len, DefragBlockKind.MetadataReserved, "dir:" + ino));
        }
        continue;
      }

      var runs = GroupRuns(zones);
      var contiguous = runs.Count == 1;
      nameByInode.TryGetValue((int)ino, out var fileName);
      foreach (var run in runs) {
        var off = (long)run.first * blockSize;
        var len = (long)run.count * blockSize;
        if (off >= data.Length) continue;
        len = Math.Min(len, data.Length - off);
        if (len <= 0) continue;
        // Only a single-run file maps cleanly onto the wiper's "extent starts at
        // file offset 0" assumption; tag fragmented runs (or unnamed inodes) with
        // a name absent from the size lookup so tip trimming never misfires.
        var name = contiguous && fileName != null ? fileName : "frag:" + ino;
        result.Add(new DefragBlockInfo(off, len, DefragBlockKind.Used, name));
      }
    }

    // Free zones: any unowned zone past the metadata region.
    var freeStart = -1;
    for (var z = 0; z < totalZones; z++) {
      if (!owned[z]) {
        if (freeStart < 0) freeStart = z;
      } else if (freeStart >= 0) {
        result.Add(new DefragBlockInfo((long)freeStart * blockSize, (long)(z - freeStart) * blockSize, DefragBlockKind.Free));
        freeStart = -1;
      }
    }
    if (freeStart >= 0)
      result.Add(new DefragBlockInfo((long)freeStart * blockSize, (long)(totalZones - freeStart) * blockSize, DefragBlockKind.Free));

    return result;
  }

  private static Dictionary<int, string> NamesByInode(byte[] data) {
    var map = new Dictionary<int, string>();
    try {
      using var s = new MemoryStream(data, writable: false);
      var r = new MinixFsReader(s);
      foreach (var e in r.Entries)
        if (!e.IsDirectory)
          map[e.InodeNumber] = e.Name;
    } catch {
      // Malformed image: fall back to inode-only layout (no file names).
    }
    return map;
  }

  // Collects the ordered list of data-zone numbers referenced by an inode,
  // following direct + (single/double/triple) indirect pointers.
  /// <summary>
  /// The zones an inode owns: the ones holding its bytes, and separately the
  /// indirect blocks that address them.
  /// </summary>
  /// <remarks>
  /// <para>The indirect blocks used not to be reported at all, so every one of
  /// them looked like free space — free to be handed to another file, and free
  /// for the wiper to zero, which takes a file's tail with it.</para>
  ///
  /// <para>A pointer of zero is a hole and not the end of the file. Stopping at
  /// the first one left every zone behind it unclaimed, with the same
  /// consequences.</para>
  /// </remarks>
  private static (List<uint> Data, List<uint> Pointers) CollectZones(byte[] data, long inodeTableOffset,
      int inodeSize, Version version, int blockSize, uint inodeNum) {
    var zones = new List<uint>();
    var pointers = new List<uint>();
    if (inodeNum == 0) return (zones, pointers);
    var inodeOff = inodeTableOffset + (long)(inodeNum - 1) * inodeSize;
    if (inodeOff < 0 || inodeOff + inodeSize > data.Length) return (zones, pointers);

    uint size;
    uint[] ptrs;
    if (version == Version.V3) {
      size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)inodeOff + 8));
      ptrs = new uint[10];
      for (var i = 0; i < 10; i++)
        ptrs[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)inodeOff + 24 + i * 4));
    } else {
      size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)inodeOff + 4));
      ptrs = new uint[9];
      for (var i = 0; i < 9; i++)
        ptrs[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)inodeOff + 14 + i * 2));
    }
    if (size == 0) return (zones, pointers);

    var remaining = (long)size;
    const int directCount = 7;
    var indirectIdx = directCount;
    var dindirectIdx = directCount + 1;
    var tindirectIdx = version == Version.V3 ? directCount + 2 : -1;

    for (var i = 0; i < directCount && remaining > 0; i++)
      AddZone(zones, ptrs[i], ref remaining, blockSize);

    if (remaining > 0 && indirectIdx < ptrs.Length)
      WalkIndirect(data, zones, pointers, ptrs[indirectIdx], ref remaining, 1, blockSize);
    if (remaining > 0 && dindirectIdx < ptrs.Length)
      WalkIndirect(data, zones, pointers, ptrs[dindirectIdx], ref remaining, 2, blockSize);
    if (remaining > 0 && tindirectIdx >= 0 && tindirectIdx < ptrs.Length)
      WalkIndirect(data, zones, pointers, ptrs[tindirectIdx], ref remaining, 3, blockSize);

    return (zones, pointers);
  }

  /// <summary>Claims one logical zone; a zone of nought is a hole and owns nothing.</summary>
  private static void AddZone(List<uint> zones, uint zone, ref long remaining, int blockSize) {
    if (zone != 0) zones.Add(zone);
    remaining -= blockSize;
  }

  private static void WalkIndirect(byte[] data, List<uint> zones, List<uint> pointers,
      uint indirectZone, ref long remaining, int level, int blockSize) {
    if (remaining <= 0) return;

    var ptrsPerBlock = blockSize / 4;
    var reach = (long)ptrsPerBlock;
    for (var i = 1; i < level; ++i) reach *= ptrsPerBlock;

    // An absent block is a hole as wide as everything it would have addressed.
    var off = indirectZone == 0 ? -1 : (long)indirectZone * blockSize;
    if (off < 0 || off + blockSize > data.Length) {
      remaining -= reach * blockSize;
      return;
    }

    pointers.Add(indirectZone);
    for (var i = 0; i < ptrsPerBlock && remaining > 0; i++) {
      var ptr = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)off + i * 4));
      if (level == 1)
        AddZone(zones, ptr, ref remaining, blockSize);
      else
        WalkIndirect(data, zones, pointers, ptr, ref remaining, level - 1, blockSize);
    }
  }

  // Groups an ordered zone list into contiguous (first, count) runs.
  private static List<(uint first, int count)> GroupRuns(List<uint> zones) {
    var runs = new List<(uint, int)>();
    if (zones.Count == 0) return runs;
    var sorted = new List<uint>(zones);
    sorted.Sort();
    var runStart = sorted[0];
    var prev = sorted[0];
    var count = 1;
    for (var i = 1; i < sorted.Count; i++) {
      if (sorted[i] == prev + 1) {
        count++;
      } else {
        runs.Add((runStart, count));
        runStart = sorted[i];
        count = 1;
      }
      prev = sorted[i];
    }
    runs.Add((runStart, count));
    return runs;
  }
}
