#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Zip;

/// <summary>
/// Walks the ZIP central directory and emits the byte-level layout of every
/// local file header, compressed data payload, the central directory itself,
/// and the EOCD record as <see cref="DefragBlockInfo"/> tiles.
/// </summary>
public static class ZipLayoutMap {

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;

    long cdOffset, cdSize;
    int cdCount;
    long eocdOffset;
    try {
      (cdOffset, cdSize, cdCount, _) = ZipEndOfCentralDirectory.Read(archive);
      eocdOffset = FindEocdOffset(archive);
    } catch {
      yield break;
    }

    // Read central directory entries to learn local header offsets
    archive.Position = cdOffset;
    var reader = new BinaryReader(archive, System.Text.Encoding.Latin1, leaveOpen: true);
    var entries = new List<ZipEntry>();
    for (var i = 0; i < cdCount; i++) {
      try {
        entries.Add(ZipCentralDirectoryEntry.Read(reader));
      } catch {
        break;
      }
    }

    // For each entry, emit local file header (MetadataReserved) + compressed data (Used)
    foreach (var entry in entries) {
      // Determine LFH size: fixed 30 bytes + filename + extra field
      archive.Position = entry.LocalHeaderOffset;
      if (archive.Position + 30 > archive.Length)
        continue;

      var lfhBuf = new byte[30];
      if (archive.Read(lfhBuf, 0, 30) < 30)
        continue;

      var fnLen = BitConverter.ToUInt16(lfhBuf, 26);
      var exLen = BitConverter.ToUInt16(lfhBuf, 28);
      var lfhSize = 30 + fnLen + exLen;

      // MetadataReserved tile for the local file header
      yield return new DefragBlockInfo(
        entry.LocalHeaderOffset,
        lfhSize,
        DefragBlockKind.MetadataReserved,
        FileName: $"LFH: {entry.FileName}");

      // Used tile for compressed data
      if (entry.CompressedSize > 0) {
        var classification = ClassifyMethod(entry.CompressionMethod);
        yield return new DefragBlockInfo(
          entry.LocalHeaderOffset + lfhSize,
          entry.CompressedSize,
          DefragBlockKind.Used,
          FileName: entry.FileName,
          Classification: classification);
      }
    }

    // Central directory = one MetadataReserved tile
    if (cdSize > 0) {
      yield return new DefragBlockInfo(
        cdOffset,
        cdSize,
        DefragBlockKind.MetadataReserved,
        FileName: "Central Directory");
    }

    // EOCD = one MetadataReserved tile (from EOCD start to end of file)
    if (eocdOffset >= 0) {
      var eocdSize = archive.Length - eocdOffset;
      yield return new DefragBlockInfo(
        eocdOffset,
        eocdSize,
        DefragBlockKind.MetadataReserved,
        FileName: "End of Central Directory");
    }

    // Detect gaps (free/dead bytes) between entries
    var regions = new List<(long Start, long End)>();
    foreach (var entry in entries) {
      archive.Position = entry.LocalHeaderOffset;
      if (archive.Position + 30 > archive.Length) continue;
      var buf = new byte[30];
      if (archive.Read(buf, 0, 30) < 30) continue;
      var fn = BitConverter.ToUInt16(buf, 26);
      var ex = BitConverter.ToUInt16(buf, 28);
      var totalEntrySize = 30 + fn + ex + entry.CompressedSize;
      regions.Add((entry.LocalHeaderOffset, entry.LocalHeaderOffset + totalEntrySize));
    }
    regions.Add((cdOffset, cdOffset + cdSize));
    if (eocdOffset >= 0)
      regions.Add((eocdOffset, archive.Length));

    regions.Sort((a, b) => a.Start.CompareTo(b.Start));

    var cursor = 0L;
    foreach (var (start, end) in regions) {
      if (start > cursor) {
        yield return new DefragBlockInfo(
          cursor,
          start - cursor,
          DefragBlockKind.Free,
          FileName: "Dead space");
      }
      if (end > cursor) cursor = end;
    }
  }

  private static DefragBlockClass ClassifyMethod(ZipCompressionMethod method) => method switch {
    ZipCompressionMethod.Store => DefragBlockClass.Frozen,
    ZipCompressionMethod.Deflate => DefragBlockClass.Normal,
    ZipCompressionMethod.Deflate64 => DefragBlockClass.Normal,
    ZipCompressionMethod.BZip2 => DefragBlockClass.Cold,
    ZipCompressionMethod.Lzma => DefragBlockClass.Hot,
    ZipCompressionMethod.Zstd => DefragBlockClass.Hot,
    ZipCompressionMethod.Ppmd => DefragBlockClass.Hot,
    _ => DefragBlockClass.Normal,
  };

  private static long FindEocdOffset(Stream stream) {
    var searchLen = Math.Min(stream.Length, 65557);
    var searchStart = stream.Length - searchLen;
    var buffer = new byte[searchLen];
    stream.Position = searchStart;
    var bytesRead = 0;
    while (bytesRead < buffer.Length) {
      var read = stream.Read(buffer, bytesRead, buffer.Length - bytesRead);
      if (read == 0) break;
      bytesRead += read;
    }
    for (var i = bytesRead - 22; i >= 0; --i) {
      if (buffer[i] == 0x50 && buffer[i + 1] == 0x4B &&
          buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
        return searchStart + i;
    }
    return -1;
  }
}
