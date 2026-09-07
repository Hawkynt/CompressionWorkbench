using System.Buffers.Binary;
using System.Numerics;
using Compression.Core.Entropy.Huffman;

namespace FileFormat.Lizard;

/// <summary>
/// HUF/FSE entropy coding used by Lizard levels 30-49.
/// </summary>
/// <remarks>
/// The wire format is the HUF format from FiniteStateEntropy: a compact Huffman
/// weight table followed by four backward bit streams. Weight tables use either
/// the direct nibble representation or the standard FSE NCount representation.
/// This implementation is clean-room from the published FiniteStateEntropy
/// format/algorithm behavior; it does not depend on native code.
/// </remarks>
internal static class LizardHuffman {
  private const int MaxHuffmanBits = 12;
  private const int MaxSymbolValue = 255;
  private const int FseWeightTableLog = 6;
  private const int FseMinTableLog = 5;

  internal static byte[]? TryCompress(ReadOnlySpan<byte> source) {
    if (source.Length < 12)
      return null;

    Span<int> counts = stackalloc int[256];
    foreach (var value in source)
      ++counts[value];

    var maxSymbol = MaxSymbolValue;
    while (maxSymbol > 0 && counts[maxSymbol] == 0)
      --maxSymbol;

    if (counts[maxSymbol] == source.Length)
      return [source[0]];

    var frequencies = new long[256];
    for (var i = 0; i <= maxSymbol; ++i)
      frequencies[i] = counts[i];

    var root = HuffmanTree.BuildFromFrequencies(frequencies);
    var lengths = HuffmanTree.GetCodeLengths(root, 256);
    HuffmanTree.LimitCodeLengths(lengths, MaxHuffmanBits);

    var tableLog = 0;
    for (var i = 0; i <= maxSymbol; ++i)
      tableLog = Math.Max(tableLog, lengths[i]);
    if (tableLog is < 1 or > MaxHuffmanBits)
      return null;

    var kraft = 0;
    for (var i = 0; i <= maxSymbol; ++i) {
      var length = lengths[i];
      if (length > 0)
        kraft += 1 << (tableLog - length);
    }
    if (kraft != 1 << tableLog || lengths[maxSymbol] == 0)
      return null;

    var weights = new byte[256];
    for (var i = 0; i <= maxSymbol; ++i)
      if (lengths[i] > 0)
        weights[i] = checked((byte)(tableLog + 1 - lengths[i]));

    var tableHeader = WriteTableHeader(weights, maxSymbol);
    if (tableHeader is null)
      return null;

    BuildCodeValues(lengths, tableLog, maxSymbol, out var codeValues, out var codeLengths);

    var segmentSize = (source.Length + 3) / 4;
    var segment1 = EncodeHuffmanStream(source[..segmentSize], codeValues, codeLengths);
    var segment2 = EncodeHuffmanStream(source.Slice(segmentSize, segmentSize), codeValues, codeLengths);
    var segment3 = EncodeHuffmanStream(source.Slice(segmentSize * 2, segmentSize), codeValues, codeLengths);
    var segment4 = EncodeHuffmanStream(source[(segmentSize * 3)..], codeValues, codeLengths);

    if (segment1.Length > ushort.MaxValue || segment2.Length > ushort.MaxValue || segment3.Length > ushort.MaxValue)
      return null;

    using var output = new MemoryStream(tableHeader.Length + 6 + segment1.Length + segment2.Length + segment3.Length + segment4.Length);
    output.Write(tableHeader);
    Span<byte> jumpTable = stackalloc byte[6];
    BinaryPrimitives.WriteUInt16LittleEndian(jumpTable, (ushort)segment1.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(jumpTable[2..], (ushort)segment2.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(jumpTable[4..], (ushort)segment3.Length);
    output.Write(jumpTable);
    output.Write(segment1);
    output.Write(segment2);
    output.Write(segment3);
    output.Write(segment4);

    var result = output.ToArray();
    return result.Length < source.Length - 1 ? result : null;
  }

  internal static byte[] Decompress(ReadOnlySpan<byte> compressed, int originalSize) {
    if (originalSize < 0)
      throw new InvalidDataException("Negative Lizard HUF output size.");
    if (originalSize == 0)
      return [];
    if (compressed.Length == 1)
      return Enumerable.Repeat(compressed[0], originalSize).ToArray();
    if (compressed.Length == originalSize)
      return compressed.ToArray();
    if (compressed.Length < 2)
      throw new InvalidDataException("Truncated Lizard HUF stream.");

    var (weights, tableLog, tableBytes) = ReadTableHeader(compressed);
    if (compressed.Length - tableBytes < 10)
      throw new InvalidDataException("Truncated Lizard HUF four-stream payload.");

    BuildDecodeTable(weights, tableLog, out var symbols, out var bitLengths);

    var payload = compressed[tableBytes..];
    var length1 = BinaryPrimitives.ReadUInt16LittleEndian(payload);
    var length2 = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
    var length3 = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
    var length4 = payload.Length - 6 - length1 - length2 - length3;
    if (length4 <= 0)
      throw new InvalidDataException("Invalid Lizard HUF jump table.");

    var position = 6;
    if (position + length1 + length2 + length3 > payload.Length)
      throw new InvalidDataException("Lizard HUF jump table exceeds the compressed stream.");

    var segmentSize = (originalSize + 3) / 4;
    var output = new byte[originalSize];
    DecodeHuffmanStream(payload.Slice(position, length1), output.AsSpan(0, segmentSize), symbols, bitLengths, tableLog);
    position += length1;
    DecodeHuffmanStream(payload.Slice(position, length2), output.AsSpan(segmentSize, segmentSize), symbols, bitLengths, tableLog);
    position += length2;
    DecodeHuffmanStream(payload.Slice(position, length3), output.AsSpan(segmentSize * 2, segmentSize), symbols, bitLengths, tableLog);
    position += length3;
    DecodeHuffmanStream(payload[position..], output.AsSpan(segmentSize * 3), symbols, bitLengths, tableLog);
    return output;
  }

  private static byte[]? WriteTableHeader(byte[] weights, int maxSymbol) {
    var transmitted = weights.AsSpan(0, maxSymbol);
    if (maxSymbol > 0) {
      var fse = FseWeights.TryCompress(transmitted);
      if (fse is { Length: > 1 and < 128 } && fse.Length < maxSymbol / 2) {
        var result = new byte[fse.Length + 1];
        result[0] = (byte)fse.Length;
        fse.CopyTo(result, 1);
        return result;
      }
    }

    if (maxSymbol > 128)
      return null;

    var packedBytes = (maxSymbol + 1) / 2;
    var raw = new byte[packedBytes + 1];
    raw[0] = checked((byte)(128 + maxSymbol - 1));
    for (var symbol = 0; symbol < maxSymbol; symbol += 2) {
      var high = weights[symbol];
      var low = symbol + 1 < maxSymbol ? weights[symbol + 1] : 0;
      raw[1 + symbol / 2] = (byte)((high << 4) | low);
    }
    return raw;
  }

  private static (byte[] Weights, int TableLog, int BytesRead) ReadTableHeader(ReadOnlySpan<byte> input) {
    if (input.IsEmpty)
      throw new InvalidDataException("Missing Lizard HUF table header.");

    var header = input[0];
    byte[] transmitted;
    int bytesRead;
    if (header >= 128) {
      var count = header - 127;
      var packedBytes = (count + 1) / 2;
      if (input.Length < packedBytes + 1)
        throw new InvalidDataException("Truncated direct Lizard HUF weight table.");
      transmitted = new byte[count];
      for (var i = 0; i < count; i += 2) {
        var packed = input[1 + i / 2];
        transmitted[i] = (byte)(packed >> 4);
        if (i + 1 < count)
          transmitted[i + 1] = (byte)(packed & 0x0F);
      }
      bytesRead = packedBytes + 1;
    } else {
      var compressedSize = header;
      if (compressedSize == 0 || input.Length < compressedSize + 1)
        throw new InvalidDataException("Truncated FSE-compressed Lizard HUF weight table.");
      transmitted = FseWeights.Decompress(input.Slice(1, compressedSize), 255);
      bytesRead = compressedSize + 1;
    }

    if (transmitted.Length == 0 || transmitted.Length >= 256)
      throw new InvalidDataException("Invalid Lizard HUF weight count.");

    var rankStats = new int[MaxHuffmanBits + 1];
    var weightTotal = 0;
    foreach (var weight in transmitted) {
      if (weight >= MaxHuffmanBits)
        throw new InvalidDataException("Invalid Lizard HUF weight.");
      ++rankStats[weight];
      if (weight > 0)
        weightTotal += 1 << (weight - 1);
    }
    if (weightTotal == 0)
      throw new InvalidDataException("Empty Lizard HUF tree.");

    var tableLog = BitOperations.Log2((uint)weightTotal) + 1;
    if (tableLog > MaxHuffmanBits)
      throw new InvalidDataException("Lizard HUF table log exceeds 12 bits.");
    var rest = (1 << tableLog) - weightTotal;
    if (rest <= 0 || !BitOperations.IsPow2(rest))
      throw new InvalidDataException("Lizard HUF implied weight is not a power of two.");
    var lastWeight = BitOperations.Log2((uint)rest) + 1;
    ++rankStats[lastWeight];
    if (rankStats[1] < 2 || (rankStats[1] & 1) != 0)
      throw new InvalidDataException("Invalid Lizard HUF rank-1 population.");

    var weights = new byte[transmitted.Length + 1];
    transmitted.CopyTo(weights, 0);
    weights[^1] = checked((byte)lastWeight);
    return (weights, tableLog, bytesRead);
  }

  private static void BuildCodeValues(int[] lengths, int tableLog, int maxSymbol, out ushort[] values, out byte[] bitLengths) {
    var perLength = new int[tableLog + 2];
    for (var symbol = 0; symbol <= maxSymbol; ++symbol)
      if (lengths[symbol] > 0)
        ++perLength[lengths[symbol]];

    var nextValue = new int[tableLog + 2];
    var min = 0;
    for (var length = tableLog; length > 0; --length) {
      nextValue[length] = min;
      min += perLength[length];
      min >>= 1;
    }

    values = new ushort[256];
    bitLengths = new byte[256];
    for (var symbol = 0; symbol <= maxSymbol; ++symbol) {
      var length = lengths[symbol];
      if (length == 0)
        continue;
      values[symbol] = checked((ushort)nextValue[length]++);
      bitLengths[symbol] = checked((byte)length);
    }
  }

  private static void BuildDecodeTable(byte[] weights, int tableLog, out byte[] symbols, out byte[] bitLengths) {
    var rankStart = new int[tableLog + 1];
    var rankCount = new int[tableLog + 1];
    foreach (var weight in weights)
      if (weight > 0) {
        if (weight > tableLog)
          throw new InvalidDataException("Lizard HUF weight exceeds table log.");
        ++rankCount[weight];
      }

    var next = 0;
    for (var weight = 1; weight <= tableLog; ++weight) {
      rankStart[weight] = next;
      next += rankCount[weight] << (weight - 1);
    }
    if (next != 1 << tableLog)
      throw new InvalidDataException("Incomplete Lizard HUF decode table.");

    symbols = new byte[1 << tableLog];
    bitLengths = new byte[1 << tableLog];
    for (var symbol = 0; symbol < weights.Length; ++symbol) {
      var weight = weights[symbol];
      if (weight == 0)
        continue;
      var run = 1 << (weight - 1);
      var start = rankStart[weight];
      var bits = checked((byte)(tableLog + 1 - weight));
      for (var i = 0; i < run; ++i) {
        symbols[start + i] = (byte)symbol;
        bitLengths[start + i] = bits;
      }
      rankStart[weight] += run;
    }
  }

  private static byte[] EncodeHuffmanStream(ReadOnlySpan<byte> source, ushort[] values, byte[] bitLengths) {
    var writer = new ForwardBitWriter();
    for (var i = source.Length - 1; i >= 0; --i) {
      var symbol = source[i];
      var length = bitLengths[symbol];
      if (length == 0)
        throw new InvalidOperationException("HUF table is missing an input symbol.");
      writer.WriteBits(values[symbol], length);
    }
    return writer.CloseWithSentinel();
  }

  private static void DecodeHuffmanStream(ReadOnlySpan<byte> source, Span<byte> destination,
      byte[] symbols, byte[] bitLengths, int tableLog) {
    var reader = new ReverseBitReader(source);
    for (var i = 0; i < destination.Length; ++i) {
      var index = checked((int)reader.PeekBitsPadded(tableLog));
      var length = bitLengths[index];
      if (length == 0)
        throw new InvalidDataException("Invalid Lizard HUF codeword.");
      destination[i] = symbols[index];
      reader.SkipBits(length);
      if (reader.Overflowed)
        throw new InvalidDataException("Lizard HUF bitstream ended before its output was complete.");
    }
    if (!reader.IsConsumed)
      throw new InvalidDataException("Lizard HUF bitstream contains trailing code bits.");
  }

  private sealed class ForwardBitWriter {
    private readonly List<byte> _bytes = [];
    private ulong _bits;
    private int _count;

    internal void WriteBits(uint value, int count) {
      if (count is < 0 or > 31)
        throw new ArgumentOutOfRangeException(nameof(count));
      if (count == 0)
        return;
      var mask = (1UL << count) - 1;
      this._bits |= ((ulong)value & mask) << this._count;
      this._count += count;
      while (this._count >= 8) {
        this._bytes.Add((byte)this._bits);
        this._bits >>= 8;
        this._count -= 8;
      }
    }

    internal byte[] FlushWithoutSentinel() {
      if (this._count > 0) {
        this._bytes.Add((byte)this._bits);
        this._bits = 0;
        this._count = 0;
      }
      return this._bytes.ToArray();
    }

    internal byte[] CloseWithSentinel() {
      this.WriteBits(1, 1);
      return this.FlushWithoutSentinel();
    }
  }

  private ref struct ForwardBitReader(ReadOnlySpan<byte> source) {
    private int _position;

    internal int Position => this._position;

    internal uint PeekBits(int count) {
      if (count == 0)
        return 0;
      if (count < 0 || this._position + count > source.Length * 8)
        throw new InvalidDataException("Truncated FSE NCount header.");
      uint value = 0;
      for (var bit = 0; bit < count; ++bit) {
        var absolute = this._position + bit;
        value |= (uint)((source[absolute >> 3] >> (absolute & 7)) & 1) << bit;
      }
      return value;
    }

    internal uint ReadBits(int count) {
      var value = this.PeekBits(count);
      this._position += count;
      return value;
    }
  }

  private ref struct ReverseBitReader {
    private readonly ReadOnlySpan<byte> _source;
    private int _position;

    internal ReverseBitReader(ReadOnlySpan<byte> source) {
      if (source.IsEmpty || source[^1] == 0)
        throw new InvalidDataException("Lizard entropy stream has no end marker.");
      this._source = source;
      this._position = (source.Length - 1) * 8 + BitOperations.Log2((uint)source[^1]) - 1;
      this.Overflowed = false;
    }

    internal bool Overflowed { get; private set; }
    internal bool IsConsumed => this._position < 0;

    internal uint PeekBitsPadded(int count) {
      if (count is < 0 or > 31)
        throw new ArgumentOutOfRangeException(nameof(count));
      uint value = 0;
      var start = this._position - count + 1;
      for (var bit = 0; bit < count; ++bit) {
        var absolute = start + bit;
        if (absolute >= 0)
          value |= (uint)((this._source[absolute >> 3] >> (absolute & 7)) & 1) << bit;
      }
      return value;
    }

    internal uint ReadBits(int count, bool allowPadding = false) {
      var start = this._position - count + 1;
      if (start < 0) {
        this.Overflowed = true;
        if (!allowPadding)
          throw new InvalidDataException("Truncated Lizard entropy bitstream.");
      }
      var value = this.PeekBitsPadded(count);
      this._position -= count;
      return value;
    }

    internal void SkipBits(int count) {
      if (this._position - count + 1 < 0)
        this.Overflowed = true;
      this._position -= count;
    }
  }

  private static class FseWeights {
    private readonly record struct DecodeCell(byte Symbol, ushort NewState, byte NumBits);
    private readonly record struct Transform(int DeltaFindState, uint DeltaNbBits);
    private sealed record CompressionTable(int TableLog, ushort[] States, Transform[] Transforms);

    internal static byte[]? TryCompress(ReadOnlySpan<byte> source) {
      if (source.Length <= 2)
        return null;

      Span<int> counts = stackalloc int[MaxHuffmanBits + 1];
      foreach (var symbol in source) {
        if (symbol > MaxHuffmanBits)
          return null;
        ++counts[symbol];
      }
      var maxSymbol = MaxHuffmanBits;
      while (maxSymbol > 0 && counts[maxSymbol] == 0)
        --maxSymbol;
      if (counts[maxSymbol] == source.Length)
        return [source[0]];

      var normalized = Normalize(counts, maxSymbol, FseWeightTableLog, source.Length);
      var header = WriteNCount(normalized, maxSymbol, FseWeightTableLog);
      var table = BuildCompressionTable(normalized, maxSymbol, FseWeightTableLog);
      var bits = Encode(source, table);
      var result = new byte[header.Length + bits.Length];
      header.CopyTo(result, 0);
      bits.CopyTo(result, header.Length);
      return result.Length < source.Length ? result : null;
    }

    internal static byte[] Decompress(ReadOnlySpan<byte> source, int capacity) {
      var (normalized, maxSymbol, tableLog, headerBytes) = ReadNCount(source);
      var table = BuildDecodeTable(normalized, maxSymbol, tableLog);
      var bits = source[headerBytes..];
      if (bits.IsEmpty)
        throw new InvalidDataException("Missing FSE weight bitstream.");

      var reader = new ReverseBitReader(bits);
      var state1 = checked((int)reader.ReadBits(tableLog));
      var state2 = checked((int)reader.ReadBits(tableLog));
      var output = new List<byte>();

      while (true) {
        if (output.Count >= capacity - 1)
          throw new InvalidDataException("FSE weight stream exceeds its destination capacity.");
        output.Add(DecodeSymbol(table, ref state1, ref reader));
        if (reader.Overflowed) {
          output.Add(DecodeSymbol(table, ref state2, ref reader));
          break;
        }

        if (output.Count >= capacity - 1)
          throw new InvalidDataException("FSE weight stream exceeds its destination capacity.");
        output.Add(DecodeSymbol(table, ref state2, ref reader));
        if (reader.Overflowed) {
          output.Add(DecodeSymbol(table, ref state1, ref reader));
          break;
        }
      }

      if (output.Count > capacity)
        throw new InvalidDataException("FSE weight stream exceeds its destination capacity.");
      return output.ToArray();
    }

    private static byte DecodeSymbol(DecodeCell[] table, ref int state, ref ReverseBitReader reader) {
      if ((uint)state >= (uint)table.Length)
        throw new InvalidDataException("Invalid FSE state.");
      var cell = table[state];
      var lowBits = reader.ReadBits(cell.NumBits, allowPadding: true);
      state = cell.NewState + checked((int)lowBits);
      return cell.Symbol;
    }

    private static short[] Normalize(ReadOnlySpan<int> counts, int maxSymbol, int tableLog, int total) {
      var tableSize = 1 << tableLog;
      var normalized = new short[maxSymbol + 1];
      var assigned = 0;
      for (var symbol = 0; symbol <= maxSymbol; ++symbol) {
        if (counts[symbol] == 0)
          continue;
        var value = Math.Max(1, (int)((long)counts[symbol] * tableSize / total));
        normalized[symbol] = checked((short)value);
        assigned += value;
      }

      while (assigned > tableSize) {
        var best = -1;
        long greatestExcess = long.MinValue;
        for (var symbol = 0; symbol <= maxSymbol; ++symbol) {
          if (normalized[symbol] <= 1)
            continue;
          var excess = (long)normalized[symbol] * total - (long)counts[symbol] * tableSize;
          if (excess > greatestExcess) {
            greatestExcess = excess;
            best = symbol;
          }
        }
        if (best < 0)
          throw new InvalidOperationException("Unable to normalize FSE weights.");
        --normalized[best];
        --assigned;
      }

      while (assigned < tableSize) {
        var best = -1;
        long greatestDeficit = long.MinValue;
        for (var symbol = 0; symbol <= maxSymbol; ++symbol) {
          if (counts[symbol] == 0)
            continue;
          var deficit = (long)counts[symbol] * tableSize - (long)normalized[symbol] * total;
          if (deficit > greatestDeficit) {
            greatestDeficit = deficit;
            best = symbol;
          }
        }
        if (best < 0)
          throw new InvalidOperationException("Unable to normalize FSE weights.");
        ++normalized[best];
        ++assigned;
      }

      return normalized;
    }

    private static byte[] WriteNCount(short[] normalized, int maxSymbol, int tableLog) {
      var writer = new ForwardBitWriter();
      writer.WriteBits((uint)(tableLog - FseMinTableLog), 4);

      var remaining = (1 << tableLog) + 1;
      var threshold = 1 << tableLog;
      var nbBits = tableLog + 1;
      var symbol = 0;
      var previousIsZero = false;
      while (symbol <= maxSymbol && remaining > 1) {
        if (previousIsZero) {
          var start = symbol;
          while (symbol <= maxSymbol && normalized[symbol] == 0)
            ++symbol;
          if (symbol > maxSymbol)
            throw new InvalidOperationException("Invalid trailing zero run in FSE normalization.");
          while (symbol >= start + 24) {
            writer.WriteBits(0xFFFF, 16);
            start += 24;
          }
          while (symbol >= start + 3) {
            writer.WriteBits(3, 2);
            start += 3;
          }
          writer.WriteBits((uint)(symbol - start), 2);
        }

        var count = normalized[symbol++];
        var max = (2 * threshold - 1) - remaining;
        remaining -= Math.Abs(count);
        var encoded = count + 1;
        if (encoded >= threshold)
          encoded += max;
        var bits = nbBits - (encoded < max ? 1 : 0);
        writer.WriteBits(checked((uint)encoded), bits);
        previousIsZero = encoded == 1;
        if (remaining < 1)
          throw new InvalidOperationException("Invalid FSE normalized distribution.");
        while (remaining < threshold) {
          --nbBits;
          threshold >>= 1;
        }
      }

      if (remaining != 1)
        throw new InvalidOperationException("FSE normalized distribution does not fill its table.");
      return writer.FlushWithoutSentinel();
    }

    private static (short[] Normalized, int MaxSymbol, int TableLog, int BytesRead) ReadNCount(ReadOnlySpan<byte> source) {
      var reader = new ForwardBitReader(source);
      var tableLog = checked((int)reader.ReadBits(4)) + FseMinTableLog;
      if (tableLog is < FseMinTableLog or > FseWeightTableLog)
        throw new InvalidDataException("Invalid FSE weight table log.");

      var normalized = new short[MaxHuffmanBits + 1];
      var remaining = (1 << tableLog) + 1;
      var threshold = 1 << tableLog;
      var nbBits = tableLog + 1;
      var symbol = 0;
      var previousZero = false;
      while (remaining > 1 && symbol <= MaxHuffmanBits) {
        if (previousZero) {
          var zeroEnd = symbol;
          while (reader.PeekBits(16) == 0xFFFF) {
            reader.ReadBits(16);
            zeroEnd += 24;
            if (zeroEnd > MaxHuffmanBits + 1)
              throw new InvalidDataException("FSE zero run exceeds the weight alphabet.");
          }
          while (reader.PeekBits(2) == 3) {
            reader.ReadBits(2);
            zeroEnd += 3;
            if (zeroEnd > MaxHuffmanBits + 1)
              throw new InvalidDataException("FSE zero run exceeds the weight alphabet.");
          }
          zeroEnd += checked((int)reader.ReadBits(2));
          if (zeroEnd > MaxHuffmanBits)
            throw new InvalidDataException("FSE zero run exceeds the weight alphabet.");
          while (symbol < zeroEnd)
            normalized[symbol++] = 0;
        }

        var max = (2 * threshold - 1) - remaining;
        int count;
        if (reader.PeekBits(nbBits - 1) < max) {
          count = checked((int)reader.ReadBits(nbBits - 1));
        } else {
          count = checked((int)reader.ReadBits(nbBits));
          if (count >= threshold)
            count -= max;
        }
        --count;
        remaining -= Math.Abs(count);
        normalized[symbol++] = checked((short)count);
        previousZero = count == 0;
        while (remaining < threshold) {
          --nbBits;
          threshold >>= 1;
        }
      }

      if (remaining != 1 || symbol == 0)
        throw new InvalidDataException("Invalid FSE normalized weight distribution.");
      return (normalized, symbol - 1, tableLog, (reader.Position + 7) / 8);
    }

    private static CompressionTable BuildCompressionTable(short[] normalized, int maxSymbol, int tableLog) {
      var tableSize = 1 << tableLog;
      var tableMask = tableSize - 1;
      var step = (tableSize >> 1) + (tableSize >> 3) + 3;
      var tableSymbols = new byte[tableSize];
      var highThreshold = tableSize - 1;
      var cumul = new int[maxSymbol + 2];
      for (var symbol = 1; symbol <= maxSymbol + 1; ++symbol) {
        var count = normalized[symbol - 1];
        if (count == -1) {
          cumul[symbol] = cumul[symbol - 1] + 1;
          tableSymbols[highThreshold--] = (byte)(symbol - 1);
        } else {
          cumul[symbol] = cumul[symbol - 1] + count;
        }
      }
      cumul[maxSymbol + 1] = tableSize + 1;

      var position = 0;
      for (var symbol = 0; symbol <= maxSymbol; ++symbol) {
        for (var occurrence = 0; occurrence < normalized[symbol]; ++occurrence) {
          tableSymbols[position] = (byte)symbol;
          position = (position + step) & tableMask;
          while (position > highThreshold)
            position = (position + step) & tableMask;
        }
      }
      if (position != 0)
        throw new InvalidOperationException("FSE symbol spread did not wrap to zero.");

      var states = new ushort[tableSize];
      var nextCumul = (int[])cumul.Clone();
      for (var state = 0; state < tableSize; ++state) {
        var symbol = tableSymbols[state];
        states[nextCumul[symbol]++] = checked((ushort)(tableSize + state));
      }

      var transforms = new Transform[maxSymbol + 1];
      var total = 0;
      for (var symbol = 0; symbol <= maxSymbol; ++symbol) {
        var count = normalized[symbol];
        if (count == 0) {
          transforms[symbol] = new(0, unchecked((uint)(((tableLog + 1) << 16) - tableSize)));
          continue;
        }
        if (count is -1 or 1) {
          transforms[symbol] = new(total - 1, unchecked((uint)((tableLog << 16) - tableSize)));
          ++total;
          continue;
        }

        var maxBitsOut = tableLog - BitOperations.Log2((uint)(count - 1));
        var minStatePlus = count << maxBitsOut;
        transforms[symbol] = new(total - count, unchecked((uint)((maxBitsOut << 16) - minStatePlus)));
        total += count;
      }
      return new(tableLog, states, transforms);
    }

    private static DecodeCell[] BuildDecodeTable(short[] normalized, int maxSymbol, int tableLog) {
      var tableSize = 1 << tableLog;
      var highThreshold = tableSize - 1;
      var table = new DecodeCell[tableSize];
      var symbols = new byte[tableSize];
      var next = new int[maxSymbol + 1];
      for (var symbol = 0; symbol <= maxSymbol; ++symbol) {
        if (normalized[symbol] == -1) {
          symbols[highThreshold--] = (byte)symbol;
          next[symbol] = 1;
        } else {
          next[symbol] = normalized[symbol];
        }
      }

      var mask = tableSize - 1;
      var step = (tableSize >> 1) + (tableSize >> 3) + 3;
      var position = 0;
      for (var symbol = 0; symbol <= maxSymbol; ++symbol) {
        for (var i = 0; i < normalized[symbol]; ++i) {
          symbols[position] = (byte)symbol;
          position = (position + step) & mask;
          while (position > highThreshold)
            position = (position + step) & mask;
        }
      }
      if (position != 0)
        throw new InvalidDataException("Invalid FSE symbol spread.");

      for (var state = 0; state < tableSize; ++state) {
        var symbol = symbols[state];
        var nextState = next[symbol]++;
        var numBits = tableLog - BitOperations.Log2((uint)nextState);
        table[state] = new(symbol, checked((ushort)((nextState << numBits) - tableSize)), checked((byte)numBits));
      }
      return table;
    }

    private static byte[] Encode(ReadOnlySpan<byte> source, CompressionTable table) {
      var writer = new ForwardBitWriter();
      var index = source.Length;
      uint state1;
      uint state2;
      if ((source.Length & 1) != 0) {
        state1 = InitState(table, source[--index]);
        state2 = InitState(table, source[--index]);
        EncodeSymbol(table, ref state1, source[--index], writer);
      } else {
        state2 = InitState(table, source[--index]);
        state1 = InitState(table, source[--index]);
      }

      var remaining = source.Length - 2;
      if ((remaining & 2) != 0) {
        EncodeSymbol(table, ref state2, source[--index], writer);
        EncodeSymbol(table, ref state1, source[--index], writer);
      }

      while (index > 0) {
        EncodeSymbol(table, ref state2, source[--index], writer);
        EncodeSymbol(table, ref state1, source[--index], writer);
        if (index > 0) {
          EncodeSymbol(table, ref state2, source[--index], writer);
          EncodeSymbol(table, ref state1, source[--index], writer);
        }
      }

      writer.WriteBits(state2, table.TableLog);
      writer.WriteBits(state1, table.TableLog);
      return writer.CloseWithSentinel();
    }

    private static uint InitState(CompressionTable table, byte symbol) {
      var transform = table.Transforms[symbol];
      var bitsOut = (transform.DeltaNbBits + (1u << 15)) >> 16;
      var value = (bitsOut << 16) - transform.DeltaNbBits;
      var index = checked((int)(value >> checked((int)bitsOut))) + transform.DeltaFindState;
      if ((uint)index >= (uint)table.States.Length)
        throw new InvalidOperationException("Invalid FSE initial state.");
      return table.States[index];
    }

    private static void EncodeSymbol(CompressionTable table, ref uint state, byte symbol, ForwardBitWriter writer) {
      var transform = table.Transforms[symbol];
      var bitsOut = checked((int)((state + transform.DeltaNbBits) >> 16));
      writer.WriteBits(state, bitsOut);
      var index = checked((int)(state >> bitsOut)) + transform.DeltaFindState;
      if ((uint)index >= (uint)table.States.Length)
        throw new InvalidOperationException("Invalid FSE transition.");
      state = table.States[index];
    }
  }
}
