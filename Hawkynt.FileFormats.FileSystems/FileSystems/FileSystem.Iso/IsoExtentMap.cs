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
    byte[]? bootRecordSector = null;
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
      if (type == 0 && bootRecordSector == null) bootRecordSector = (byte[])sectorBuf.Clone();
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

    // El Torito: the boot catalog and every boot image it names are referenced
    // from nowhere in the directory tree, and a wipe or a move that treated them
    // as free space left the disc unbootable.
    if (bootRecordSector != null)
      foreach (var ext in ElToritoExtents(cache, bootRecordSector, image.Length))
        yield return ext;

    // Past the volume space an image may carry more (a hybrid disc's partitions,
    // an appended session). None of it is ours to call free.
    var volumeBlocks = BinaryPrimitives.ReadUInt32LittleEndian(pvdSector.AsSpan(80));
    var logicalBlock = BinaryPrimitives.ReadUInt16LittleEndian(pvdSector.AsSpan(128));
    if (logicalBlock == 0) logicalBlock = SectorSize;
    var volumeEnd = (long)volumeBlocks * logicalBlock;
    if (volumeEnd > 0 && volumeEnd < image.Length && HoldsData(cache, volumeEnd, image.Length))
      yield return new DefragBlockInfo(volumeEnd, image.Length - volumeEnd, DefragBlockKind.MetadataReserved,
        FileName: "beyond the ISO9660 volume space");
  }

  /// <summary>Whether any byte in [<paramref name="from" />, <paramref name="to" />) is non-zero.</summary>
  private static bool HoldsData(SectorCache cache, long from, long to) {
    var buffer = new byte[64 * 1024];
    for (var at = from; at < to; at += buffer.Length) {
      var n = (int)Math.Min(buffer.Length, to - at);
      cache.Read(at, buffer.AsSpan(0, n));
      if (buffer.AsSpan(0, n).ContainsAnyExcept((byte)0)) return true;
    }
    return false;
  }

  /// <summary>
  /// The El Torito boot catalog named by the boot record, and each boot image its
  /// valid entries point at (sector counts are in 512-byte virtual sectors).
  /// </summary>
  private static IEnumerable<DefragBlockInfo> ElToritoExtents(SectorCache cache, byte[] bootRecord, long imageLength) {
    var id = Encoding.ASCII.GetString(bootRecord, 7, 23);
    if (!id.StartsWith("EL TORITO SPECIFICATION", StringComparison.Ordinal)) yield break;
    var catalogLba = BinaryPrimitives.ReadUInt32LittleEndian(bootRecord.AsSpan(0x47));
    var catalogOff = (long)catalogLba * SectorSize;
    if (catalogLba == 0 || catalogOff + SectorSize > imageLength) yield break;
    yield return new DefragBlockInfo(catalogOff, SectorSize, DefragBlockKind.MetadataReserved,
      FileName: "El Torito boot catalog");
    var catalog = new byte[SectorSize];
    cache.Read(catalogOff, catalog);
    for (var e = 32; e + 32 <= SectorSize; e += 32) {
      var indicator = catalog[e];
      if (indicator is not (0x88 or 0x00)) continue;   // bootable / non-bootable entries
      var count = BinaryPrimitives.ReadUInt16LittleEndian(catalog.AsSpan(e + 6));
      var lba = BinaryPrimitives.ReadUInt32LittleEndian(catalog.AsSpan(e + 8));
      if (lba == 0) continue;
      var off = (long)lba * SectorSize;
      var len = Math.Max(1, (long)count) * 512;
      if (off >= imageLength) continue;
      yield return new DefragBlockInfo(off, Math.Min(len, imageLength - off), DefragBlockKind.MetadataReserved,
        FileName: "El Torito boot image");
    }
  }

  /// <summary>
  /// The Rock Ridge / SUSP continuation areas ("CE" entries) a directory record's
  /// system use field points at. They hold the long name, permissions, owners,
  /// times and link targets of the entry, live in sectors no directory extent
  /// covers, and zeroing them made every Rock Ridge attribute vanish.
  /// </summary>
  private static void CollectContinuationAreas(byte[] dir, int pos, int recLen, int nameLen, long imageLength,
      List<DefragBlockInfo> into) {
    var su = pos + 33 + nameLen + ((nameLen & 1) == 0 ? 1 : 0);
    var end = pos + recLen;
    while (su + 4 <= end) {
      var len = dir[su + 2];
      if (len < 4 || su + len > end) break;
      if (dir[su] == (byte)'C' && dir[su + 1] == (byte)'E' && len >= 28) {
        var block = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(su + 4));
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(su + 12));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(su + 20));
        var start = (long)block * SectorSize;
        if (block != 0 && start < imageLength) {
          // The whole sector is claimed: other entries' continuations share it.
          var sectors = ((long)offset + length + SectorSize - 1) / SectorSize;
          into.Add(new DefragBlockInfo(start, Math.Min(Math.Max(1, sectors) * SectorSize, imageLength - start),
            DefragBlockKind.MetadataReserved, FileName: "Rock Ridge continuation area"));
        }
      }
      su += len;
    }
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
      int lba, int length, string basePath, bool joliet, bool isRoot, HashSet<long>? owned = null) {
    owned ??= [];
    // The directory itself is a contiguous extent (LBA, length). It is reserved, not
    // movable: repointing a folder means rewriting its "." record, every child's
    // "..", the parent's record and both path tables, which the mover does not do.
    yield return new DefragBlockInfo((long)lba * SectorSize, length,
      DefragBlockKind.MetadataReserved,
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
      var continuations = new List<DefragBlockInfo>();

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

        if (!joliet) CollectContinuationAreas(dirBytes, pos, recLen, nameLen, image.Length, continuations);

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

      // Emit collected files for this directory. Both trees name the same file
      // extents; only the primary tree reports them as owners, since two owners of
      // one run would have the planner move the bytes twice.
      // Several records may name one extent (a hard link, or a mastering tool that
      // stored identical files once); the first name owns it, and the mover
      // repoints every record that names the extent when it moves.
      if (!joliet)
        foreach (var (byteOff, len, name) in files)
          if (owned.Add(byteOff))
            yield return new DefragBlockInfo(byteOff, len, DefragBlockKind.Used, name);
      foreach (var area in continuations.DistinctBy(a => a.Offset))
        yield return area;

      // Recurse into subdirectories (their extents are yielded inside).
      foreach (var (sublba, sublen, subpath) in subdirs) {
        foreach (var ext in WalkDirectory(image, cache, sublba, sublen, subpath, joliet, isRoot: false, owned))
          yield return ext;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(dirBytes);
    }
  }
}
