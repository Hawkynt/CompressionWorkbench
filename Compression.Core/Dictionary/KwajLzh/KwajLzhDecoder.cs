namespace Compression.Core.Dictionary.KwajLzh;

/// <summary>KWAJ LZH decoder converted from MIT-licensed kwaj-lzh; see the adjacent notice.</summary>
internal static class KwajLzhDecoder {
  private const int WindowSize = 4096;
  private const int WindowMask = WindowSize - 1;
  private const int TableBits = 9;

  private sealed class PaddingConsumedException : Exception;

  private sealed record DecodeTable(ushort[] Entries, byte[] Lengths);

  private ref struct BitReader(ReadOnlySpan<byte> source) {
    private readonly ReadOnlySpan<byte> _source = source;
    private int _position;
    private uint _buffer;
    private int _available;
    private int _padding;

    public readonly bool HasPadding => this._padding != 0;

    private void Ensure(int count) {
      while (this._available < count) {
        var value = this._position < this._source.Length ? this._source[this._position++] : this.Pad();
        this._buffer |= (uint)value << (24 - this._available);
        this._available += 8;
      }
    }

    private byte Pad() {
      this._padding += 8;
      return 0;
    }

    private void Consume(int count, bool guarded) {
      this._buffer <<= count;
      this._available -= count;
      if (guarded && this._padding != 0 && this._available < this._padding)
        throw new PaddingConsumedException();
    }

    public int ReadBits(int count, bool guarded = true) {
      this.Ensure(count);
      var result = (int)(this._buffer >> (32 - count));
      this.Consume(count, guarded);
      return result;
    }

    public int ReadSymbol(DecodeTable table) {
      this.Ensure(16);
      var symbol = (int)table.Entries[this._buffer >> (32 - TableBits)];
      var mask = 1U << (32 - TableBits);
      while (symbol >= table.Lengths.Length) {
        mask >>= 1;
        var index = (symbol << 1) | ((this._buffer & mask) == 0 ? 0 : 1);
        if (mask == 0 || (uint)index >= table.Entries.Length)
          throw new InvalidDataException("KWAJ LZH: invalid Huffman code.");
        symbol = table.Entries[index];
      }
      var length = table.Lengths[symbol];
      if (length == 0)
        throw new InvalidDataException("KWAJ LZH: empty Huffman code.");
      this.Consume(length, guarded: true);
      return symbol;
    }
  }

  private static DecodeTable ReadTable(ref BitReader reader, int encoding, int symbolCount) {
    var lengths = new byte[symbolCount];
    switch (encoding) {
      case 0:
        lengths.AsSpan().Fill((byte)(symbolCount switch { 16 => 4, 32 => 5, 64 => 6, 256 => 8, _ => 0 }));
        break;
      case 1: {
        var length = reader.ReadBits(4, guarded: false);
        lengths[0] = (byte)length;
        for (var i = 1; i < symbolCount; ++i) {
          if (reader.ReadBits(1, guarded: false) != 0)
            length = reader.ReadBits(1, guarded: false) == 0 ? length + 1 : reader.ReadBits(4, guarded: false);
          lengths[i] = (byte)length;
        }
        break;
      }
      case 2: {
        var length = reader.ReadBits(4, guarded: false);
        lengths[0] = (byte)length;
        for (var i = 1; i < symbolCount; ++i) {
          var selector = reader.ReadBits(2, guarded: false);
          length = selector == 3 ? reader.ReadBits(4, guarded: false) : (length + selector - 1) & 0xFF;
          lengths[i] = (byte)length;
        }
        break;
      }
      case 3:
        for (var i = 0; i < symbolCount; ++i)
          lengths[i] = (byte)reader.ReadBits(4, guarded: false);
        break;
      default:
        throw new InvalidDataException($"KWAJ LZH: unknown table encoding {encoding}.");
    }
    return BuildTable(lengths);
  }

  private static DecodeTable BuildTable(byte[] lengths) {
    var coverage = 0;
    foreach (var length in lengths) {
      if (length > 16)
        throw new InvalidDataException("KWAJ LZH: invalid Huffman code length.");
      if (length != 0)
        coverage += 1 << (16 - length);
    }
    if (coverage != 1 << 16)
      throw new InvalidDataException("KWAJ LZH: incomplete or over-subscribed Huffman table.");
    const int directSize = 1 << TableBits;
    var entries = new ushort[directSize + lengths.Length * 2];
    Array.Fill(entries, ushort.MaxValue);
    var position = 0;
    var width = directSize >> 1;
    for (var bits = 1; bits <= TableBits; ++bits) {
      for (var symbol = 0; symbol < lengths.Length; ++symbol) {
        if (lengths[symbol] != bits)
          continue;
        if (position + width > directSize)
          throw new InvalidDataException("KWAJ LZH: over-subscribed Huffman table.");
        entries.AsSpan(position, width).Fill((ushort)symbol);
        position += width;
      }
      width >>= 1;
    }

    if (position != directSize) {
      var nextNode = Math.Max(directSize >> 1, lengths.Length);
      position <<= 16;
      const int limit = directSize << 16;
      width = 1 << 15;
      for (var bits = TableBits + 1; bits <= 16; ++bits) {
        for (var symbol = 0; symbol < lengths.Length; ++symbol) {
          if (lengths[symbol] != bits)
            continue;
          if (position >= limit)
            throw new InvalidDataException("KWAJ LZH: over-subscribed Huffman table.");
          var leaf = position >> 16;
          for (var depth = 0; depth < bits - TableBits; ++depth) {
            if ((uint)leaf >= entries.Length)
              throw new InvalidDataException("KWAJ LZH: invalid Huffman tree.");
            if (entries[leaf] == ushort.MaxValue) {
              if (nextNode * 2 + 1 >= entries.Length)
                throw new InvalidDataException("KWAJ LZH: Huffman tree exceeds its table.");
              entries[leaf] = (ushort)nextNode++;
            }
            leaf = (entries[leaf] << 1) | ((position >> (15 - depth)) & 1);
          }
          if ((uint)leaf >= entries.Length)
            throw new InvalidDataException("KWAJ LZH: invalid Huffman tree.");
          entries[leaf] = (ushort)symbol;
          position += width;
        }
        width >>= 1;
      }
      if (position != limit)
        throw new InvalidDataException("KWAJ LZH: incomplete Huffman table.");
    }
    return new(entries, lengths);
  }

  public static byte[] Decode(ReadOnlySpan<byte> source) {
    var reader = new BitReader(source);
    Span<byte> window = stackalloc byte[WindowSize];
    window.Fill(0x20);
    var cursor = 0;
    using var output = new MemoryStream();
    try {
      Span<int> encodings = stackalloc int[6];
      for (var i = 0; i < encodings.Length; ++i)
        encodings[i] = reader.ReadBits(4);
      var initialMatch = ReadTable(ref reader, encodings[0], 16);
      var followingMatch = ReadTable(ref reader, encodings[1], 16);
      var literalCount = ReadTable(ref reader, encodings[2], 32);
      var distances = ReadTable(ref reader, encodings[3], 64);
      var literals = ReadTable(ref reader, encodings[4], 256);
      var afterShortRun = false;
      while (!reader.HasPadding) {
        var count = reader.ReadSymbol(afterShortRun ? followingMatch : initialMatch);
        if (count != 0) {
          count += 2;
          afterShortRun = false;
          var distance = (reader.ReadSymbol(distances) << 6) | reader.ReadBits(6);
          while (count-- > 0) {
            var value = window[(cursor + WindowSize - distance) & WindowMask];
            window[cursor] = value;
            output.WriteByte(value);
            cursor = (cursor + 1) & WindowMask;
          }
        } else {
          count = reader.ReadSymbol(literalCount) + 1;
          afterShortRun = count != 32;
          while (count-- > 0) {
            var value = (byte)reader.ReadSymbol(literals);
            window[cursor] = value;
            output.WriteByte(value);
            cursor = (cursor + 1) & WindowMask;
          }
        }
      }
    } catch (PaddingConsumedException) {
      // The padding completes lookahead but never contributes output bytes.
    }
    return output.ToArray();
  }
}
