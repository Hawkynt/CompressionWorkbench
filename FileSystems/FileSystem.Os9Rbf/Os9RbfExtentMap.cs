#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Os9Rbf;

/// <summary>
/// Walks a Microware OS-9 RBF disk image (256-byte sectors, big-endian fields)
/// and yields the actual on-disk byte layout — the identification sector +
/// allocation bitmap as <see cref="DefragBlockKind.MetadataReserved"/>, the
/// root directory FD + directory data sectors as
/// <see cref="DefragBlockKind.MetadataReserved"/>, every per-file FD sector as
/// <see cref="DefragBlockKind.MetadataReserved"/>, every (start, count) segment
/// in a file's segment list as a contiguous <see cref="DefragBlockKind.Used"/>
/// extent, and the rest as <see cref="DefragBlockKind.Free"/>.
/// </summary>
public static class Os9RbfExtentMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var data = ms.ToArray();
    if (data.Length < Os9Layout.SectorSize) yield break;

    var totalSectors = ReadU24Be(data, Os9Layout.Pd_DD_TOT);
    var rootLsn = ReadU24Be(data, Os9Layout.Pd_DD_DIR);
    var clusterSize = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(Os9Layout.Pd_DD_BIT));
    var bitmapBytes = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(Os9Layout.Pd_DD_MAP));

    if (totalSectors < 4 || rootLsn < 1 || clusterSize == 0) yield break;
    if ((long)totalSectors * Os9Layout.SectorSize > data.Length) {
      // Image truncated — clamp.
      totalSectors = data.Length / Os9Layout.SectorSize;
    }

    // Identification sector (LSN 0).
    yield return new DefragBlockInfo(0, Os9Layout.SectorSize,
      DefragBlockKind.MetadataReserved, FileName: "OS-9 RBF identification sector");

    // Allocation bitmap (LSN 1+).
    var bitmapBytesActual = bitmapBytes > 0 ? bitmapBytes : (totalSectors + 7) / 8;
    var bitmapSectors = (bitmapBytesActual + Os9Layout.SectorSize - 1) / Os9Layout.SectorSize;
    yield return new DefragBlockInfo(Os9Layout.BitmapLsn * Os9Layout.SectorSize,
      (long)bitmapSectors * Os9Layout.SectorSize,
      DefragBlockKind.MetadataReserved, FileName: "OS-9 RBF allocation bitmap");

    var owned = new bool[totalSectors];
    owned[0] = true;
    for (var s = 0; s < bitmapSectors && Os9Layout.BitmapLsn + s < totalSectors; s++)
      owned[Os9Layout.BitmapLsn + s] = true;

    // Root directory FD sector itself.
    if (rootLsn > 0 && rootLsn < totalSectors) {
      yield return new DefragBlockInfo((long)rootLsn * Os9Layout.SectorSize,
        Os9Layout.SectorSize, DefragBlockKind.MetadataReserved,
        FileName: "OS-9 RBF root directory FD");
      owned[rootLsn] = true;

      // Walk root FD's segment list to find where dir data lives — emit as metadata.
      // Copy FD into a managed array so we can yield while iterating its segments.
      var rootFd = new byte[Os9Layout.SectorSize];
      Array.Copy(data, rootLsn * Os9Layout.SectorSize, rootFd, 0, Os9Layout.SectorSize);
      foreach (var (segStart, segCount) in EnumerateSegments(rootFd)) {
        var byteOff = (long)segStart * Os9Layout.SectorSize;
        var byteLen = (long)segCount * Os9Layout.SectorSize;
        if (byteOff + byteLen > data.Length) byteLen = Math.Max(0, data.Length - byteOff);
        if (byteLen <= 0) continue;
        yield return new DefragBlockInfo(byteOff, byteLen,
          DefragBlockKind.MetadataReserved, FileName: "OS-9 RBF root directory data");
        for (var s = segStart; s < segStart + segCount && s < totalSectors; s++)
          owned[s] = true;
      }

      // Walk dir entries → per-file FD + their segment lists.
      var dirData = ConcatenateSegments(data, rootFd);
      for (var off = 0; off + Os9Layout.DirEntryBytes <= dirData.Length; off += Os9Layout.DirEntryBytes) {
        if (dirData[off] == 0) continue;
        var name = ReadHighBitTerminatedAscii(dirData.AsSpan(off, Os9Layout.DirEntryNameMaxBytes));
        if (string.IsNullOrEmpty(name) || name == "." || name == "..") continue;

        var fdLsn = ReadU24Be(dirData, off + Os9Layout.DirEntryFdLsnOffset);
        if (fdLsn == 0 || fdLsn >= totalSectors) continue;

        // FD sector itself counts as metadata for this file (per-file
        // descriptor sector — analogous to an inode block).
        yield return new DefragBlockInfo((long)fdLsn * Os9Layout.SectorSize,
          Os9Layout.SectorSize, DefragBlockKind.MetadataReserved,
          FileName: $"OS-9 RBF FD ({name})");
        owned[fdLsn] = true;

        // Copy FD into a managed array so we can yield while iterating.
        var fd = new byte[Os9Layout.SectorSize];
        Array.Copy(data, fdLsn * Os9Layout.SectorSize, fd, 0, Os9Layout.SectorSize);
        var attrs = fd[Os9Layout.FD_ATT];
        var isDir = (attrs & Os9Layout.FAttr_Directory) != 0;

        foreach (var (segStart, segCount) in EnumerateSegments(fd)) {
          if (segStart >= totalSectors) continue;
          var byteOff = (long)segStart * Os9Layout.SectorSize;
          var byteLen = (long)segCount * Os9Layout.SectorSize;
          if (byteOff + byteLen > data.Length) byteLen = Math.Max(0, data.Length - byteOff);
          if (byteLen <= 0) continue;

          yield return new DefragBlockInfo(byteOff, byteLen, DefragBlockKind.Used, name,
            Classification: isDir ? DefragBlockClass.Directory : null);
          for (var s = segStart; s < segStart + segCount && s < totalSectors; s++)
            owned[s] = true;
        }
      }
    }

    // Emit Free runs for unowned sectors.
    var freeStart = -1;
    for (var s = 0; s < totalSectors; s++) {
      if (!owned[s]) {
        if (freeStart < 0) freeStart = s;
      } else if (freeStart >= 0) {
        yield return new DefragBlockInfo((long)freeStart * Os9Layout.SectorSize,
          (long)(s - freeStart) * Os9Layout.SectorSize, DefragBlockKind.Free);
        freeStart = -1;
      }
    }
    if (freeStart >= 0) {
      yield return new DefragBlockInfo((long)freeStart * Os9Layout.SectorSize,
        (long)(totalSectors - freeStart) * Os9Layout.SectorSize, DefragBlockKind.Free);
    }
  }

  private static IEnumerable<(int Start, int Count)> EnumerateSegments(ReadOnlySpan<byte> fd) {
    var off = Os9Layout.FD_SEG;
    var list = new List<(int, int)>();
    while (off + Os9Layout.SegmentBytes <= fd.Length) {
      var startLsn = (fd[off] << 16) | (fd[off + 1] << 8) | fd[off + 2];
      var sectors = (fd[off + 3] << 8) | fd[off + 4];
      if (startLsn == 0) break;
      if (sectors == 0) { off += Os9Layout.SegmentBytes; continue; }
      list.Add((startLsn, sectors));
      off += Os9Layout.SegmentBytes;
    }
    return list;
  }

  private static byte[] ConcatenateSegments(byte[] image, ReadOnlySpan<byte> fd) {
    var size = (long)BinaryPrimitives.ReadUInt32BigEndian(fd[Os9Layout.FD_SIZ..]);
    using var ms = new MemoryStream();
    foreach (var (startLsn, sectors) in EnumerateSegments(fd)) {
      var byteOff = startLsn * Os9Layout.SectorSize;
      var bytes = sectors * Os9Layout.SectorSize;
      if (byteOff + bytes > image.Length) break;
      ms.Write(image, byteOff, bytes);
    }
    var buf = ms.ToArray();
    if (size <= 0 || size > buf.Length) return buf;
    var trimmed = new byte[size];
    Array.Copy(buf, trimmed, size);
    return trimmed;
  }

  private static int ReadU24Be(byte[] span, int offset)
    => (span[offset] << 16) | (span[offset + 1] << 8) | span[offset + 2];

  private static string ReadHighBitTerminatedAscii(ReadOnlySpan<byte> span) {
    var sb = new StringBuilder();
    for (var i = 0; i < span.Length; i++) {
      var b = span[i];
      if (b == 0) break;
      sb.Append((char)(b & 0x7F));
      if ((b & 0x80) != 0) break;
    }
    return sb.ToString();
  }
}
