namespace Compression.Core.Dictionary.Arj;

/// <summary>
/// Decodes ARJ method 4 ("fastest"), the LZ77 variant ARJ uses when asked for speed over ratio.
/// </summary>
/// <remarks>
/// <para>There is no Huffman stage. The bit stream is MSB-first, like methods 1-3, and is a sequence of
/// tokens, each opened by a length code:</para>
/// <list type="bullet">
///   <item><description>Length code: up to 7 one-bits, each adding <c>1 &lt;&lt; i</c>; a zero bit stops it early.
///   The number of one-bits <c>w</c> then gives a <c>w</c>-bit literal that is added on. Value 0 means
///   "literal": the next 8 bits are the byte. Any other value <c>n</c> is a match of <c>n + 2</c> bytes.</description></item>
///   <item><description>Distance code (matches only): the same shape with 4 optional one-bits starting at
///   width 9 (adding 512, 1024, 2048, 4096) followed by a <c>9 + w</c>-bit literal. The copy starts
///   <c>distance + 1</c> bytes back.</description></item>
/// </list>
/// <para>Sourcing: converted from the method-4 decoder of The Unarchiver's XADMaster
/// (<c>XADARJFastestHandle.m</c>, LGPL-2.1-or-later), checked against ARJ archives written by the
/// open-source ARJ port (<c>arj a -m4</c>); see THIRD-PARTY-NOTICE.XADMaster.txt beside this file.</para>
/// </remarks>
public static class ArjFastestDecoder {
  private const int MinMatch = 3;
  private const int LengthMaxWidth = 7;
  private const int DistanceStartWidth = 9;
  private const int DistanceStopWidth = 13;

  /// <summary>Decodes <paramref name="originalSize"/> bytes of method-4 data.</summary>
  /// <param name="compressed">The member's compressed bytes (after any degarbling).</param>
  /// <param name="originalSize">The member's uncompressed size from its header.</param>
  /// <returns>The decoded bytes.</returns>
  /// <exception cref="InvalidDataException">A match reaches back before the start of the output.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> compressed, int originalSize) {
    ArgumentOutOfRangeException.ThrowIfNegative(originalSize);
    var output = new byte[originalSize];
    var bits = new MsbBitReader(compressed);
    var pos = 0;

    while (pos < originalSize) {
      var length = 0;
      var width = 0;
      while (width < LengthMaxWidth && bits.ReadBit() != 0) {
        length += 1 << width;
        ++width;
      }
      if (width != 0)
        length += bits.ReadBits(width);

      if (length == 0) {
        output[pos++] = (byte)bits.ReadBits(8);
        continue;
      }

      var count = length + MinMatch - 1;
      var distance = 0;
      width = DistanceStartWidth;
      while (width < DistanceStopWidth && bits.ReadBit() != 0) {
        distance += 1 << width;
        ++width;
      }
      distance += bits.ReadBits(width);

      var src = pos - distance - 1;
      if (src < 0)
        throw new InvalidDataException(
          $"ARJ method 4 match reaches {-src} bytes before the start of the output.");

      // Byte by byte: a match may overlap its own output (distance + 1 < count).
      for (var i = 0; i < count && pos < originalSize; ++i)
        output[pos++] = output[src + i];
    }

    return output;
  }

  /// <summary>MSB-first reader that yields zero bits past the end, as ARJ's own fillbuf does.</summary>
  private ref struct MsbBitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bytePos;
    private int _bitPos;

    public int ReadBit() {
      if (this._bytePos >= this._data.Length)
        return 0;
      var bit = (this._data[this._bytePos] >> (7 - this._bitPos)) & 1;
      if (++this._bitPos == 8) {
        this._bitPos = 0;
        ++this._bytePos;
      }
      return bit;
    }

    public int ReadBits(int count) {
      var value = 0;
      for (var i = 0; i < count; ++i)
        value = (value << 1) | this.ReadBit();
      return value;
    }
  }
}
