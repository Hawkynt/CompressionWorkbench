namespace FileFormat.SevenZip;

/// <summary>
/// Reads and writes 7z variable-length integers.
/// </summary>
/// <remarks>
/// The encoding uses the first byte's leading 1-bits to indicate the number
/// of additional bytes. Remaining bits of the first byte plus the following
/// bytes (in little-endian order) form the value.
/// <list type="bullet">
/// <item><c>0xxxxxxx</c> -- 7-bit value (1 byte total)</item>
/// <item><c>10xxxxxx + 1 byte</c> -- 14-bit value (2 bytes total)</item>
/// <item><c>110xxxxx + 2 bytes</c> -- 21-bit value (3 bytes total)</item>
/// <item>...up to 8 extra bytes for full 64-bit values</item>
/// </list>
/// </remarks>
internal static class SevenZipVarInt {
  /// <summary>
  /// Reads a 7z variable-length integer from a stream.
  /// </summary>
  /// <param name="stream">The stream to read from.</param>
  /// <returns>The decoded value.</returns>
  public static ulong Read(Stream stream) {
    int firstByte = stream.ReadByte();
    if (firstByte < 0)
      throw new EndOfStreamException("Unexpected end of stream reading 7z varint.");

    // Count leading 1-bits
    int mask = 0x80;
    var extraBytes = 0;
    while (extraBytes < 8 && (firstByte & mask) != 0) {
      ++extraBytes;
      mask >>= 1;
    }

    // Special case: all 8 bits are leading 1s (0xFF prefix) means read 8 more bytes
    if (extraBytes == 8) {
      ulong value = 0;
      for (int i = 0; i < 8; ++i) {
        int b = stream.ReadByte();
        if (b < 0)
          throw new EndOfStreamException("Unexpected end of stream reading 7z varint.");
        value |= (ulong)b << (i * 8);
      }

      return value;
    }

    // Read extraBytes additional bytes in little-endian order (low bytes first)
    ulong result = 0;
    for (int i = 0; i < extraBytes; ++i) {
      int b = stream.ReadByte();
      if (b < 0)
        throw new EndOfStreamException("Unexpected end of stream reading 7z varint.");
      result |= (ulong)b << (i * 8);
    }

    // Add the remaining data bits of the first byte as the high part
    ulong highPart = (ulong)(firstByte & (mask - 1));
    result += highPart << (extraBytes * 8);

    return result;
  }

  /// <summary>
  /// Writes a 7z variable-length integer to a stream.
  /// </summary>
  /// <param name="stream">The stream to write to.</param>
  /// <param name="value">The value to encode.</param>
  public static void Write(Stream stream, ulong value) {
    // Determine how many bytes we need
    // 1 byte: value < 2^7
    // 2 bytes: value < 2^14
    // ...
    // k+1 bytes: value < 2^(7*(k+1)/... )
    // We need to find the smallest k such that value fits in (7-k) + 8*k = 7+7*k bits
    // Actually: 1 byte = 7 data bits, 2 bytes = 6+8=14, 3=5+16=21, etc.
    // So k extra bytes gives us (7-k) + 8*k = 7+7k data bits.

    if (value < 0x80) {
      stream.WriteByte((byte)value);
      return;
    }

    // Find the number of extra bytes needed
    var extraBytes = 1;
    ulong limit = 1UL << 14; // 2 bytes: 14 bits
    while (extraBytes < 8 && value >= limit) {
      ++extraBytes;
      if (extraBytes < 8)
        limit = 1UL << (7 + 7 * extraBytes);
    }

    if (extraBytes >= 8) {
      // Write 0xFF followed by 8 raw bytes
      stream.WriteByte(0xFF);
      for (int i = 0; i < 8; ++i)
        stream.WriteByte((byte)(value >> (i * 8)));

      return;
    }

    // Build the first byte: leading 1-bits followed by high bits of value
    int dataBitsInFirstByte = 7 - extraBytes;
    var firstByte = 0;
    for (int i = 0; i < extraBytes; ++i)
      firstByte |= 0x80 >> i;

    firstByte |= (int)(value >> (extraBytes * 8)) & ((1 << dataBitsInFirstByte) - 1);
    stream.WriteByte((byte)firstByte);

    // Write remaining bytes in little-endian
    for (int i = 0; i < extraBytes; ++i)
      stream.WriteByte((byte)(value >> (i * 8)));
  }
}
