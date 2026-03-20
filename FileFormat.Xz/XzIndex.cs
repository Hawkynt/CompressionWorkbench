using Compression.Core.Checksums;

namespace FileFormat.Xz;

/// <summary>
/// XZ index: record list of block sizes.
/// </summary>
internal sealed class XzIndex {
  /// <summary>
  /// List of (unpadded size, uncompressed size) pairs.
  /// </summary>
  public List<(long UnpaddedSize, long UncompressedSize)> Records { get; } = [];

  /// <summary>
  /// Reads an XZ index from the stream.
  /// </summary>
  public static XzIndex Read(Stream stream) {
    var startPos = stream.Position;

    int indicator = stream.ReadByte();
    if (indicator != 0x00)
      throw new InvalidDataException("Invalid XZ index indicator byte.");

    var index = new XzIndex();
    ulong numRecords = XzVarint.Read(stream);

    for (ulong i = 0; i < numRecords; ++i) {
      long unpaddedSize = (long)XzVarint.Read(stream);
      long uncompressedSize = (long)XzVarint.Read(stream);
      index.Records.Add((unpaddedSize, uncompressedSize));
    }

    // Padding to 4-byte alignment
    var dataSize = stream.Position - startPos;
    int padding = (int)((4 - (dataSize % 4)) % 4);
    for (int i = 0; i < padding; ++i) {
      int b = stream.ReadByte();
      if (b != 0)
        throw new InvalidDataException("Non-zero padding in XZ index.");
    }

    // Read and verify CRC-32
    byte[] crcBuf = new byte[4];
    if (stream.Read(crcBuf, 0, 4) != 4)
      throw new EndOfStreamException("Truncated XZ index CRC.");

    uint storedCrc = (uint)(crcBuf[0] | (crcBuf[1] << 8) | (crcBuf[2] << 16) | (crcBuf[3] << 24));

    // Compute CRC over the index data (indicator + records + padding)
    var endPos = stream.Position - 4; // before CRC
    long indexDataLen = endPos - startPos;

    stream.Position = startPos;
    byte[] indexData = new byte[indexDataLen];
    _ = stream.Read(indexData, 0, (int)indexDataLen);
    stream.Position = endPos + 4; // skip past CRC

    uint computedCrc = Crc32.Compute(indexData);
    if (storedCrc != computedCrc)
      throw new InvalidDataException("XZ index CRC mismatch.");

    return index;
  }

  /// <summary>
  /// Writes this XZ index to the stream.
  /// </summary>
  public void Write(Stream stream) {
    using var ms = new MemoryStream();

    // Indicator byte
    ms.WriteByte(0x00);

    // Number of records
    XzVarint.Write(ms, (ulong)Records.Count);

    // Records
    foreach (var (unpaddedSize, uncompressedSize) in Records) {
      XzVarint.Write(ms, (ulong)unpaddedSize);
      XzVarint.Write(ms, (ulong)uncompressedSize);
    }

    byte[] indexData = ms.ToArray();

    // Padding to 4-byte alignment
    int padding = (int)((4 - (indexData.Length % 4)) % 4);
    byte[] paddedData = new byte[indexData.Length + padding];
    indexData.AsSpan().CopyTo(paddedData);

    // CRC-32 over padded data
    uint crc = Crc32.Compute(paddedData);

    stream.Write(paddedData);
    stream.WriteByte((byte)crc);
    stream.WriteByte((byte)(crc >> 8));
    stream.WriteByte((byte)(crc >> 16));
    stream.WriteByte((byte)(crc >> 24));
  }

  /// <summary>
  /// Gets the size of the index in bytes (for backward size calculation).
  /// </summary>
  public int GetSize() {
    using var ms = new MemoryStream();
    Write(ms);
    return (int)ms.Length;
  }
}
