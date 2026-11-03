namespace Compression.Core.Dictionary.PkwareDcl;

/// <summary>Literal coding of a PKWARE DCL stream, from the first header byte.</summary>
public enum PkwareDclMode : byte {
  /// <summary>Literals are stored as plain 8-bit values.</summary>
  Binary = 0,
  /// <summary>Literals use the fixed Huffman-style ASCII code table.</summary>
  Ascii = 1,
}

/// <summary>
/// Decodes the PKWARE Data Compression Library "implode" format ("explode"), as used by MPQ
/// archives (method 0x08 and imploded files), InstallShield 3 and many DOS-era programs.
/// </summary>
/// <remarks>
/// <para>Stream layout: byte 0 is the literal mode (<see cref="PkwareDclMode"/>), byte 1 the
/// dictionary size in bits (4, 5 or 6 for 1, 2 or 4 KiB), then an LSB-first bit stream of tokens.
/// A token starting with a 0 bit is a literal; one starting with a 1 bit is a length code, its
/// extra bits, a distance-position code and its low distance bits. The length value 0x205 ends
/// the stream.</para>
/// <para>The format and its code tables follow StormLib's <c>pklib/explode.c</c> (Ladislav Zezula,
/// MIT), converted into this decoder; see <c>THIRD-PARTY-NOTICE.StormLib.txt</c>. Unlike the C
/// original it refuses a stream that ends before its end marker and a distance reaching before
/// the start of the output, instead of producing bytes it cannot account for.</para>
/// </remarks>
public static class PkwareDclDecoder {

  private const int LiteralTableBits = 13;

  private static readonly byte[] LengthCodeLookup = BuildLookup(PkwareDclTables.LenCode, PkwareDclTables.LenBits);
  private static readonly byte[] DistanceCodeLookup = BuildLookup(PkwareDclTables.DistCode, PkwareDclTables.DistBits);
  private static readonly ushort[] AsciiLookup = BuildAsciiLookup();

  /// <summary>Decodes a complete stream up to its end marker.</summary>
  /// <param name="input">The compressed stream, starting with the two header bytes.</param>
  /// <returns>The decoded bytes.</returns>
  /// <exception cref="InvalidDataException">The header is invalid, the stream is truncated or a distance is out of range.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> input) => Decompress(input, -1);

  /// <summary>Decodes a complete stream and checks that it yields exactly <paramref name="expectedSize"/> bytes.</summary>
  /// <param name="input">The compressed stream, starting with the two header bytes.</param>
  /// <param name="expectedSize">The size the stream must decode to, or -1 for no check.</param>
  /// <returns>The decoded bytes.</returns>
  /// <exception cref="InvalidDataException">The stream is malformed or decodes to a different size.</exception>
  public static byte[] Decompress(ReadOnlySpan<byte> input, int expectedSize) {
    if (input.Length < 3)
      throw new InvalidDataException("PKWARE DCL stream is shorter than its header.");

    var mode = input[0] switch {
      0 => PkwareDclMode.Binary,
      1 => PkwareDclMode.Ascii,
      var m => throw new InvalidDataException($"PKWARE DCL literal mode {m} is not 0 (binary) or 1 (ASCII)."),
    };
    int dictionaryBits = input[1];
    if (dictionaryBits is < 4 or > 6)
      throw new InvalidDataException($"PKWARE DCL dictionary size {dictionaryBits} is not 4, 5 or 6 bits.");
    var dictionaryMask = (1 << dictionaryBits) - 1;

    var bits = new BitReader(input[2..]);
    var output = new List<byte>(expectedSize > 0 ? expectedSize : input.Length * 4);
    var limit = expectedSize >= 0 ? expectedSize : int.MaxValue;

    while (true) {
      if (bits.Read(1) == 1) {
        var lengthCode = LengthCodeLookup[bits.Peek(8)];
        bits.Skip(PkwareDclTables.LenBits[lengthCode]);
        int length = PkwareDclTables.LenBase[lengthCode];
        int extraBits = PkwareDclTables.ExLenBits[lengthCode];
        if (extraBits != 0)
          length += bits.Read(extraBits);
        bits.ThrowIfOverrun();

        if (length == PkwareDclTables.EndOfStreamLength)
          break;

        var repLength = length + 2;
        var distanceCode = DistanceCodeLookup[bits.Peek(8)];
        bits.Skip(PkwareDclTables.DistBits[distanceCode]);
        var distance = repLength == 2
          ? (distanceCode << 2) | bits.Read(2)
          : (distanceCode << dictionaryBits) | (bits.Read(dictionaryBits) & dictionaryMask);
        ++distance;
        bits.ThrowIfOverrun();

        if (distance > output.Count)
          throw new InvalidDataException($"PKWARE DCL distance {distance} reaches before the start of the output ({output.Count} bytes).");
        if (output.Count + repLength > limit)
          throw new InvalidDataException($"PKWARE DCL stream decodes past its expected {expectedSize} bytes.");
        var source = output.Count - distance;
        for (var i = 0; i < repLength; ++i)
          output.Add(output[source + i]);
        continue;
      }

      int literal;
      if (mode == PkwareDclMode.Binary)
        literal = bits.Read(8);
      else {
        var entry = AsciiLookup[bits.Peek(LiteralTableBits)];
        literal = entry & 0xFF;
        bits.Skip(entry >> 8);
      }
      bits.ThrowIfOverrun();
      if (output.Count >= limit)
        throw new InvalidDataException($"PKWARE DCL stream decodes past its expected {expectedSize} bytes.");
      output.Add((byte)literal);
    }

    if (expectedSize >= 0 && output.Count != expectedSize)
      throw new InvalidDataException($"PKWARE DCL stream decoded to {output.Count} bytes; {expectedSize} were expected.");
    return [.. output];
  }

  /// <summary>Builds a 256-entry table mapping the next 8 stream bits to the code they start with.</summary>
  private static byte[] BuildLookup(ReadOnlySpan<byte> codes, ReadOnlySpan<byte> lengths) {
    var table = new byte[256];
    for (var symbol = 0; symbol < codes.Length; ++symbol)
      for (var index = (int)codes[symbol]; index < 256; index += 1 << lengths[symbol])
        table[index] = (byte)symbol;
    return table;
  }

  /// <summary>Builds a 2^13-entry table: low byte = literal, high byte = code length.</summary>
  private static ushort[] BuildAsciiLookup() {
    var table = new ushort[1 << LiteralTableBits];
    for (var symbol = 0; symbol < 256; ++symbol) {
      var length = PkwareDclTables.ChBitsAsc[symbol];
      for (var index = (int)PkwareDclTables.ChCodeAsc[symbol]; index < table.Length; index += 1 << length)
        table[index] = (ushort)(symbol | (length << 8));
    }
    return table;
  }

  /// <summary>LSB-first bit reader that reads zeros past the end and reports the overrun afterwards.</summary>
  private ref struct BitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private long _position;

    public readonly int Peek(int count) {
      var value = 0;
      var byteIndex = (int)(this._position >> 3);
      var shift = (int)(this._position & 7);
      for (var i = 0; i * 8 - shift < count && i < 4; ++i) {
        var b = byteIndex + i < this._data.Length ? this._data[byteIndex + i] : 0;
        value |= b << (i * 8);
      }
      return (value >> shift) & ((1 << count) - 1);
    }

    public void Skip(int count) => this._position += count;

    public int Read(int count) {
      var value = this.Peek(count);
      this._position += count;
      return value;
    }

    public readonly void ThrowIfOverrun() {
      if (this._position > (long)this._data.Length * 8)
        throw new InvalidDataException("PKWARE DCL stream ends before its end marker.");
    }
  }
}
