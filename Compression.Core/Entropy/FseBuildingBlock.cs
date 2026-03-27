using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Entropy;

/// <summary>
/// Exposes FSE (Finite State Entropy) / tANS as a benchmarkable building block.
/// Uses a state machine where transitions encode symbol probability information.
/// Table size is 1024 (tableLog=10). Symbols are spread proportionally to frequency.
/// Encoding processes symbols in reverse (ANS is LIFO).
/// </summary>
public sealed class FseBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_FSE";
  /// <inheritdoc/>
  public string DisplayName => "FSE/tANS";
  /// <inheritdoc/>
  public string Description => "Table-based Asymmetric Numeral Systems, used in Zstd";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;

  private const int DefaultTableLog = 10;
  private const int DefaultTableSize = 1 << DefaultTableLog; // 1024

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    // Write uncompressed size (4 bytes, LE).
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    // Count symbol frequencies.
    var rawFreq = new int[256];
    foreach (var b in data)
      rawFreq[b]++;

    // Collect used symbols.
    var symbols = new List<byte>();
    for (var i = 0; i < 256; i++)
      if (rawFreq[i] > 0)
        symbols.Add((byte)i);

    // Single-symbol case.
    if (symbols.Count == 1) {
      ms.WriteByte(0); // tableLog=0 signals single-symbol mode
      ms.WriteByte(symbols[0]);
      return ms.ToArray();
    }

    var tableLog = DefaultTableLog;
    var tableSize = DefaultTableSize;

    var normFreq = NormalizeFrequencies(rawFreq, symbols, tableSize, data.Length);
    var symbolTable = BuildSpreadTable(normFreq, symbols, tableSize);

    // Build per-symbol occurrence mapping.
    var symOccurrence = new int[256];
    var positionToReduced = new int[tableSize];
    for (var s = 0; s < tableSize; s++) {
      var sym = symbolTable[s];
      var k = symOccurrence[sym]++;
      positionToReduced[s] = normFreq[sym] + k;
    }

    // Build encoding table.
    var encTable = new int[256][];
    for (var i = 0; i < 256; i++) {
      if (normFreq[i] > 0)
        encTable[i] = new int[normFreq[i]];
    }
    for (var s = 0; s < tableSize; s++) {
      var sym = symbolTable[s];
      var r = positionToReduced[s];
      encTable[sym][r - normFreq[sym]] = s;
    }

    // Encode in reverse.
    var bitStack = new List<byte>();
    var state = tableSize;

    for (var i = data.Length - 1; i >= 0; i--) {
      var sym = data[i];
      var f = normFreq[sym];

      // Reduce state to [f, 2*f-1] by outputting low bits.
      while (state >= 2 * f) {
        bitStack.Add((byte)(state & 1));
        state >>= 1;
      }

      var spreadPos = encTable[sym][state - f];
      state = spreadPos + tableSize;
    }

    // Write header.
    ms.WriteByte((byte)tableLog);

    Span<byte> buf2 = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(buf2, (ushort)symbols.Count);
    ms.Write(buf2);

    foreach (var sym in symbols) {
      ms.WriteByte(sym);
      BinaryPrimitives.WriteUInt16LittleEndian(buf2, (ushort)normFreq[sym]);
      ms.Write(buf2);
    }

    // Write final state.
    BinaryPrimitives.WriteUInt16LittleEndian(buf2, (ushort)state);
    ms.Write(buf2);

    // Write bit count.
    Span<byte> buf4 = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(buf4, bitStack.Count);
    ms.Write(buf4);

    // Pack bits into bytes.
    var byteCount = (bitStack.Count + 7) / 8;
    var packed = new byte[byteCount];
    for (var i = 0; i < bitStack.Count; i++) {
      if (bitStack[i] != 0)
        packed[i / 8] |= (byte)(1 << (i % 8));
    }
    ms.Write(packed);

    return ms.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var offset = 0;

    var uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    offset += 4;

    if (uncompressedSize == 0)
      return [];

    var tableLogByte = data[offset++];

    if (tableLogByte == 0) {
      var sym = data[offset];
      var result = new byte[uncompressedSize];
      Array.Fill(result, sym);
      return result;
    }

    var tableLog = (int)tableLogByte;
    var tableSize = 1 << tableLog;

    var symbolCount = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    offset += 2;

    var normFreq = new int[256];
    var symbols = new List<byte>(symbolCount);
    for (var i = 0; i < symbolCount; i++) {
      var sym = data[offset++];
      symbols.Add(sym);
      normFreq[sym] = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
      offset += 2;
    }

    // Read final state.
    var state = (int)BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    offset += 2;

    var bitCount = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
    offset += 4;

    var byteCount = (bitCount + 7) / 8;
    var packed = data.Slice(offset, byteCount);

    // Unpack bits.
    var bitStack = new byte[bitCount];
    for (var i = 0; i < bitCount; i++)
      bitStack[i] = (byte)((packed[i / 8] >> (i % 8)) & 1);

    // Rebuild spread table and occurrence mapping.
    var symbolTable = BuildSpreadTable(normFreq, symbols, tableSize);

    var symOccurrence = new int[256];
    var positionToReduced = new int[tableSize];
    for (var s = 0; s < tableSize; s++) {
      var sym = symbolTable[s];
      var k = symOccurrence[sym]++;
      positionToReduced[s] = normFreq[sym] + k;
    }

    // Decode.
    var decoded = new byte[uncompressedSize];
    var bitPos = bitStack.Length - 1;

    for (var i = 0; i < uncompressedSize; i++) {
      var spreadPos = state - tableSize;
      if (spreadPos < 0 || spreadPos >= tableSize)
        throw new InvalidDataException($"FSE: invalid state {state} during decoding.");

      var sym = symbolTable[spreadPos];
      decoded[i] = sym;

      var reduced = positionToReduced[spreadPos];
      state = reduced;

      while (state < tableSize) {
        if (bitPos < 0)
          throw new InvalidDataException("FSE: unexpected end of bitstream during decoding.");
        state = (state << 1) | bitStack[bitPos--];
      }
    }

    return decoded;
  }

  private static int[] NormalizeFrequencies(int[] rawFreq, List<byte> symbols, int tableSize, int totalCount) {
    var normFreq = new int[256];
    var assigned = 0;

    foreach (var sym in symbols) {
      var nf = (int)((long)rawFreq[sym] * tableSize / totalCount);
      if (nf < 1) nf = 1;
      normFreq[sym] = nf;
      assigned += nf;
    }

    while (assigned != tableSize) {
      if (assigned < tableSize) {
        var bestSym = symbols[0];
        var bestError = double.MinValue;
        foreach (var sym in symbols) {
          var ideal = (double)rawFreq[sym] * tableSize / totalCount;
          var error = ideal - normFreq[sym];
          if (error > bestError) {
            bestError = error;
            bestSym = sym;
          }
        }
        normFreq[bestSym]++;
        assigned++;
      } else {
        var bestSym = symbols[0];
        var bestError = double.MaxValue;
        foreach (var sym in symbols) {
          if (normFreq[sym] <= 1) continue;
          var ideal = (double)rawFreq[sym] * tableSize / totalCount;
          var error = ideal - normFreq[sym];
          if (error < bestError) {
            bestError = error;
            bestSym = sym;
          }
        }
        if (normFreq[bestSym] > 1) {
          normFreq[bestSym]--;
          assigned--;
        } else {
          break;
        }
      }
    }

    return normFreq;
  }

  private static byte[] BuildSpreadTable(int[] normFreq, List<byte> symbols, int tableSize) {
    var table = new byte[tableSize];
    var step = (tableSize >> 1) + (tableSize >> 3) + 3;
    var mask = tableSize - 1;
    var pos = 0;

    foreach (var sym in symbols) {
      for (var i = 0; i < normFreq[sym]; i++) {
        table[pos] = sym;
        pos = (pos + step) & mask;
      }
    }

    return table;
  }
}
