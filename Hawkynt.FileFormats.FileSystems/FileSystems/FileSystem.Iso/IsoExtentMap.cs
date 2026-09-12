#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Iso;

/// <summary>
/// Walks an ISO 9660 image and yields its actual on-disk byte layout — the
/// 32 KiB system area, every Volume Descriptor sector (PVD/SVD/VDST), the
/// path tables, every directory record's contiguous extent (ISO 9660 spec
/// requires single-extent files), and the trailing free space. Each file
/// surfaces as exactly one Used extent because ECMA-119 mandates contiguous
/// allocation.
/// <para>
/// Streaming: reads only the volume descriptor sectors + one directory's
/// contents at a time through a <see cref="SectorCache"/>. A 100 GB DVD/BD
/// image needs only ~256 MB of cache regardless of size — directory bytes
/// are pulled on-demand from disk rather than loaded whole.
/// </para>
/// </summary>
public static class IsoExtentMap {

  private const int SectorSize = 2048;

  /// <summary>
  /// Single-pass walker. Parses volume descriptors at sector 16+, then walks
  /// the directory tree from the root. Each file directory record carries an
  /// (extent_LBA, length) pair which is yielded as a single contiguous run.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < 17 * SectorSize) yield break;

    // SectorCache provides chunked LRU random reads — never load the whole
    // image. 100 GB DVD/BD images stay bounded to ~256 MB of cache.
    using var cache = new SectorCache(image);

    // 32 KiB system area at start (sectors 0-15).
    yield return new DefragBlockInfo(0, 16L * SectorSize, DefragBlockKind.MetadataReserved,
      FileName: "ISO9660 system area");

    // Volume descriptors: scan sectors 16..256 looking for CD001 records.
    // Read one sector at a time through the cache. Heap-allocated buffer —
    // Span<byte>/stackalloc can't live across `yield return` boundaries.
    int pvdOffset = -1, jolietOffset = -1;
    var vdSectors = new List<int>();
    var sectorBuf = new byte[SectorSize];
    byte[]? pvdSector = null;
    byte[]? jolietSector = null;
    for (var sector = 16; sector < 256; sector++) {
      var off = (long)sector * SectorSize;
      if (off + SectorSize > image.Length) break;
      cache.Read(off, sectorBuf);
      if (!IsCD001(sectorBuf)) {
        // Stop scanning once we leave the VD sequence.
        if (vdSectors.Count > 0) break;
        continue;
      }
      vdSectors.Add(sector);
      var type = sectorBuf[0];
      if (type == 0xFF) break; // VDST terminator
      if (type == 1 && pvdOffset < 0) {
        pvdOffset = (int)off;
        pvdSector = (byte[])sectorBuf.Clone();
      } else if (type == 2 && jolietOffset < 0) {
        if (sectorBuf[88] == 0x25 && sectorBuf[89] == 0x2F &&
            (sectorBuf[90] == 0x40 || sectorBuf[90] == 0x43 || sectorBuf[90] == 0x45)) {
          jolietOffset = (int)off;
          jolietSector = (byte[])sectorBuf.Clone();
        }
      }
    }

    if (pvdOffset < 0 || pvdSector == null) yield break;

    // Yield every VD sector as a metadata extent (PVD / SVD / VDST).
    foreach (var s in vdSectors) {
      yield return new DefragBlockInfo((long)s * SectorSize, SectorSize,
        DefragBlockKind.MetadataReserved, FileName: $"ISO9660 VD@{s}");
    }

    // Path tables (L + M) from the PVD (offset 140 = L, 148 = M big-endian) and,
    // when present, from the Joliet SVD. Both sets are live metadata and must be
    // surfaced so the unused-space wiper does not reclaim them.
    foreach (var ext in PathTableExtents(pvdSector, "ISO9660"))
      yield return ext;
    if (jolietSector != null)
      foreach (var ext in PathTableExtents(jolietSector, "Joliet"))
        yield return ext;

    // The directory extents of BOTH trees are live metadata: the primary
    // ECMA-119 tree (short names) and, when present, the Joliet tree (long
    // UCS-2 names). They share the same file-data extents, so emitting both
    // walks yields each shared file extent twice — harmless for the wiper /
    // visualiser, which treat extents as occupied regions. The Joliet walk is
    // emitted first so its long names win when a consumer keys by file path.
    if (jolietSector != null) {
      var jRootLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(jolietSector.AsSpan(156 + 2));
      var jRootLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(jolietSector.AsSpan(156 + 10));
      foreach (var ext in WalkDirectory(image, cache, jRootLba, jRootLen, "", joliet: true, isRoot: true))
        yield return ext;
    }

    var rootLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvdSector.AsSpan(156 + 2));
    var rootLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(pvdSector.AsSpan(156 + 10));
    foreach (var ext in WalkDirectory(image, cache, rootLba, rootLen, "", joliet: false, isRoot: true))
      yield return ext;
  }

  private static IEnumerable<DefragBlockInfo> PathTableExtents(byte[] descSector, string label) {
    var pathTableSize = BinaryPrimitives.ReadUInt32LittleEndian(descSector.AsSpan(132));
    var lPathLba = BinaryPrimitives.ReadUInt32LittleEndian(descSector.AsSpan(140));
    var mPathLba = BinaryPrimitives.ReadUInt32BigEndian(descSector.AsSpan(148));
    if (pathTableSize > 0 && lPathLba > 0) {
      var sectors = (long)((pathTableSize + SectorSize - 1) / SectorSize);
      yield return new DefragBlockInfo((long)lPathLba * SectorSize, sectors * SectorSize,
        DefragBlockKind.MetadataReserved, FileName: $"{label} L-path table");
    }
    if (pathTableSize > 0 && mPathLba > 0) {
      var sectors = (long)((pathTableSize + SectorSize - 1) / SectorSize);
      yield return new DefragBlockInfo((long)mPathLba * SectorSize, sectors * SectorSize,
        DefragBlockKind.MetadataReserved, FileName: $"{label} M-path table");
    }
  }

  private static bool IsCD001(byte[] data) =>
    data.Length > 5 &&
    data[1] == 'C' && data[2] == 'D' &&
    data[3] == '0' && data[4] == '0' && data[5] == '1';

  /// <summary>
  /// Walks one directory's contents from the stream via the cache. The
  /// directory's bytes are read on demand (one chunk at a time). Subdirs
  /// recurse — each subdir read is independent so working set stays bounded
  /// to one directory's bytes plus the LRU cache.
  /// </summary>
  private static IEnumerable<DefragBlockInfo> WalkDirectory(Stream image, SectorCache cache,
      int lba, int length, string basePath, bool joliet, bool isRoot) {
    // The directory itself is a contiguous extent (LBA, length) — yield as Used+Directory
    // so the block visualiser tints it gold instead of treating it as gray metadata.
    yield return new DefragBlockInfo((long)lba * SectorSize, length,
      DefragBlockKind.Used,
      FileName: isRoot ? "ISO9660 root dir" : $"dir:{basePath}",
      Classification: DefragBlockClass.Directory);

    if (length <= 0) yield break;

    // Read the directory contents through the cache. For huge directories
    // (rare in ISO) we still rely on the cache's LRU to bound memory.
    var dirBytes = ArrayPool<byte>.Shared.Rent(length);
    try {
      var dirOff = (long)lba * SectorSize;
      var toRead = (int)Math.Min(length, image.Length - dirOff);
      if (toRead <= 0) yield break;
      cache.Read(dirOff, dirBytes.AsSpan(0, toRead));

      // Collect subdirectory recursion targets — recurse AFTER the walk so we
      // don't disturb iteration if any caller materialises the IEnumerable lazily.
      var subdirs = new List<(int lba, int len, string path)>();
      var files = new List<(long byteOff, int len, string name)>();

      var pos = 0;
      var end = toRead;
      while (pos < end) {
        var recLen = dirBytes[pos];
        if (recLen == 0) {
          var nextSector = ((pos / SectorSize) + 1) * SectorSize;
          pos = nextSector;
          continue;
        }
        if (pos + recLen > end) break;

        var extLba = (int)BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(pos + 2));
        var dataLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(pos + 10));
        var flags = dirBytes[pos + 25];
        var nameLen = dirBytes[pos + 32];
        var isDir = (flags & 2) != 0;

        // Skip . and ..
        if (nameLen == 1 && (dirBytes[pos + 33] == 0 || dirBytes[pos + 33] == 1)) {
          pos += recLen;
          continue;
        }

        string name;
        if (joliet) {
          name = Encoding.BigEndianUnicode.GetString(dirBytes, pos + 33, nameLen);
        } else {
          name = Encoding.ASCII.GetString(dirBytes, pos + 33, nameLen);
        }
        var semi = name.IndexOf(';');
        if (semi >= 0) name = name[..semi];
        name = name.TrimEnd('.');

        if (string.IsNullOrEmpty(name)) {
          pos += recLen;
          continue;
        }

        var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";

        if (isDir) {
          subdirs.Add((extLba, dataLen, fullPath));
        } else {
          // ISO 9660 files are ALWAYS single-extent contiguous — one Used run per file.
          var byteOff = (long)extLba * SectorSize;
          if (byteOff + dataLen <= image.Length && dataLen > 0)
            files.Add((byteOff, dataLen, fullPath));
        }

        pos += recLen;
      }

      // Emit collected files for this directory.
      foreach (var (byteOff, len, name) in files)
        yield return new DefragBlockInfo(byteOff, len, DefragBlockKind.Used, name);

      // Recurse into subdirectories (their extents are yielded inside).
      foreach (var (sublba, sublen, subpath) in subdirs) {
        foreach (var ext in WalkDirectory(image, cache, sublba, sublen, subpath, joliet, isRoot: false))
          yield return ext;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(dirBytes);
    }
  }
}
