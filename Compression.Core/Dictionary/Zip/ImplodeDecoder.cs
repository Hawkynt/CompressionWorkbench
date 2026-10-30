namespace Compression.Core.Dictionary.Zip;

/// <summary>
/// Decodes ZIP Implode (method 6): LZ77 over a 4 or 8 KB window with Shannon-Fano coded literals
/// (optional), lengths and the high distance bits.
/// </summary>
/// <remarks>
/// Converted from hwexplode in hwzip 2.4's implode.c (Hans Wennborg, public domain), see
/// THIRD-PARTY-NOTICE.hwzip.txt. Each tree arrives as run-length coded code lengths; the code is the
/// canonical code for those lengths with every bit inverted, sent first bit first. A back-reference
/// that reaches before the start of the data yields zero bytes, as PKZIP's decoder does.
/// </remarks>
public static class ImplodeDecoder {
  /// <summary>
  /// Decompresses ZIP Implode data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <param name="hasLiteralTree">General-purpose bit 2: literals are Shannon-Fano coded.</param>
  /// <param name="is8kDictionary">General-purpose bit 1: the window is 8 KB rather than 4 KB.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decode(byte[] compressed, int originalSize, bool hasLiteralTree, bool is8kDictionary)
    => Decode(compressed.AsSpan(), originalSize, hasLiteralTree, is8kDictionary);

  /// <summary>
  /// Decompresses ZIP Implode data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <param name="hasLiteralTree">General-purpose bit 2: literals are Shannon-Fano coded.</param>
  /// <param name="is8kDictionary">General-purpose bit 1: the window is 8 KB rather than 4 KB.</param>
  /// <returns>The decompressed data.</returns>
  /// <exception cref="InvalidDataException">The data is not a valid Implode stream of that size.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> compressed, int originalSize, bool hasLiteralTree, bool is8kDictionary) {
    ArgumentOutOfRangeException.ThrowIfNegative(originalSize);
    var dst = new byte[originalSize];
    var reader = new ZipLegacyBitReader(compressed);

    var literalCode = hasLiteralTree ? ReadTree(ref reader, 256) : null;
    var lengthCode = ReadTree(ref reader, 64);
    var distanceCode = ReadTree(ref reader, 64);

    var minLength = hasLiteralTree ? 3 : 2;
    var lowDistanceBits = is8kDictionary ? 7 : 6;
    var pos = 0;
    while (pos < originalSize) {
      var bits = reader.Bits();
      if ((bits & 1) == 1) {
        bits >>= 1;
        int literal, used;
        if (literalCode != null) {
          literal = literalCode.Decode(bits, out used);
        } else {
          literal = (int)(bits & 0xFF);
          used = 8;
        }

        if (!reader.Advance(1 + used))
          throw new InvalidDataException("Implode data ended early.");

        dst[pos++] = (byte)literal;
        continue;
      }

      var usedTotal = 1;
      bits >>= 1;
      var distance = (int)ZipLegacyBitReader.Low(bits, lowDistanceBits);
      bits >>= lowDistanceBits;
      usedTotal += lowDistanceBits;

      var high = distanceCode.Decode(bits, out var usedDistance);
      bits >>= usedDistance;
      usedTotal += usedDistance;
      distance = (distance | high << lowDistanceBits) + 1;

      var lengthSymbol = lengthCode.Decode(bits, out var usedLength);
      bits >>= usedLength;
      usedTotal += usedLength;
      var length = lengthSymbol + minLength;
      if (lengthSymbol == 63) {
        length += (int)(bits & 0xFF);
        usedTotal += 8;
      }

      if (!reader.Advance(usedTotal))
        throw new InvalidDataException("Implode data ended early.");
      if (length > originalSize - pos)
        throw new InvalidDataException("Implode back-reference runs past the expected size.");

      for (var i = 0; i < length; ++i, ++pos)
        dst[pos] = distance > pos ? (byte)0 : dst[pos - distance];
    }

    return dst;
  }

  private static ImplodeCode ReadTree(ref ZipLegacyBitReader reader, int symbols) {
    var byteCount = (int)(reader.Bits() & 0xFF) + 1;
    if (!reader.Advance(8))
      throw new InvalidDataException("Implode tree cut off.");

    var lengths = new int[symbols];
    var at = 0;
    for (var i = 0; i < byteCount; ++i) {
      var value = (int)(reader.Bits() & 0xFF);
      if (!reader.Advance(8))
        throw new InvalidDataException("Implode tree cut off.");

      var length = (value & 0xF) + 1;
      var run = (value >> 4) + 1;
      if (at + run > symbols)
        throw new InvalidDataException("Implode tree lists too many code lengths.");

      for (var r = 0; r < run; ++r)
        lengths[at++] = length;
    }

    if (at < symbols)
      throw new InvalidDataException("Implode tree lists too few code lengths.");

    return new(lengths);
  }

  /// <summary>
  /// A complete prefix code over 1- to 16-bit lengths, canonical in the Deflate sense but with every
  /// bit inverted, read first bit first from a least-significant-bit-first stream.
  /// </summary>
  internal sealed class ImplodeCode {
    private const int MaxBits = 16;
    private readonly int[] _firstCode = new int[MaxBits + 2];
    private readonly int[] _count = new int[MaxBits + 1];
    private readonly int[] _offset = new int[MaxBits + 1];
    private readonly int[] _symbols;

    public ImplodeCode(int[] lengths) {
      foreach (var length in lengths)
        ++this._count[length];

      // PKZIP only accepts a full tree.
      var available = 1L;
      for (var length = 1; length <= MaxBits; ++length) {
        available = available * 2 - this._count[length];
        if (available < 0)
          throw new InvalidDataException("Implode tree is over-subscribed.");
      }

      if (available != 0)
        throw new InvalidDataException("Implode tree is not complete.");

      var code = 0;
      var index = 0;
      for (var length = 1; length <= MaxBits; ++length) {
        code = (code + this._count[length - 1]) << 1;
        this._firstCode[length] = code;
        this._offset[length] = index;
        index += this._count[length];
      }

      this._symbols = new int[lengths.Length];
      var next = (int[])this._offset.Clone();
      for (var symbol = 0; symbol < lengths.Length; ++symbol)
        this._symbols[next[lengths[symbol]]++] = symbol;
    }

    /// <summary>Decodes the symbol at the start of <paramref name="bits"/>.</summary>
    public int Decode(ulong bits, out int used) {
      var code = 0;
      for (var length = 1; length <= MaxBits; ++length) {
        code = code << 1 | (int)(~bits & 1);
        bits >>= 1;
        var index = code - this._firstCode[length];
        if (index >= 0 && index < this._count[length]) {
          used = length;
          return this._symbols[this._offset[length] + index];
        }
      }

      throw new InvalidDataException("Invalid Implode code.");
    }
  }
}
