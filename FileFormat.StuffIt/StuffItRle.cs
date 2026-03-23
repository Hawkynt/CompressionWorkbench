namespace FileFormat.StuffIt;

/// <summary>
/// StuffIt RLE decoder (compression method 1).
/// </summary>
/// <remarks>
/// The encoding uses 0x90 as the repeat marker:
/// <list type="bullet">
///   <item><description>0x90 followed by 0x00 encodes a literal 0x90 byte.</description></item>
///   <item><description>0x90 followed by <c>count</c> (1–255) means repeat the preceding byte
///   so that the total run length is <c>count + 1</c>.</description></item>
/// </list>
/// </remarks>
internal static class StuffItRle {
  private const byte Marker = StuffItConstants.RleMarker;

  /// <summary>
  /// Encodes data using StuffIt RLE compression.
  /// </summary>
  /// <remarks>
  /// Runs of 4 or more identical bytes are encoded as: byte, 0x90, (count-1).
  /// Literal 0x90 bytes are escaped as 0x90 0x00.
  /// </remarks>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return [];

    var output = new List<byte>(data.Length);
    var i = 0;

    while (i < data.Length) {
      var current = data[i];

      // Count the run length.
      var runLength = 1;
      while (i + runLength < data.Length && data[i + runLength] == current && runLength < 255)
        ++runLength;

      if (runLength >= 4) {
        // Emit the byte once, then marker + (runLength - 1).
        // The decoder sees the byte, then marker + count, and repeats it count more times.
        // Total output = 1 + count additional = runLength.
        // But if the byte itself is the marker, we need to emit it escaped first.
        if (current == Marker) {
          output.Add(Marker);
          output.Add(0x00); // literal 0x90
        } else {
          output.Add(current);
        }
        output.Add(Marker);
        output.Add((byte)(runLength - 1));
        i += runLength;
      } else {
        // Emit individual bytes, escaping 0x90.
        for (var r = 0; r < runLength; ++r) {
          output.Add(current);
          if (current == Marker)
            output.Add(0x00);
        }
        i += runLength;
      }
    }

    return [.. output];
  }

  /// <summary>
  /// Decodes StuffIt RLE-encoded data.
  /// </summary>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      return [];

    var output = new List<byte>(data.Length * 2);
    byte lastByte = 0;
    var i = 0;

    while (i < data.Length) {
      var current = data[i++];

      if (current != Marker) {
        output.Add(current);
        lastByte = current;
        continue;
      }

      // Marker byte: read the count.
      if (i >= data.Length)
        throw new InvalidDataException("Unexpected end of StuffIt RLE stream after marker byte.");

      var count = data[i++];

      if (count == 0) {
        // Literal 0x90.
        output.Add(Marker);
        lastByte = Marker;
        continue;
      }

      // Repeat lastByte `count` additional times (it was already emitted once).
      for (var r = 0; r < count; ++r)
        output.Add(lastByte);
    }

    return [.. output];
  }
}
