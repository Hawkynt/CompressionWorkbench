#pragma warning disable CS1591

namespace Codec.Musepack;

/// <summary>
/// Canonical-Huffman decoder matching FFmpeg's <c>ff_vlc_init_from_lengths</c> as the
/// Musepack SV7 decoder (<c>libavcodec/mpc7.c</c>) builds its books: the symbol/length
/// pairs are supplied in <em>input</em> order (no length sort) and assigned codes via a
/// left-aligned 32-bit running counter (<c>code += 1u &lt;&lt; (32 - len)</c>). The
/// resulting MSB-first prefix codes are consumed bit-by-bit. The per-table symbol bias
/// is the <c>offset</c> argument FFmpeg passes to its VLC initialiser.
/// </summary>
internal sealed class Mpc7Vlc {

  private readonly uint[] _codes;     // left-aligned 32-bit canonical codes
  private readonly byte[] _lengths;
  private readonly int[] _symbols;
  private readonly int _maxLength;

  /// <summary>
  /// Builds a book from the interleaved <c>{ symbol, length, symbol, length, … }</c>
  /// table used by <c>mpc7data.h</c> (one byte each). Codes are assigned in the listed
  /// order using the left-aligned counter, exactly like <c>ff_vlc_init_from_lengths</c>.
  /// </summary>
  public Mpc7Vlc(IReadOnlyList<byte> symbolThenLength, int symbolOffset) {
    var count = symbolThenLength.Count / 2;
    this._codes = new uint[count];
    this._lengths = new byte[count];
    this._symbols = new int[count];

    uint code = 0;
    var maxLen = 0;
    for (var i = 0; i < count; ++i) {
      var sym = (sbyte)symbolThenLength[i * 2];      // symbols are signed in mpc7data.h
      var len = symbolThenLength[i * 2 + 1];
      this._symbols[i] = sym + symbolOffset;
      this._lengths[i] = len;
      this._codes[i] = code;
      code += 1u << (32 - len);
      if (len > maxLen)
        maxLen = len;
    }
    this._maxLength = maxLen;
  }

  /// <summary>Reads one symbol MSB-first from <paramref name="reader"/>.</summary>
  public int Read(MpcBitReader reader) {
    // Accumulate bits into a left-aligned prefix and match against the stored codes
    // whose length equals the number of bits read so far.
    uint prefix = 0;
    for (var len = 1; len <= this._maxLength; ++len) {
      prefix |= (uint)reader.GetBit() << (32 - len);
      for (var i = 0; i < this._codes.Length; ++i)
        if (this._lengths[i] == len && this._codes[i] == prefix)
          return this._symbols[i];
    }
    throw new InvalidDataException("Musepack SV7: invalid VLC code (no matching prefix).");
  }
}
