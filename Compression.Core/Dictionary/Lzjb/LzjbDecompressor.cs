namespace Compression.Core.Dictionary.Lzjb;

/// <summary>
/// Decodes LZJB, Jeff Bonwick's compressor from Solaris and ZFS (<c>compression=lzjb</c>).
/// </summary>
/// <remarks>
/// <para>Eight items follow each copy-map byte, whose bits are read least-significant
/// first. A clear bit is one literal byte. A set bit is a two-byte match <c>b0 b1</c>:
/// length <c>(b0 &gt;&gt; 2) + 3</c>, distance <c>((b0 &lt;&lt; 8) | b1) &amp; 0x3FF</c>,
/// copied byte by byte so it may overlap itself. Decoding stops once the declared output
/// length is reached, inside a match if need be; anything left in the input is padding.</para>
/// <para>Written from <c>docs/ZFS-ON-DISK.md</c>; OpenZFS (CDDL) was read for behaviour
/// only, and blocks OpenZFS itself compressed are decoded by the ZFS reference-pool tests.</para>
/// </remarks>
public static class LzjbDecompressor {
  private const int MatchBits = 6;
  private const int MatchMin = 3;
  private const int OffsetMask = (1 << (16 - LzjbDecompressor.MatchBits)) - 1;

  /// <summary>Decompresses an LZJB stream.</summary>
  /// <param name="data">The compressed byte stream.</param>
  /// <param name="originalLength">The exact length of the decompressed output.</param>
  /// <returns>The reconstructed original bytes.</returns>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int originalLength) {
    var output = new byte[originalLength];
    LzjbDecompressor.Decompress(data, output);
    return output;
  }

  /// <summary>Decompresses an LZJB stream until <paramref name="destination"/> is full.</summary>
  /// <param name="data">The compressed byte stream, possibly padded.</param>
  /// <param name="destination">Receives exactly its length in decoded bytes.</param>
  /// <exception cref="InvalidDataException">The stream is truncated or a match reaches before the output start.</exception>
  public static void Decompress(ReadOnlySpan<byte> data, Span<byte> destination) {
    var op = 0;
    var ip = 0;
    var copymap = 0;
    var bit = 8;

    while (op < destination.Length) {
      if (bit == 8) {
        if (ip >= data.Length)
          throw new InvalidDataException("LZJB: stream ended before the declared length.");
        copymap = data[ip++];
        bit = 0;
      }

      if (((copymap >> bit++) & 1) == 0) {
        if (ip >= data.Length)
          throw new InvalidDataException("LZJB: stream ended before the declared length.");
        destination[op++] = data[ip++];
        continue;
      }

      if (ip + 2 > data.Length)
        throw new InvalidDataException("LZJB: match token truncated.");
      var length = (data[ip] >> (8 - LzjbDecompressor.MatchBits)) + LzjbDecompressor.MatchMin;
      var distance = ((data[ip] << 8) | data[ip + 1]) & LzjbDecompressor.OffsetMask;
      ip += 2;
      var from = op - distance;
      if (distance == 0 || from < 0)
        throw new InvalidDataException("LZJB: match distance reaches before the start of the output.");
      for (var end = Math.Min(op + length, destination.Length); op < end;)
        destination[op++] = destination[from++];
    }
  }
}
