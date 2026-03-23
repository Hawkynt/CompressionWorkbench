namespace FileFormat.Rar;

/// <summary>
/// RAR5 variable-length integer encoding and decoding.
/// Each byte contributes 7 data bits; bit 7 (0x80) is the continuation flag.
/// </summary>
internal static class RarVint {
  /// <summary>
  /// Reads a RAR5 variable-length integer from the stream.
  /// </summary>
  /// <param name="stream">The stream to read from.</param>
  /// <param name="bytesRead">The number of bytes consumed.</param>
  /// <returns>The decoded value.</returns>
  public static ulong Read(Stream stream, out int bytesRead) {
    ulong result = 0;
    var shift = 0;
    bytesRead = 0;

    while (true) {
      var b = stream.ReadByte();
      if (b < 0)
        throw new EndOfStreamException("Unexpected end of RAR vint.");
      ++bytesRead;

      result |= (ulong)(b & 0x7F) << shift;

      if ((b & 0x80) == 0)
        break;

      shift += 7;
      if (bytesRead >= 10)
        throw new InvalidDataException("RAR vint too long.");
    }

    return result;
  }

  /// <summary>
  /// Reads a RAR5 variable-length integer from a byte span.
  /// </summary>
  /// <param name="data">The span to read from.</param>
  /// <param name="bytesRead">The number of bytes consumed.</param>
  /// <returns>The decoded value.</returns>
  public static ulong Read(ReadOnlySpan<byte> data, out int bytesRead) {
    ulong result = 0;
    var shift = 0;
    bytesRead = 0;

    while (bytesRead < data.Length) {
      var b = data[bytesRead++];
      result |= (ulong)(b & 0x7F) << shift;

      if ((b & 0x80) == 0)
        break;

      shift += 7;
      if (bytesRead >= 10)
        throw new InvalidDataException("RAR vint too long.");
    }

    return result;
  }

  /// <summary>
  /// Writes a RAR5 variable-length integer to the stream.
  /// </summary>
  /// <param name="stream">The stream to write to.</param>
  /// <param name="value">The value to encode.</param>
  public static void Write(Stream stream, ulong value) {
    do {
      var b = (byte)(value & 0x7F);
      value >>= 7;
      if (value != 0)
        b |= 0x80;
      stream.WriteByte(b);
    }
    while (value != 0);
  }

  /// <summary>
  /// Returns the number of bytes needed to encode the given value as a vint.
  /// </summary>
  /// <param name="value">The value to measure.</param>
  /// <returns>The encoded size in bytes.</returns>
  public static int EncodedSize(ulong value) {
    var size = 1;
    while (value > 0x7F) {
      value >>= 7;
      ++size;
    }
    return size;
  }
}
