using Compression.Core.Checksums;

namespace FileFormat.Xz;

/// <summary>
/// XZ block header containing filter chain and optional sizes.
/// </summary>
internal sealed class XzBlockHeader {
  /// <summary>
  /// List of filters (filter ID, properties bytes).
  /// </summary>
  public List<(ulong FilterId, byte[] Properties)> Filters { get; } = [];

  /// <summary>
  /// Optional compressed size.
  /// </summary>
  public long? CompressedSize { get; set; }

  /// <summary>
  /// Optional uncompressed size.
  /// </summary>
  public long? UncompressedSize { get; set; }

  /// <summary>
  /// The total header size in bytes (as read from the stream).
  /// </summary>
  public int HeaderSize { get; private set; }

  /// <summary>
  /// Reads a block header from the stream.
  /// </summary>
  public static XzBlockHeader Read(Stream stream) {
    var sizeByte = stream.ReadByte();
    if (sizeByte <= 0)
      throw new EndOfStreamException("Unexpected end of XZ block header.");

    var headerSize = (sizeByte + 1) * 4;
    var headerData = new byte[headerSize];
    headerData[0] = (byte)sizeByte;
    if (stream.Read(headerData, 1, headerSize - 1) != headerSize - 1)
      throw new EndOfStreamException("Truncated XZ block header.");

    // Verify CRC-32 (last 4 bytes of headerData)
    var storedCrc = (uint)(headerData[headerSize - 4] | (headerData[headerSize - 3] << 8) |
                (headerData[headerSize - 2] << 16) | (headerData[headerSize - 1] << 24));
    var computedCrc = Crc32.Compute(headerData.AsSpan(0, headerSize - 4));
    if (storedCrc != computedCrc)
      throw new InvalidDataException("XZ block header CRC mismatch.");

    using var ms = new MemoryStream(headerData, 1, headerSize - 5);
    var header = new XzBlockHeader { HeaderSize = headerSize };

    var flags = ms.ReadByte();
    var numFilters = (flags & 0x03) + 1;
    var hasCompressedSize = (flags & 0x40) != 0;
    var hasUncompressedSize = (flags & 0x80) != 0;

    if (hasCompressedSize)
      header.CompressedSize = (long)XzVarint.Read(ms);
    if (hasUncompressedSize)
      header.UncompressedSize = (long)XzVarint.Read(ms);

    for (var i = 0; i < numFilters; ++i) {
      var filterId = XzVarint.Read(ms);
      var propsSize = (int)XzVarint.Read(ms);
      var props = new byte[propsSize];
      if (propsSize > 0)
        _ = ms.Read(props, 0, propsSize);
      header.Filters.Add((filterId, props));
    }

    return header;
  }

  /// <summary>
  /// Writes this block header to the stream.
  /// </summary>
  public void Write(Stream stream) {
    using var ms = new MemoryStream();

    // Flags byte
    var flags = (Filters.Count - 1) & 0x03;
    if (CompressedSize.HasValue) flags |= 0x40;
    if (UncompressedSize.HasValue) flags |= 0x80;
    ms.WriteByte((byte)flags);

    if (CompressedSize.HasValue)
      XzVarint.Write(ms, (ulong)CompressedSize.Value);
    if (UncompressedSize.HasValue)
      XzVarint.Write(ms, (ulong)UncompressedSize.Value);

    foreach (var (filterId, props) in Filters) {
      XzVarint.Write(ms, filterId);
      XzVarint.Write(ms, (ulong)props.Length);
      if (props.Length > 0)
        ms.Write(props);
    }

    var content = ms.ToArray();

    // Header size = (sizeByte + 1) * 4, where sizeByte is the first byte
    // Total = 1 (sizeByte) + content.Length + padding + 4 (CRC)
    var totalWithoutSizeByte = content.Length + 4; // content + CRC
    var paddedTotal = ((totalWithoutSizeByte + 3) / 4) * 4 + 4; // round up to 4-byte multiple + CRC
    var headerSize = 1 + content.Length;
    var paddedHeaderSize = ((headerSize + 3) / 4) * 4;
    var sizeByte = paddedHeaderSize / 4;

    var headerBuf = new byte[sizeByte * 4 + 4]; // sizeByte * 4 bytes content + 4 CRC
    headerBuf[0] = (byte)sizeByte;
    content.AsSpan().CopyTo(headerBuf.AsSpan(1));
    // Padding is already zero

    var crc = Crc32.Compute(headerBuf.AsSpan(0, sizeByte * 4));
    headerBuf[sizeByte * 4] = (byte)crc;
    headerBuf[sizeByte * 4 + 1] = (byte)(crc >> 8);
    headerBuf[sizeByte * 4 + 2] = (byte)(crc >> 16);
    headerBuf[sizeByte * 4 + 3] = (byte)(crc >> 24);

    stream.WriteByte((byte)sizeByte);
    stream.Write(headerBuf.AsSpan(1, headerBuf.Length - 1));
  }
}
