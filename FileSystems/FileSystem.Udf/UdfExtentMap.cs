#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Udf;

/// <summary>
/// Walks a UDF (ECMA-167) image and yields its actual on-disk byte layout —
/// the 32 KiB system area, NSR02/03 VRS sector, AVDP (LBA 256), the Volume
/// Descriptor Sequence (PD/LVD/etc.), the FSD, the root File Entry, and
/// every file's allocation descriptors as Used extents. Each File Entry's
/// short_ad / long_ad descriptor list yields one extent per descriptor —
/// already-coalesced as the ECMA-167 spec mandates contiguous physical
/// blocks per descriptor.
/// <para>
/// Streaming: reads only the volume descriptor sectors, the FSD, and each
/// File Entry as it is traversed — all through a <see cref="SectorCache"/>.
/// A 100 GB BD-R UDF image needs only ~256 MB of cache regardless of size.
/// </para>
/// </summary>
public static class UdfExtentMap {

  private const int SectorSize = 2048;
  private const int AvdpSector = 256;

  /// <summary>
  /// Single-pass walker. Locates AVDP@LBA 256 → VDS → FSD → root FE, then
  /// recurses through directory File Entries, decoding short_ad (8-byte) /
  /// long_ad (16-byte) allocation descriptors.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < (AvdpSector + 1) * SectorSize) yield break;

    using var cache = new SectorCache(image);

    // 32 KiB system area (sectors 0..15).
    yield return new DefragBlockInfo(0, 16L * SectorSize, DefragBlockKind.MetadataReserved,
      FileName: "UDF system area");

    // Heap-allocated scratch sector buffer — Span<byte>/stackalloc can't live
    // across `yield return` boundaries in an iterator method.
    var sectorBuf = new byte[SectorSize];

    // Volume Recognition Sequence (sectors 16..). Yield the BEA01/NSR02/03/TEA01 sectors.
    var foundNsr = false;
    for (var s = 16; s < 20; s++) {
      var off = (long)s * SectorSize;
      if (off + SectorSize > image.Length) break;
      cache.Read(off, sectorBuf);
      var id = Encoding.ASCII.GetString(sectorBuf, 1, 5);
      yield return new DefragBlockInfo(off, SectorSize, DefragBlockKind.MetadataReserved,
        FileName: $"UDF VRS:{id}");
      if (id is "NSR02" or "NSR03") foundNsr = true;
      if (id is "TEA01") break;
    }
    if (!foundNsr) yield break;

    // AVDP at sector 256.
    var avdpOff = (long)AvdpSector * SectorSize;
    if (avdpOff + SectorSize > image.Length) yield break;
    cache.Read(avdpOff, sectorBuf);
    var avdpTag = BinaryPrimitives.ReadUInt16LittleEndian(sectorBuf);
    if (avdpTag != 2) yield break;
    yield return new DefragBlockInfo(avdpOff, SectorSize, DefragBlockKind.MetadataReserved,
      FileName: "UDF AVDP");

    var mainVdsLoc = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(20));
    var mainVdsLen = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(16));

    // Walk VDS to find PD (5) and LVD (6).
    int partStart = 0;
    int fsdLbn = 0;
    var vdsSectors = (int)(mainVdsLen / SectorSize);
    for (var i = 0; i < vdsSectors && i < 64; i++) {
      var off = (long)(mainVdsLoc + i) * SectorSize;
      if (off + SectorSize > image.Length) break;
      cache.Read(off, sectorBuf);
      var tagId = BinaryPrimitives.ReadUInt16LittleEndian(sectorBuf);
      yield return new DefragBlockInfo(off, SectorSize, DefragBlockKind.MetadataReserved,
        FileName: $"UDF VDS tag={tagId}");

      if (tagId == 5) {
        partStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(188));
      } else if (tagId == 6) {
        fsdLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(252));
      } else if (tagId == 8) break; // terminator
    }

    // Read FSD.
    var fsdOffset = (long)(partStart + fsdLbn) * SectorSize;
    if (fsdOffset + SectorSize > image.Length) yield break;
    cache.Read(fsdOffset, sectorBuf);
    var fsdTag = BinaryPrimitives.ReadUInt16LittleEndian(sectorBuf);
    if (fsdTag != 256) yield break;
    yield return new DefragBlockInfo(fsdOffset, SectorSize, DefragBlockKind.MetadataReserved,
      FileName: "UDF FSD");

    var rootIcbLbn = BinaryPrimitives.ReadUInt32LittleEndian(sectorBuf.AsSpan(404));

    foreach (var ext in WalkDirectory(image, cache, partStart, (int)rootIcbLbn, "", isRoot: true,
                            seenIcbs: []))
      yield return ext;
  }

  private static IEnumerable<DefragBlockInfo> WalkDirectory(Stream image, SectorCache cache,
      int partStart, int icbLbn, string basePath, bool isRoot, HashSet<int> seenIcbs) {
    if (!seenIcbs.Add(icbLbn)) yield break;
    var feOffset = (long)(partStart + icbLbn) * SectorSize;
    if (feOffset + SectorSize > image.Length) yield break;

    // Read the File Entry sector through the cache.
    var feSector = ArrayPool<byte>.Shared.Rent(SectorSize);
    try {
      cache.Read(feOffset, feSector.AsSpan(0, SectorSize));

      var feTag = BinaryPrimitives.ReadUInt16LittleEndian(feSector.AsSpan(0));
      if (feTag is not (261 or 266)) yield break;

      // Yield the FE sector itself as Used+Directory — this is the on-disk
      // representation of a UDF folder, render it gold rather than gray meta.
      yield return new DefragBlockInfo(feOffset, SectorSize, DefragBlockKind.Used,
        FileName: isRoot ? "UDF root FE" : $"FE:{basePath}",
        Classification: DefragBlockClass.Directory);

      var icbFlags = BinaryPrimitives.ReadUInt16LittleEndian(feSector.AsSpan(34));
      var fileType = feSector[27];
      var infoLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(feSector.AsSpan(56));
      int lEa, lAd, adRel;
      if (feTag == 261) {
        lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(168));
        lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(172));
        adRel = 176 + lEa;
      } else {
        lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(208));
        lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(212));
        adRel = 216 + lEa;
      }

      if (fileType != 4) yield break; // not a directory

      // Decode ADs to gather directory bytes — streaming reads via cache.
      var adType = icbFlags & 0x07;
      var dirBytes = ReadAllocDataStream(image, cache, feSector, partStart, adRel, lAd, adType, infoLength);
      if (dirBytes == null) yield break;

      // Parse File Identifier Descriptors. Collect targets first, then recurse
      // after the FE sector buffer is returned to the pool.
      var subdirs = new List<(int lbn, string path)>();
      var fileTargets = new List<(int lbn, string path)>();

      var pos = 0;
      while (pos + 38 < dirBytes.Length) {
        var fidTag = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(pos));
        if (fidTag != 257) break;

        var lIu = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(pos + 36));
        var fidIdLen = dirBytes[pos + 19];
        var fidLen = 38 + lIu + fidIdLen;
        fidLen = (fidLen + 3) & ~3;

        var fidFlags = dirBytes[pos + 18];
        var isParent = (fidFlags & 0x08) != 0;
        var isDeleted = (fidFlags & 0x04) != 0;
        var isDir = (fidFlags & 0x02) != 0;
        var childIcbLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(pos + 24));

        if (!isParent && !isDeleted && fidIdLen > 0) {
          var nameStart = pos + 38 + lIu;
          string name;
          if (fidIdLen > 1 && dirBytes[nameStart] == 8)
            name = Encoding.UTF8.GetString(dirBytes, nameStart + 1, fidIdLen - 1);
          else if (fidIdLen > 1 && dirBytes[nameStart] == 16)
            name = Encoding.BigEndianUnicode.GetString(dirBytes, nameStart + 1, fidIdLen - 1);
          else
            name = Encoding.ASCII.GetString(dirBytes, nameStart, fidIdLen);
          name = name.TrimEnd('\0');

          var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";

          if (isDir) subdirs.Add((childIcbLbn, fullPath));
          else fileTargets.Add((childIcbLbn, fullPath));
        }

        pos += fidLen;
      }

      // Recurse — each call only holds one FE sector in pool.
      foreach (var (lbn, p) in fileTargets) {
        foreach (var ext in EnumerateFileExtents(image, cache, partStart, lbn, p))
          yield return ext;
      }
      foreach (var (lbn, p) in subdirs) {
        foreach (var ext in WalkDirectory(image, cache, partStart, lbn, p,
                              isRoot: false, seenIcbs))
          yield return ext;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(feSector);
    }
  }

  private static IEnumerable<DefragBlockInfo> EnumerateFileExtents(Stream image, SectorCache cache,
      int partStart, int icbLbn, string name) {
    var feOff = (long)(partStart + icbLbn) * SectorSize;
    if (feOff + SectorSize > image.Length) yield break;

    var feSector = ArrayPool<byte>.Shared.Rent(SectorSize);
    int adType;
    int lEa, lAd, adRel;
    long? runOff;
    long runLen;
    try {
      cache.Read(feOff, feSector.AsSpan(0, SectorSize));

      var feTag = BinaryPrimitives.ReadUInt16LittleEndian(feSector.AsSpan(0));
      if (feTag is not (261 or 266)) yield break;

      yield return new DefragBlockInfo(feOff, SectorSize, DefragBlockKind.MetadataReserved,
        FileName: $"FE:{name}");

      var icbFlags = BinaryPrimitives.ReadUInt16LittleEndian(feSector.AsSpan(34));
      if (feTag == 261) {
        lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(168));
        lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(172));
        adRel = 176 + lEa;
      } else {
        lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(208));
        lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(212));
        adRel = 216 + lEa;
      }

      adType = icbFlags & 0x07;
      if (adType == 3) {
        // Embedded — file data is inline inside the FE; covered by the FE metadata extent above.
        yield break;
      }

      // Walk allocation descriptors inside the FE sector. Coalesce adjacent same-run extents.
      runOff = null;
      runLen = 0;
      var pos = adRel;
      var end = adRel + lAd;
      if (end > SectorSize) end = SectorSize;

      while (pos < end) {
        long extLen, extByteOff;
        if (adType == 0) {
          if (pos + 8 > SectorSize) break;
          extLen = BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(pos)) & 0x3FFFFFFF;
          var extPos = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(pos + 4));
          extByteOff = (long)(partStart + extPos) * SectorSize;
          pos += 8;
        } else if (adType == 1) {
          if (pos + 16 > SectorSize) break;
          extLen = BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(pos)) & 0x3FFFFFFF;
          var extLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(pos + 4));
          extByteOff = (long)(partStart + extLbn) * SectorSize;
          pos += 16;
        } else break;

        if (extLen <= 0) continue;

        if (runOff is { } ro && ro + runLen == extByteOff) {
          runLen += extLen;
        } else {
          if (runOff is { } prev)
            yield return new DefragBlockInfo(prev, runLen, DefragBlockKind.Used, name);
          runOff = extByteOff;
          runLen = extLen;
        }
      }
    } finally {
      ArrayPool<byte>.Shared.Return(feSector);
    }

    if (runOff is { } finalOff)
      yield return new DefragBlockInfo(finalOff, runLen, DefragBlockKind.Used, name);
  }

  /// <summary>
  /// Reads the bytes addressed by a sequence of allocation descriptors. The
  /// descriptor sequence is in <paramref name="feSector"/> at relative offset
  /// <paramref name="adRel"/>; referenced data is read on demand via the cache.
  /// </summary>
  private static byte[]? ReadAllocDataStream(Stream image, SectorCache cache, byte[] feSector,
      int partStart, int adRel, int lAd, int adType, long infoLength) {
    if (adType == 3) {
      // Embedded — data is inside the FE sector at adRel.
      if (adRel + lAd <= SectorSize)
        return feSector.AsSpan(adRel, lAd).ToArray();
      return null;
    }

    using var ms = new MemoryStream();
    var pos = adRel;
    var end = adRel + lAd;
    if (end > SectorSize) end = SectorSize;
    var stride = adType == 0 ? 8 : 16;

    while (pos + stride <= end && ms.Length < infoLength) {
      var extLen = (int)(BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(pos)) & 0x3FFFFFFF);
      int extLbn;
      if (adType == 0) {
        extLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(pos + 4));
      } else {
        // long_ad: lbn at offset 4 within ADs (short part of LongAd)
        extLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(pos + 4));
      }
      pos += stride;

      if (extLen <= 0) continue;
      var off = (long)(partStart + extLbn) * SectorSize;
      if (off + extLen > image.Length) break;

      // Stream the extent into the buffer through the cache.
      var buf = ArrayPool<byte>.Shared.Rent(extLen);
      try {
        cache.Read(off, buf.AsSpan(0, extLen));
        ms.Write(buf, 0, extLen);
      } finally {
        ArrayPool<byte>.Shared.Return(buf);
      }
    }
    return ms.ToArray();
  }
}
