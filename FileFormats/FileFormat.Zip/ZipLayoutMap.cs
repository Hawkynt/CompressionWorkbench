#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.Zip;

/// <summary>
/// Walks a ZIP central directory and emits a fail-closed byte-level layout of local headers,
/// compressed payloads, optional data descriptors, the central directory and EOCD.
/// </summary>
public static class ZipLayoutMap {

  private sealed record EntryLayout(
    ZipEntry Entry,
    long HeaderOffset,
    int HeaderLength,
    long DataOffset,
    long DataLength,
    int DescriptorLength) {
    public long End => this.DataOffset + this.DataLength + this.DescriptorLength;
  }

  public static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanSeek)
      yield break;

    archive.Position = 0;

    long cdOffset;
    long cdSize;
    int cdCount;
    long eocdOffset;
    try {
      (cdOffset, cdSize, cdCount, _) = ZipEndOfCentralDirectory.Read(archive);
      eocdOffset = FindEocdOffset(archive);
    } catch {
      yield break;
    }

    List<ZipEntry> entries;
    try {
      entries = ReadCentralDirectory(archive, cdOffset, cdCount);
    } catch {
      yield break;
    }

    var ordered = entries.OrderBy(entry => entry.LocalHeaderOffset).ToArray();
    var layouts = new List<EntryLayout>(ordered.Length);
    for (var i = 0; i < ordered.Length; ++i) {
      var entry = ordered[i];
      if (!TryReadLocalHeaderLength(archive, entry.LocalHeaderOffset, out var headerLength))
        continue;

      var dataOffset = entry.LocalHeaderOffset + headerLength;
      if (dataOffset < 0 || entry.CompressedSize < 0 || dataOffset > archive.Length - entry.CompressedSize)
        continue;

      var dataEnd = dataOffset + entry.CompressedSize;
      var nextBoundary = cdOffset;
      for (var next = i + 1; next < ordered.Length; ++next) {
        if (ordered[next].LocalHeaderOffset > entry.LocalHeaderOffset) {
          nextBoundary = Math.Min(nextBoundary, ordered[next].LocalHeaderOffset);
          break;
        }
      }
      nextBoundary = Math.Clamp(nextBoundary, dataEnd, archive.Length);

      var descriptorLength = ReadDataDescriptorLength(archive, entry, dataEnd, nextBoundary);
      layouts.Add(new EntryLayout(
        entry,
        entry.LocalHeaderOffset,
        headerLength,
        dataOffset,
        entry.CompressedSize,
        descriptorLength));
    }

    foreach (var layout in layouts) {
      yield return new DefragBlockInfo(
        layout.HeaderOffset,
        layout.HeaderLength,
        DefragBlockKind.MetadataReserved,
        FileName: $"LFH: {layout.Entry.FileName}");

      if (layout.DataLength > 0) {
        yield return new DefragBlockInfo(
          layout.DataOffset,
          layout.DataLength,
          DefragBlockKind.Used,
          FileName: layout.Entry.FileName,
          Classification: ClassifyMethod(layout.Entry.CompressionMethod));
      }

      if (layout.DescriptorLength > 0) {
        yield return new DefragBlockInfo(
          layout.DataOffset + layout.DataLength,
          layout.DescriptorLength,
          DefragBlockKind.MetadataReserved,
          FileName: $"Data descriptor: {layout.Entry.FileName}");
      }
    }

    if (cdSize > 0) {
      yield return new DefragBlockInfo(
        cdOffset,
        cdSize,
        DefragBlockKind.MetadataReserved,
        FileName: "Central Directory");
    }

    if (eocdOffset >= 0) {
      yield return new DefragBlockInfo(
        eocdOffset,
        archive.Length - eocdOffset,
        DefragBlockKind.MetadataReserved,
        FileName: "End of Central Directory");
    }

    var regions = new List<(long Start, long End)>(layouts.Count + 2);
    regions.AddRange(layouts.Select(layout => (layout.HeaderOffset, layout.End)));
    if (cdSize > 0)
      regions.Add((cdOffset, cdOffset + cdSize));
    if (eocdOffset >= 0)
      regions.Add((eocdOffset, archive.Length));
    regions.Sort(static (left, right) => left.Start.CompareTo(right.Start));

    var cursor = 0L;
    foreach (var (start, end) in regions) {
      if (start > cursor) {
        yield return new DefragBlockInfo(
          cursor,
          start - cursor,
          DefragBlockKind.Free,
          FileName: "Dead space");
      }
      cursor = Math.Max(cursor, end);
    }

    if (cursor < archive.Length) {
      yield return new DefragBlockInfo(
        cursor,
        archive.Length - cursor,
        DefragBlockKind.Free,
        FileName: "Dead space");
    }
  }

  private static List<ZipEntry> ReadCentralDirectory(Stream archive, long cdOffset, int cdCount) {
    archive.Position = cdOffset;
    using var reader = new BinaryReader(archive, System.Text.Encoding.Latin1, leaveOpen: true);
    var entries = new List<ZipEntry>(cdCount);
    for (var i = 0; i < cdCount; ++i)
      entries.Add(ZipCentralDirectoryEntry.Read(reader));
    return entries;
  }

  private static bool TryReadLocalHeaderLength(Stream archive, long offset, out int length) {
    length = 0;
    if (offset < 0 || offset > archive.Length - 30)
      return false;

    Span<byte> header = stackalloc byte[30];
    archive.Position = offset;
    if (!ReadExactly(archive, header))
      return false;
    if (BinaryPrimitives.ReadUInt32LittleEndian(header) != ZipConstants.LocalFileHeaderSignature)
      return false;

    var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
    var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
    length = 30 + fileNameLength + extraLength;
    return offset <= archive.Length - length;
  }

  /// <summary>
  /// Returns the number of bytes occupied by an optional ZIP data descriptor. When bit 3 says a
  /// descriptor exists but its values cannot be proven, the maximum standard descriptor span up
  /// to the next known structure is reserved rather than exposed as free space.
  /// </summary>
  private static int ReadDataDescriptorLength(Stream archive, ZipEntry entry, long offset, long nextBoundary) {
    if ((entry.GeneralPurposeFlags & ZipConstants.FlagDataDescriptor) == 0)
      return 0;

    var availableLong = Math.Min(24L, Math.Max(0, nextBoundary - offset));
    var available = (int)availableLong;
    if (available == 0)
      return 0;

    Span<byte> data = stackalloc byte[24];
    archive.Position = offset;
    var read = ReadUpTo(archive, data[..available]);
    if (read == 0)
      return 0;
    data = data[..read];

    var signaturePresent = data.Length >= 4 &&
      BinaryPrimitives.ReadUInt32LittleEndian(data) == ZipConstants.DataDescriptorSignature;

    if (signaturePresent) {
      if (Matches32BitDescriptor(data, 4, entry))
        return 16;
      if (Matches64BitDescriptor(data, 4, entry))
        return 24;
    }

    if (Matches32BitDescriptor(data, 0, entry))
      return 12;
    if (Matches64BitDescriptor(data, 0, entry))
      return 20;

    return read;
  }

  private static bool Matches32BitDescriptor(ReadOnlySpan<byte> data, int offset, ZipEntry entry) {
    if (entry.CompressedSize > uint.MaxValue || entry.UncompressedSize > uint.MaxValue || data.Length < offset + 12)
      return false;
    return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) == entry.Crc32 &&
           BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]) == (uint)entry.CompressedSize &&
           BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]) == (uint)entry.UncompressedSize;
  }

  private static bool Matches64BitDescriptor(ReadOnlySpan<byte> data, int offset, ZipEntry entry) {
    if (data.Length < offset + 20)
      return false;
    return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) == entry.Crc32 &&
           BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 4)..]) == (ulong)entry.CompressedSize &&
           BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 12)..]) == (ulong)entry.UncompressedSize;
  }

  private static bool ReadExactly(Stream stream, Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = stream.Read(buffer[total..]);
      if (read <= 0)
        return false;
      total += read;
    }
    return true;
  }

  private static int ReadUpTo(Stream stream, Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = stream.Read(buffer[total..]);
      if (read <= 0)
        break;
      total += read;
    }
    return total;
  }

  private static DefragBlockClass ClassifyMethod(ZipCompressionMethod method) => method switch {
    ZipCompressionMethod.Store => DefragBlockClass.Frozen,
    ZipCompressionMethod.Deflate or ZipCompressionMethod.Deflate64 => DefragBlockClass.Normal,
    ZipCompressionMethod.BZip2 => DefragBlockClass.Cold,
    ZipCompressionMethod.Lzma or ZipCompressionMethod.Zstd or ZipCompressionMethod.Ppmd => DefragBlockClass.Hot,
    _ => DefragBlockClass.Normal,
  };

  private static long FindEocdOffset(Stream stream) {
    var searchLength = Math.Min(stream.Length, 65_557);
    var searchStart = stream.Length - searchLength;
    var buffer = new byte[checked((int)searchLength)];
    stream.Position = searchStart;
    var bytesRead = ReadUpTo(stream, buffer);
    for (var i = bytesRead - 22; i >= 0; --i) {
      if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i, 4)) == ZipConstants.EndOfCentralDirectorySignature)
        return searchStart + i;
    }
    return -1;
  }
}
