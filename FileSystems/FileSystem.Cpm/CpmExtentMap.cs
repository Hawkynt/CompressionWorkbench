#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileSystem.Cpm;

/// <summary>
/// Walks a Digital Research CP/M 2.2 reference disk image (8" SSSD geometry —
/// 256 256 bytes, 2 reserved tracks, 1024-byte allocation blocks, 64-entry
/// directory) and yields the actual on-disk byte layout — the reserved tracks
/// (BIOS) + 2 KB directory blocks as <see cref="DefragBlockKind.MetadataReserved"/>,
/// every per-file allocation-block list as one or more contiguous-run extents,
/// and unused blocks as <see cref="DefragBlockKind.Free"/>. Used by the defrag
/// window's block-map preview.
/// </summary>
public static class CpmExtentMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < CpmLayout.TotalBytes) yield break;

    // Reserved tracks (BIOS): bytes [0 .. ReservedBytes). 2 tracks * 26 sectors
    // * 128 bytes = 6656 bytes. CP/M doesn't allocate them as data blocks.
    yield return new DefragBlockInfo(0, CpmLayout.ReservedBytes,
      DefragBlockKind.MetadataReserved, FileName: "CP/M reserved tracks (BIOS)");

    // Directory blocks 0 + 1 (the first 2 KB of the data area). These are the
    // 64 × 32-byte directory entries we're about to walk.
    var dirOff = (long)CpmLayout.ReservedBytes;
    var dirLen = (long)CpmLayout.DirectoryBytes;
    yield return new DefragBlockInfo(dirOff, dirLen,
      DefragBlockKind.MetadataReserved, FileName: "CP/M directory");

    // Walk every directory entry — multiple extents per file are matched by
    // (userCode, name, ext). Each extent has its own block list (16 single-byte
    // pointers in this geometry, since TotalBlocks=243 < 256).
    var owned = new bool[CpmLayout.TotalBlocks];
    // Pre-mark directory blocks 0 and 1 as owned (we already emitted them).
    owned[0] = true;
    owned[1] = true;

    // Group extents by (user, name, ext) so we can emit one set of runs per
    // logical file. Within a group, sort by extent number so the runs come
    // out in logical order.
    var groups = new Dictionary<(byte u, string n, string x), List<(int extent, int[] blocks)>>();
    var order = new List<(byte u, string n, string x)>();

    for (var i = 0; i < CpmLayout.DirectoryEntries; i++) {
      var off = CpmLayout.ReservedBytes + i * CpmLayout.DirectoryEntrySize;
      if (off + CpmLayout.DirectoryEntrySize > data.Length) break;
      var userCode = data[off];
      if (userCode == CpmLayout.EmptyEntryUserCode) continue;
      if (userCode > 0x1F) continue;

      var nameBytes = data.AsSpan(off + 1, 8).ToArray();
      var extBytes = data.AsSpan(off + 9, 3).ToArray();
      for (var k = 0; k < nameBytes.Length; k++) nameBytes[k] &= 0x7F;
      for (var k = 0; k < extBytes.Length; k++) extBytes[k] &= 0x7F;
      var name = Encoding.ASCII.GetString(nameBytes).TrimEnd(' ');
      var ext = Encoding.ASCII.GetString(extBytes).TrimEnd(' ');

      var ex = data[off + 12];
      var s2 = data[off + 14];
      var entryNumber = ((s2 & 0x3F) << 5) | (ex & 0x1F);

      var blocks = new int[16];
      for (var b = 0; b < 16; b++) blocks[b] = data[off + 16 + b];

      var key = (userCode, name, ext);
      if (!groups.TryGetValue(key, out var list)) {
        list = [];
        groups[key] = list;
        order.Add(key);
      }
      list.Add((entryNumber, blocks));
    }

    foreach (var key in order) {
      var extents = groups[key];
      extents.Sort((a, b) => a.extent.CompareTo(b.extent));
      var fullName = string.IsNullOrEmpty(key.x) ? key.n : $"{key.n}.{key.x}";

      // Build one ordered list of all referenced data blocks across all extents.
      var allBlocks = new List<int>();
      foreach (var (_, blocks) in extents) {
        for (var bi = 0; bi < blocks.Length; bi++) {
          var b = blocks[bi];
          if (b == 0) break;
          if (b >= CpmLayout.TotalBlocks) continue;
          allBlocks.Add(b);
        }
      }

      // Coalesce contiguous block numbers into runs.
      var runStart = -1;
      var runEnd = -1;
      foreach (var b in allBlocks) {
        if (b < CpmLayout.TotalBlocks) owned[b] = true;
        if (runStart < 0) {
          runStart = b;
          runEnd = b;
        } else if (b == runEnd + 1) {
          runEnd = b;
        } else {
          var startOff = (long)CpmLayout.ReservedBytes + (long)runStart * CpmLayout.BlockSize;
          var len = (long)(runEnd - runStart + 1) * CpmLayout.BlockSize;
          yield return new DefragBlockInfo(startOff, len, DefragBlockKind.Used, fullName);
          runStart = b;
          runEnd = b;
        }
      }
      if (runStart >= 0) {
        var startOff = (long)CpmLayout.ReservedBytes + (long)runStart * CpmLayout.BlockSize;
        var len = (long)(runEnd - runStart + 1) * CpmLayout.BlockSize;
        yield return new DefragBlockInfo(startOff, len, DefragBlockKind.Used, fullName);
      }
    }

    // Emit Free runs by collapsing the unowned data blocks.
    var freeStart = -1;
    for (var b = CpmLayout.DataBlockStart; b < CpmLayout.TotalBlocks; b++) {
      if (!owned[b]) {
        if (freeStart < 0) freeStart = b;
      } else if (freeStart >= 0) {
        var startOff = (long)CpmLayout.ReservedBytes + (long)freeStart * CpmLayout.BlockSize;
        var len = (long)(b - freeStart) * CpmLayout.BlockSize;
        yield return new DefragBlockInfo(startOff, len, DefragBlockKind.Free);
        freeStart = -1;
      }
    }
    if (freeStart >= 0) {
      var startOff = (long)CpmLayout.ReservedBytes + (long)freeStart * CpmLayout.BlockSize;
      var len = (long)(CpmLayout.TotalBlocks - freeStart) * CpmLayout.BlockSize;
      yield return new DefragBlockInfo(startOff, len, DefragBlockKind.Free);
    }
  }
}
