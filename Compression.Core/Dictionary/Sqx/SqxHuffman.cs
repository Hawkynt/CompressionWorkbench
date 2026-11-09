namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// An SQX canonical Huffman table: code lengths of up to 15 bits and a flat lookup table indexed
/// by the next <see cref="IndexBits"/> stream bits. Codes are assigned in ascending length, then
/// ascending symbol order.
/// </summary>
internal sealed class SqxHuffman(int symbols, int indexBits) {
  public int Symbols { get; set; } = symbols;
  public int IndexBits { get; } = indexBits;
  public byte[] Lengths { get; } = new byte[symbols];
  private readonly ushort[] _lookup = new ushort[1 << indexBits];

  /// <summary>Builds the lookup table; false when the lengths do not form a complete code.</summary>
  public bool Build() {
    Span<uint> lengthCount = stackalloc uint[17];
    Span<uint> next = stackalloc uint[18];
    var mb = this.IndexBits;
    var tableSize = 1u << mb;
    for (var i = 0; i < this.Symbols; ++i) {
      if (this.Lengths[i] > 16)
        return false;
      ++lengthCount[this.Lengths[i]];
    }

    for (var c = 1; c <= mb; ++c)
      next[c + 1] = next[c] + (lengthCount[c] << (mb - c));
    if (next[mb + 1] != tableSize)
      return false;

    Array.Clear(this._lookup);
    for (var symbol = 0; symbol < this.Symbols; ++symbol) {
      var length = this.Lengths[symbol];
      // A length beyond the index width owns no table entries (and was left out of the sum above).
      if (length == 0 || length > mb)
        continue;
      var end = next[length] + (1u << (mb - length));
      if (end > tableSize)
        return false;
      for (var i = next[length]; i < end; ++i)
        this._lookup[i] = (ushort)symbol;
      next[length] = end;
    }

    return true;
  }

  public int Decode(SqxBitReader reader) {
    var symbol = this._lookup[reader.Peek(this.IndexBits)];
    reader.Skip(this.Lengths[symbol]);
    return symbol;
  }

  /// <summary>Reads lengths in the "flat" form: 4-bit first length, then delta tokens.</summary>
  public bool ReadFlat(SqxBitReader reader) {
    var value = (int)reader.Read(4);
    this.Lengths[0] = (byte)value;
    for (var count = 1; count < this.Symbols;) {
      if (reader.Read(1) == 0) {
        this.Lengths[count++] = (byte)value;
        continue;
      }

      if (reader.Read(1) == 0) {
        this.Lengths[count++] = (byte)++value;
        continue;
      }

      if (reader.Read(1) == 1) {
        value = (int)reader.Read(4);
        this.Lengths[count++] = (byte)value;
        continue;
      }

      value = reader.Read(1) == 0 ? value - 1 : value + 2;
      this.Lengths[count++] = (byte)value;
    }

    return this.Build();
  }

  /// <summary>Reads lengths coded with the 19-symbol meta table (16: repeat previous, 17/18: zero runs).</summary>
  public bool ReadTree(SqxBitReader reader, SqxHuffman meta) {
    for (var count = 0; count < this.Symbols;) {
      var code = meta.Decode(reader);
      if (code < 16) {
        this.Lengths[count++] = (byte)code;
      } else if (code == 16) {
        if (count == 0)
          return false;
        var repeat = 3 + (int)reader.Read(2);
        for (; count < this.Symbols && repeat-- > 0; ++count)
          this.Lengths[count] = this.Lengths[count - 1];
      } else {
        var repeat = code == 17 ? 3 + (int)reader.Read(3) : 11 + (int)reader.Read(7);
        for (; count < this.Symbols && repeat-- > 0; ++count)
          this.Lengths[count] = 0;
      }
    }

    return this.Build();
  }

  /// <summary>Canonical codewords for <paramref name="lengths"/>, or null when they are not a complete code.</summary>
  public static ushort[]? Codes(ReadOnlySpan<byte> lengths, int mb) {
    Span<uint> lengthCount = stackalloc uint[18];
    Span<uint> next = stackalloc uint[19];
    foreach (var length in lengths) {
      if (length > mb)
        return null;
      ++lengthCount[length];
    }

    for (var c = 1; c <= mb; ++c)
      next[c + 1] = next[c] + (lengthCount[c] << (mb - c));
    if (next[mb + 1] != 1u << mb)
      return null;

    var codes = new ushort[lengths.Length];
    for (var symbol = 0; symbol < lengths.Length; ++symbol) {
      var length = lengths[symbol];
      if (length == 0)
        continue;
      codes[symbol] = (ushort)(next[length] >> (mb - length));
      next[length] += 1u << (mb - length);
    }

    return codes;
  }
}
