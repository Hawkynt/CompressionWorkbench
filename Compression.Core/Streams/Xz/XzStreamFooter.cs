using Compression.Core.Checksums;

namespace FileFormat.Xz;

/// <summary>
/// XZ stream footer: 4-byte CRC-32 + 4-byte backward size + 2-byte flags + 2-byte magic.
/// </summary>
internal readonly record struct XzStreamFooter(uint BackwardSize, byte CheckType) {
  /// <summary>
  /// Reads an XZ stream footer from the stream.
  /// </summary>
  public static XzStreamFooter Read(Stream stream) {
    var buf = new byte[12];
    if (stream.Read(buf, 0, 12) != 12)
      throw new EndOfStreamException("Truncated XZ stream footer.");

    var storedCrc = (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));

    // Backward size (4 bytes, little-endian)
    var backwardSize = (uint)(buf[4] | (buf[5] << 8) | (buf[6] << 16) | (buf[7] << 24));

    // Flags
    if (buf[8] != 0x00)
      throw new InvalidDataException("Invalid XZ stream footer flags.");
    var checkType = (byte)(buf[9] & 0x0F);

    // Magic
    if (buf[10] != (byte)'Y' || buf[11] != (byte)'Z')
      throw new InvalidDataException("Invalid XZ stream footer magic.");

    // Verify CRC of backward size + flags (6 bytes: buf[4..9])
    var computedCrc = Crc32.Compute(buf.AsSpan(4, 6));
    if (storedCrc != computedCrc)
      throw new InvalidDataException("XZ stream footer CRC mismatch.");

    return new XzStreamFooter(backwardSize, checkType);
  }

  /// <summary>
  /// Writes this XZ stream footer to the stream.
  /// </summary>
  public void Write(Stream stream) {
    var payload = new byte[6];
    payload[0] = (byte)BackwardSize;
    payload[1] = (byte)(BackwardSize >> 8);
    payload[2] = (byte)(BackwardSize >> 16);
    payload[3] = (byte)(BackwardSize >> 24);
    payload[4] = 0x00;
    payload[5] = (byte)(CheckType & 0x0F);

    var crc = Crc32.Compute(payload);

    stream.WriteByte((byte)crc);
    stream.WriteByte((byte)(crc >> 8));
    stream.WriteByte((byte)(crc >> 16));
    stream.WriteByte((byte)(crc >> 24));
    stream.Write(payload);
    stream.WriteByte((byte)'Y');
    stream.WriteByte((byte)'Z');
  }
}
