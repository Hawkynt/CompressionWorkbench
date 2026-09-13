namespace FileFormat.Xz;

/// <summary>
/// XZ variable-length integer encoding (1-9 bytes, little-endian, high bit = continuation).
/// </summary>
internal static class XzVarint {
  /// <summary>
  /// Reads a variable-length integer from the stream.
  /// </summary>
  public static ulong Read(Stream stream) {
    ulong result = 0;
    var shift = 0;

    for (var i = 0; i < 9; ++i) {
      var b = stream.ReadByte();
      if (b < 0)
        throw new EndOfStreamException("Unexpected end of stream reading varint.");

      result |= ((ulong)(b & 0x7F)) << shift;
      if ((b & 0x80) == 0)
        return result;

      shift += 7;
    }

    throw new InvalidDataException("XZ varint exceeds maximum size.");
  }

  /// <summary>
  /// Writes a variable-length integer to the stream.
  /// </summary>
  public static void Write(Stream stream, ulong value) {
    while (value >= 0x80) {
      stream.WriteByte((byte)(value | 0x80));
      value >>= 7;
    }
    stream.WriteByte((byte)value);
  }

  /// <summary>
  /// Returns the number of bytes needed to encode the value.
  /// </summary>
  public static int EncodedSize(ulong value) {
    var size = 1;
    while (value >= 0x80) {
      ++size;
      value >>= 7;
    }
    return size;
  }
}
