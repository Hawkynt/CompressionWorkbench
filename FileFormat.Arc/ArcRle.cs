namespace FileFormat.Arc;

/// <summary>
/// ARC-specific run-length encoding used by compression method 3 (Packed).
/// </summary>
/// <remarks>
/// The encoding uses 0x90 as a repeat marker:
/// <list type="bullet">
///   <item><description>0x90 followed by 0x00 encodes a literal 0x90 byte.</description></item>
///   <item><description>0x90 followed by <c>count</c> (≥ 2) means repeat the preceding byte so that
///   the total run length is <c>count</c> (i.e., <c>count - 1</c> additional copies).</description></item>
/// </list>
/// </remarks>
internal static class ArcRle {
  private const byte Marker = ArcConstants.RleMarker;

  /// <summary>
  /// Encodes data using ARC's RLE scheme (method 3 / Packed).
  /// </summary>
  /// <param name="data">The raw input data.</param>
  /// <returns>The RLE-encoded data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return [];

    var output = new List<byte>(data.Length);
    var i = 0;

    while (i < data.Length) {
      byte value = data[i];

      if (value == Marker) {
        // Literal 0x90: emit marker followed by 0x00.
        output.Add(Marker);
        output.Add(0x00);
        ++i;
        continue;
      }

      // Count how long this run is (max 255).
      int runEnd = i + 1;
      while (runEnd < data.Length && data[runEnd] == value && runEnd - i < 255)
        ++runEnd;

      int runLength = runEnd - i;
      output.Add(value);

      if (runLength >= 2) {
        // Encode the run: marker + count (total occurrences).
        output.Add(Marker);
        output.Add((byte)runLength);
      }

      i += runLength;
    }

    return [.. output];
  }

  /// <summary>
  /// Decodes ARC RLE-encoded data (method 3 / Packed).
  /// </summary>
  /// <param name="data">The RLE-encoded input data.</param>
  /// <returns>The decoded data.</returns>
  /// <exception cref="InvalidDataException">Thrown when the input contains invalid RLE sequences.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return [];

    var output = new List<byte>(data.Length * 2);
    byte lastByte = 0;
    var i = 0;

    while (i < data.Length) {
      byte current = data[i++];

      if (current != Marker) {
        output.Add(current);
        lastByte = current;
        continue;
      }

      // Marker byte: read the count.
      if (i >= data.Length)
        throw new InvalidDataException("Unexpected end of ARC RLE stream after marker byte.");

      byte count = data[i++];

      if (count == 0) {
        // Literal 0x90.
        output.Add(Marker);
        lastByte = Marker;
        continue;
      }

      if (count < 2)
        throw new InvalidDataException($"Invalid ARC RLE count {count}; minimum is 2.");

      // Repeat lastByte (count - 1) additional times (it was already emitted once).
      for (int r = 1; r < count; ++r)
        output.Add(lastByte);
    }

    return [.. output];
  }
}
