namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Decodes ZIP Reduce (methods 2-5, compression factor 1-4): a byte stream coded through
/// per-predecessor "follower sets", carrying LZ77 back-references behind the escape byte 144.
/// </summary>
/// <remarks>
/// Converted from hwexpand in hwzip 2.4's reduce.c (Hans Wennborg, public domain), see
/// THIRD-PARTY-NOTICE.hwzip.txt. A back-reference before the start of the data yields zero bytes,
/// as PKZIP's decoder does.
/// </remarks>
public static class ReduceDecoder {
  internal const byte Dle = 144;

  /// <summary>
  /// Decompresses ZIP Reduce data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <param name="factor">The compression factor, 1-4 (method 2-5).</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decode(byte[] compressed, int originalSize, int factor) => Decode(compressed.AsSpan(), originalSize, factor);

  /// <summary>
  /// Decompresses ZIP Reduce data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <param name="factor">The compression factor, 1-4 (method 2-5).</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="InvalidDataException">The data is not a valid Reduce stream of that size.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> compressed, int originalSize, int factor) {
    ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(factor, 4);
    ArgumentOutOfRangeException.ThrowIfNegative(originalSize);

    var reader = new ZipLegacyBitReader(compressed);
    var sets = ReadFollowerSets(ref reader);
    var lengthBits = 8 - factor;
    var dst = new byte[originalSize];
    var pos = 0;
    byte current = 0; // the first predecessor is implicitly zero

    while (pos < originalSize) {
      current = NextByte(ref reader, current, sets);
      if (current != Dle) {
        dst[pos++] = current;
        continue;
      }

      current = NextByte(ref reader, current, sets);
      if (current == 0) {
        dst[pos++] = Dle;
        continue;
      }

      var v = current;
      var length = v & ((1 << lengthBits) - 1);
      if (length == (1 << lengthBits) - 1) {
        current = NextByte(ref reader, current, sets);
        length += current;
      }

      length += 3;
      current = NextByte(ref reader, current, sets);
      var distance = (v >> lengthBits) * 256 + current + 1;
      if (length > originalSize - pos)
        throw new InvalidDataException("Reduce back-reference runs past the expected size.");

      for (var i = 0; i < length; ++i, ++pos)
        dst[pos] = distance > pos ? (byte)0 : dst[pos - distance];
    }

    return dst;
  }

  private static byte[][] ReadFollowerSets(ref ZipLegacyBitReader reader) {
    var sets = new byte[256][];
    for (var i = 255; i >= 0; --i) {
      var n = (int)(reader.Bits() & 0x3F);
      if (n > 32 || !reader.Advance(6))
        throw new InvalidDataException("Invalid Reduce follower set.");

      var set = new byte[n];
      for (var j = 0; j < n; ++j) {
        set[j] = (byte)reader.Bits();
        if (!reader.Advance(8))
          throw new InvalidDataException("Reduce follower sets cut off.");
      }

      sets[i] = set;
    }

    return sets;
  }

  private static byte NextByte(ref ZipLegacyBitReader reader, byte previous, byte[][] sets) {
    var bits = reader.Bits();
    var set = sets[previous];
    if (set.Length == 0) {
      if (!reader.Advance(8))
        throw new InvalidDataException("Reduce data ended early.");

      return (byte)bits;
    }

    if ((bits & 1) == 1) {
      if (!reader.Advance(9))
        throw new InvalidDataException("Reduce data ended early.");

      return (byte)(bits >> 1);
    }

    var indexBits = IndexBits(set.Length);
    var index = (int)ZipLegacyBitReader.Low(bits >> 1, indexBits);
    if (index >= set.Length)
      throw new InvalidDataException("Reduce follower index out of range.");
    if (!reader.Advance(1 + indexBits))
      throw new InvalidDataException("Reduce data ended early.");

    return set[index];
  }

  /// <summary>Bits of a follower index into a set of <paramref name="n"/> bytes.</summary>
  internal static int IndexBits(int n) => n switch {
    > 16 => 5,
    > 8 => 4,
    > 4 => 3,
    > 2 => 2,
    > 0 => 1,
    _ => 0,
  };
}
