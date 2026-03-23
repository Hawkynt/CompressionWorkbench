using Compression.Core.Checksums;

namespace FileFormat.Xz;

/// <summary>
/// XZ stream header: 6-byte magic + 2-byte flags + 4-byte CRC-32.
/// </summary>
internal readonly record struct XzStreamHeader(byte CheckType) {
  /// <summary>
  /// Reads an XZ stream header from the stream.
  /// </summary>
  public static XzStreamHeader Read(Stream stream) {
    var magic = new byte[6];
    if (stream.Read(magic, 0, 6) != 6)
      throw new EndOfStreamException("Truncated XZ stream header.");

    for (var i = 0; i < 6; ++i) {
      if (magic[i] != XzConstants.StreamHeaderMagic[i])
        throw new InvalidDataException("Invalid XZ stream header magic.");
    }

    var flags = new byte[2];
    if (stream.Read(flags, 0, 2) != 2)
      throw new EndOfStreamException("Truncated XZ stream header flags.");

    if (flags[0] != 0x00)
      throw new InvalidDataException("Invalid XZ stream flags (first byte must be 0).");

    var checkType = (byte)(flags[1] & 0x0F);

    // Verify CRC-32 of flags
    var crcBuf = new byte[4];
    if (stream.Read(crcBuf, 0, 4) != 4)
      throw new EndOfStreamException("Truncated XZ stream header CRC.");

    var storedCrc = (uint)(crcBuf[0] | (crcBuf[1] << 8) | (crcBuf[2] << 16) | (crcBuf[3] << 24));
    var computedCrc = Crc32.Compute(flags);

    if (storedCrc != computedCrc)
      throw new InvalidDataException("XZ stream header CRC mismatch.");

    return new XzStreamHeader(checkType);
  }

  /// <summary>
  /// Writes this XZ stream header to the stream.
  /// </summary>
  public void Write(Stream stream) {
    stream.Write(XzConstants.StreamHeaderMagic);

    byte[] flags = [0x00, (byte)(CheckType & 0x0F)];
    stream.Write(flags);

    var crc = Crc32.Compute(flags);
    stream.WriteByte((byte)crc);
    stream.WriteByte((byte)(crc >> 8));
    stream.WriteByte((byte)(crc >> 16));
    stream.WriteByte((byte)(crc >> 24));
  }
}
