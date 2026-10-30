using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Most-significant-bit-first reader over one file's packed data, as RAR 1.5 and 2.0 read it.
/// Reads past the end yield zero bits, so a decoder can look ahead freely; reading more than a
/// few bytes past the end means the stream is corrupt.
/// </summary>
internal sealed class RarLegacyBitReader(byte[] data) {
  private const int OverrunSlackBytes = 8;
  private readonly long _limitBits = ((long)data.Length + RarLegacyBitReader.OverrunSlackBytes) * 8;
  private long _bitPos;

  /// <summary>The next <paramref name="count"/> bits (1..24), without consuming them.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public uint Peek(int count) {
    var i = (int)(this._bitPos >> 3);
    var window = (uint)(this.ByteAt(i) << 24 | this.ByteAt(i + 1) << 16 | this.ByteAt(i + 2) << 8 | this.ByteAt(i + 3));
    return (window << (int)(this._bitPos & 7)) >> (32 - count);
  }

  /// <summary>Consumes <paramref name="count"/> bits.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void Skip(int count) {
    this._bitPos += count;
    if (this._bitPos > this._limitBits)
      throw new InvalidDataException("RAR packed data ended before the file was complete.");
  }

  /// <summary>Reads <paramref name="count"/> bits (0..24) as an unsigned number, first bit highest.</summary>
  public int ReadBits(int count) {
    if (count == 0)
      return 0;

    var value = (int)this.Peek(count);
    this.Skip(count);
    return value;
  }

  /// <summary>Reads one bit.</summary>
  public bool ReadBit() => this.ReadBits(1) != 0;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private int ByteAt(int index) => (uint)index < (uint)data.Length ? data[index] : 0;
}

/// <summary>
/// A prefix code read first bit highest, decoded through one lookup table indexed by the next
/// <c>MaxLength</c> bits. Built either canonically from code lengths (shortest code all zeros,
/// symbols of equal length in ascending order) or from explicit codes.
/// </summary>
internal sealed class RarLegacyPrefixCode {
  private const int LengthBits = 5;
  private const int LengthMask = (1 << RarLegacyPrefixCode.LengthBits) - 1;
  private readonly int[] _table;
  private readonly int _maxLength;

  private RarLegacyPrefixCode(int maxLength) {
    this._maxLength = maxLength;
    this._table = new int[1 << maxLength];
    this._table.AsSpan().Fill(-1);
  }

  /// <summary>Builds the canonical code for <paramref name="lengths"/> (0 = symbol unused).</summary>
  public static RarLegacyPrefixCode FromLengths(ReadOnlySpan<int> lengths) {
    var maxLength = 1;
    foreach (var length in lengths)
      if (length > maxLength)
        maxLength = length;

    var result = new RarLegacyPrefixCode(maxLength);
    var code = 0;
    for (var length = 1; length <= maxLength; ++length) {
      for (var symbol = 0; symbol < lengths.Length; ++symbol) {
        if (lengths[symbol] != length)
          continue;

        if (code >= 1 << length)
          throw new InvalidDataException("RAR Huffman code lengths are over-subscribed.");

        result.Add(symbol, code, length);
        ++code;
      }

      code <<= 1;
    }

    return result;
  }

  /// <summary>Builds a code from explicit (symbol, code, length) triples.</summary>
  public static RarLegacyPrefixCode FromCodes(int maxLength, ReadOnlySpan<(int Symbol, int Code, int Length)> codes) {
    var result = new RarLegacyPrefixCode(maxLength);
    foreach (var (symbol, code, length) in codes)
      result.Add(symbol, code, length);

    return result;
  }

  /// <summary>Decodes one symbol from <paramref name="reader"/>.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int Decode(RarLegacyBitReader reader) {
    var entry = this._table[reader.Peek(this._maxLength)];
    if (entry < 0)
      throw new InvalidDataException("Invalid Huffman code in RAR packed data.");

    reader.Skip(entry & RarLegacyPrefixCode.LengthMask);
    return entry >> RarLegacyPrefixCode.LengthBits;
  }

  private void Add(int symbol, int code, int length) {
    var shift = this._maxLength - length;
    var first = code << shift;
    var entry = symbol << RarLegacyPrefixCode.LengthBits | length;
    for (var i = 0; i < 1 << shift; ++i) {
      if (this._table[first + i] >= 0)
        throw new InvalidDataException("RAR Huffman codes overlap.");

      this._table[first + i] = entry;
    }
  }
}
