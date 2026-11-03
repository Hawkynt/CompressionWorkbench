using Compression.Core.Deflate;

namespace FileFormat.InstallerVise;

/// <summary>
/// VISE member compression: every byte through the substitution permutation, then DEFLATE stored
/// as big-endian 16-bit words with stored blocks aligned to 16 bits. The stream is rewritten bit
/// for bit into standard raw DEFLATE (Huffman symbols are parsed only to find block ends) and
/// decoded with this repository's DEFLATE decoder.
/// </summary>
/// <remarks>Converted from unvise's <c>inflate.c</c> (MIT); see THIRD-PARTY-NOTICE.unvise.txt.</remarks>
internal static class ViseDeflate {
  private static readonly byte[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
  private static readonly byte[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
  private static readonly byte[] CodeOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

  private sealed class Rewriter(byte[] input) {
    private long _inBit;
    private readonly List<byte> _out = new(input.Length + 64);
    private int _outBit;

    public byte[] Output => [.. this._out];
    public bool InWordAligned => (this._inBit & 15) == 0;
    public bool OutByteAligned => this._outBit == 0;

    public uint ViseBit() {
      var word = this._inBit / 16 * 2;
      var shift = (int)(this._inBit % 16);
      if (word + 2 > input.Length)
        throw new InvalidDataException("VISE: truncated DEFLATE bitstream.");
      var value = ((uint)input[word] << 8) | input[word + 1];
      ++this._inBit;
      return (value >> shift) & 1;
    }

    public void DeflateBit(uint value) {
      if (this._outBit == 0)
        this._out.Add(0);
      this._out[^1] |= (byte)((value & 1) << this._outBit);
      this._outBit = (this._outBit + 1) & 7;
    }

    public uint CopyBits(int count) {
      uint value = 0;
      for (var i = 0; i < count; ++i) {
        var bit = this.ViseBit();
        this.DeflateBit(bit);
        value |= bit << i;
      }
      return value;
    }

    public uint ReadBits(int count) {
      uint value = 0;
      for (var i = 0; i < count; ++i)
        value |= this.ViseBit() << i;
      return value;
    }

    public void WriteBits(uint value, int count) {
      for (var i = 0; i < count; ++i)
        this.DeflateBit(value >> i);
    }
  }

  private sealed class Huffman {
    public readonly uint[] Count = new uint[16];
    public readonly uint[] Symbol = new uint[288];

    public Huffman(ReadOnlySpan<byte> lengths) {
      foreach (var l in lengths) {
        if (l > 15)
          throw new InvalidDataException("VISE: invalid Huffman code length.");
        ++this.Count[l];
      }
      Span<uint> offsets = stackalloc uint[16];
      for (var length = 1; length < 15; ++length)
        offsets[length + 1] = offsets[length] + this.Count[length];
      for (var symbol = 0; symbol < lengths.Length; ++symbol)
        if (lengths[symbol] != 0)
          this.Symbol[offsets[lengths[symbol]]++] = (uint)symbol;
    }
  }

  private static uint Decode(Rewriter r, Huffman h) {
    uint code = 0, first = 0, index = 0;
    for (var length = 1; length <= 15; ++length) {
      code |= r.CopyBits(1);
      var count = h.Count[length];
      if (code < first + count)
        return h.Symbol[index + code - first];
      index += count;
      first = (first + count) << 1;
      code <<= 1;
    }
    throw new InvalidDataException("VISE: invalid Huffman code.");
  }

  private static byte[] Standardize(byte[] transformed) {
    var r = new Rewriter(transformed);
    bool final;
    do {
      final = r.CopyBits(1) != 0;
      var type = r.CopyBits(2);
      if (type == 0) {
        while (!r.InWordAligned)
          r.ViseBit();
        while (!r.OutByteAligned)
          r.DeflateBit(0);
        var length = r.ReadBits(16);
        var inverse = r.ReadBits(16);
        if (length != (~inverse & 0xFFFF))
          throw new InvalidDataException("VISE: invalid stored block lengths.");
        r.WriteBits(length, 16);
        r.WriteBits(inverse, 16);
        for (var i = 0u; i < length; ++i)
          r.WriteBits(r.ReadBits(8), 8);
        if ((length & 1) != 0)
          r.ReadBits(8);
      } else if (type is 1 or 2) {
        Huffman literal, distance;
        if (type == 1) {
          var lengths = new byte[288];
          for (var i = 0; i < 288; ++i)
            lengths[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
          literal = new Huffman(lengths);
          distance = new Huffman(Enumerable.Repeat((byte)5, 32).ToArray());
        } else {
          var literalCount = (int)r.CopyBits(5) + 257;
          var distanceCount = (int)r.CopyBits(5) + 1;
          var codeCount = (int)r.CopyBits(4) + 4;
          var codeLengths = new byte[19];
          for (var i = 0; i < codeCount; ++i)
            codeLengths[CodeOrder[i]] = (byte)r.CopyBits(3);
          var code = new Huffman(codeLengths);
          var lengths = new byte[320];
          var total = literalCount + distanceCount;
          var n = 0;
          while (n < total) {
            var symbol = Decode(r, code);
            if (symbol < 16) {
              lengths[n++] = (byte)symbol;
              continue;
            }
            uint repeat;
            byte value = 0;
            if (symbol == 16) {
              if (n == 0)
                throw new InvalidDataException("VISE: Huffman repeat has no previous length.");
              value = lengths[n - 1];
              repeat = r.CopyBits(2) + 3;
            } else if (symbol == 17)
              repeat = r.CopyBits(3) + 3;
            else if (symbol == 18)
              repeat = r.CopyBits(7) + 11;
            else
              throw new InvalidDataException("VISE: invalid Huffman repeat.");
            if (repeat > total - n)
              throw new InvalidDataException("VISE: Huffman repeat overflow.");
            while (repeat-- > 0)
              lengths[n++] = value;
          }
          literal = new Huffman(lengths.AsSpan(0, literalCount));
          distance = new Huffman(lengths.AsSpan(literalCount, distanceCount));
        }
        for (;;) {
          var symbol = Decode(r, literal);
          if (symbol < 256)
            continue;
          if (symbol == 256)
            break;
          if (symbol > 285)
            throw new InvalidDataException("VISE: invalid length symbol.");
          r.CopyBits(LengthExtra[symbol - 257]);
          var dist = Decode(r, distance);
          if (dist >= 30)
            throw new InvalidDataException("VISE: invalid distance symbol.");
          r.CopyBits(DistanceExtra[dist]);
        }
      } else
        throw new InvalidDataException("VISE: invalid DEFLATE block type.");
    } while (!final);
    return r.Output;
  }

  /// <summary>Expands one packed member to exactly <paramref name="expected"/> bytes.</summary>
  public static byte[] InflateMember(ReadOnlySpan<byte> packed, byte[] table, long expected) {
    if (packed.Length < 2)
      throw new InvalidDataException("VISE: truncated member.");
    var x = new byte[packed.Length];
    for (var i = 0; i < packed.Length; ++i)
      x[i] = table[packed[i]];
    var word = (x[0] << 8) | x[1];
    // One host path presents the 16-bit words byte-reversed; a leading stored block shows it,
    // because LEN and NLEN stop being complements until each word is swapped back.
    if (((word >> 1) & 3) == 0 && packed.Length >= 6) {
      var length = (x[2] << 8) | x[3];
      var inverse = (x[4] << 8) | x[5];
      if (length != (~inverse & 0xFFFF))
        for (var i = 0; i + 1 < x.Length; i += 2)
          (x[i], x[i + 1]) = (x[i + 1], x[i]);
    }
    var deflate = Standardize(x);
    if (expected > Array.MaxLength - 1)
      throw new NotSupportedException("VISE: member is too large.");
    var output = new byte[expected];
    var decoder = new DeflateDecompressor(new MemoryStream(deflate));
    var got = 0;
    try {
      while (got < output.Length) {
        var n = decoder.Decompress(output.AsSpan(got));
        if (n == 0)
          break;
        got += n;
      }
      if (got == output.Length && !decoder.IsFinished) {
        Span<byte> extra = stackalloc byte[1];
        if (decoder.Decompress(extra) != 0)
          throw new InvalidDataException("VISE: member expands past its declared size.");
      }
    } catch (Exception e) when (got == output.Length && e is InvalidDataException or EndOfStreamException) {
      // A stream cut off right where the declared size is reached is accepted, as zlib does.
    }
    if (got != output.Length)
      throw new InvalidDataException($"VISE: member expands to {got} bytes instead of {expected}.");
    return output;
  }

  /// <summary>Expands a compressed catalog: word-swapped raw DEFLATE from <c>CVCT+0x64</c>.</summary>
  public static byte[] InflateCatalog(byte[] data, long catalog) {
    if (catalog + 8 > data.Length)
      throw new InvalidDataException("VISE: truncated catalog header.");
    long packedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)catalog + 4));
    if (catalog + 0x64 + packedSize > data.Length)
      throw new InvalidDataException("VISE: packed catalog outside the data fork.");
    var input = data.AsSpan((int)(catalog + 0x64), (int)packedSize).ToArray();
    for (var i = 0; i + 1 < input.Length; i += 2)
      (input[i], input[i + 1]) = (input[i + 1], input[i]);
    return DeflateDecompressor.Decompress(input);
  }
}
