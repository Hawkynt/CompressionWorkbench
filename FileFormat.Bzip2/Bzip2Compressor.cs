using Compression.Core.BitIO;
using Compression.Core.Checksums;
using Compression.Core.Entropy.Huffman;
using Compression.Core.Transforms;

namespace FileFormat.Bzip2;

/// <summary>
/// Compresses data using the bzip2 block-sorting algorithm.
/// </summary>
internal sealed class Bzip2Compressor {
  private readonly BitWriter<MsbBitOrder> _bitWriter;
  private readonly int _blockSize;
  private byte[] _blockBuffer;
  private int _blockLength;
  private uint _combinedCrc;

  /// <summary>
  /// Gets the combined CRC-32 of all blocks.
  /// </summary>
  public uint CombinedCrc => this._combinedCrc;

  /// <summary>
  /// Initializes a new bzip2 compressor.
  /// </summary>
  /// <param name="bitWriter">The bit writer for output.</param>
  /// <param name="blockSize100k">Block size multiplier (1-9).</param>
  public Bzip2Compressor(BitWriter<MsbBitOrder> bitWriter, int blockSize100k = 9) {
    this._bitWriter = bitWriter;
    this._blockSize = blockSize100k * 100_000;
    this._blockBuffer = new byte[this._blockSize + 1000]; // Extra room for RLE1
    this._blockLength = 0;
  }

  /// <summary>
  /// Writes data to the compressor, flushing blocks as needed.
  /// </summary>
  public void Write(ReadOnlySpan<byte> data) {
    var offset = 0;
    while (offset < data.Length) {
      var remaining = this._blockSize - this._blockLength;
      var toCopy = Math.Min(remaining, data.Length - offset);
      data.Slice(offset, toCopy).CopyTo(this._blockBuffer.AsSpan(this._blockLength));
      this._blockLength += toCopy;
      offset += toCopy;

      if (this._blockLength >= this._blockSize)
        FlushBlock();
    }
  }

  /// <summary>
  /// Finishes compression, flushing any remaining data.
  /// </summary>
  public void Finish() {
    if (this._blockLength > 0)
      FlushBlock();

    // End-of-stream magic (48 bits)
    WriteBits48(Bzip2Constants.BlockEndMagic);

    // Combined CRC
    this._bitWriter.WriteBits(this._combinedCrc, 32);

    // Pad to byte boundary
    this._bitWriter.FlushBits();
  }

  private void FlushBlock() {
    ReadOnlySpan<byte> blockData = this._blockBuffer.AsSpan(0, this._blockLength);
    this._blockLength = 0;

    // Compute block CRC
    var blockCrc = Crc32Bzip2(blockData);
    this._combinedCrc = ((this._combinedCrc << 1) | (this._combinedCrc >> 31)) ^ blockCrc;

    // Step 1: RLE1 — run-length encode runs of 4+ identical bytes
    var rle1Data = Rle1Encode(blockData);

    // Step 2: BWT
    var (bwtData, bwtIndex) = BurrowsWheelerTransform.Forward(rle1Data);

    // Step 3: Determine in-use symbols and MTF with compact alphabet
    var symbolUsed = new bool[256];
    for (var i = 0; i < bwtData.Length; ++i)
      symbolUsed[bwtData[i]] = true;

    var numSymbolsInUse = 0;
    var mtfAlphabet = new byte[256];
    for (var i = 0; i < 256; ++i) {
      if (symbolUsed[i])
        mtfAlphabet[numSymbolsInUse++] = (byte)i;
    }

    // MTF encode using only the in-use alphabet
    var mtfData = MtfEncodeWithAlphabet(bwtData, mtfAlphabet.AsSpan(0, numSymbolsInUse));

    // EOB symbol is numSymbolsInUse + 1 (RUNA=0, RUNB=1, then symbols 2..numSymbolsInUse, then EOB)
    var eobSymbol = numSymbolsInUse + 1;
    var alphaSize = eobSymbol + 1;

    var rle2Symbols = Rle2Encode(mtfData, eobSymbol);

    // Step 5: Build Huffman tables
    WriteBlock(blockCrc, bwtIndex, symbolUsed, alphaSize, rle2Symbols);
  }

  private void WriteBlock(uint blockCrc, int bwtIndex, bool[] symbolUsed,
    int alphaSize, int[] symbols) {
    // Block header magic (48 bits)
    WriteBits48(Bzip2Constants.BlockHeaderMagic);

    // Block CRC (32 bits)
    this._bitWriter.WriteBits(blockCrc, 32);

    // Randomized flag (1 bit, always 0)
    this._bitWriter.WriteBit(0);

    // BWT original pointer (24 bits)
    this._bitWriter.WriteBits((uint)bwtIndex, 24);

    // Symbol bitmap: 16 segments of 16 bits each
    WriteSymbolBitmap(symbolUsed);

    var numGroups = (symbols.Length + Bzip2Constants.GroupSize - 1) / Bzip2Constants.GroupSize;
    if (numGroups == 0) numGroups = 1;

    // Choose number of tables based on data size
    var numTrees = numGroups < 2 ? 1
      : numGroups < 8 ? 2
      : numGroups < 100 ? 3
      : numGroups < 600 ? 4
      : numGroups < 1200 ? 5
      : Bzip2Constants.MaxTrees;
    numTrees = Math.Min(numTrees, numGroups);

    // Build per-table code lengths and selectors
    var (tableLengths, selectors) = BuildMultiTable(symbols, alphaSize, numTrees, numGroups);

    var numSelectors = selectors.Length;

    // Number of trees (3 bits)
    this._bitWriter.WriteBits((uint)numTrees, 3);

    // Number of selectors (15 bits)
    this._bitWriter.WriteBits((uint)numSelectors, 15);

    // Write selectors (MTF-encoded, then unary-coded)
    var selectorMtf = MtfEncodeSelectors(selectors, numTrees);
    for (var i = 0; i < numSelectors; ++i) {
      int v = selectorMtf[i];
      for (var j = 0; j < v; ++j)
        this._bitWriter.WriteBit(1);
      this._bitWriter.WriteBit(0);
    }

    // Write code lengths for each table (delta-encoded)
    for (var t = 0; t < numTrees; ++t)
      WriteCodeLengths(tableLengths[t], alphaSize);

    // Write Huffman-coded symbols using the selected table per group
    var canonicals = new CanonicalHuffman[numTrees];
    for (var t = 0; t < numTrees; ++t)
      canonicals[t] = new CanonicalHuffman(tableLengths[t]);

    var symIdx = 0;
    for (var g = 0; g < numGroups; ++g) {
      var huff = canonicals[selectors[g]];
      var groupEnd = Math.Min(symIdx + Bzip2Constants.GroupSize, symbols.Length);
      while (symIdx < groupEnd) {
        var (code, len) = huff.GetCode(symbols[symIdx]);
        this._bitWriter.WriteBits(code, len);
        ++symIdx;
      }
    }
  }

  private static (int[][] TableLengths, byte[] Selectors) BuildMultiTable(
      int[] symbols, int alphaSize, int numTrees, int numGroups) {
    // Initialize tables by dividing symbols roughly equally among tables
    // Each table is initially optimized for a different frequency distribution
    var tableLengths = new int[numTrees][];
    var selectors = new byte[numGroups];

    // Build initial frequency counts per group
    var groupFreqs = new long[numGroups][];
    for (var g = 0; g < numGroups; ++g) {
      groupFreqs[g] = new long[alphaSize];
      var start = g * Bzip2Constants.GroupSize;
      var end = Math.Min(start + Bzip2Constants.GroupSize, symbols.Length);
      for (var i = start; i < end; ++i)
        ++groupFreqs[g][symbols[i]];
    }

    // Initialize tables: assign groups evenly to tables
    for (var g = 0; g < numGroups; ++g)
      selectors[g] = (byte)(g * numTrees / numGroups);

    // Build initial tables from assigned groups
    RebuildTables(tableLengths, selectors, groupFreqs, alphaSize, numTrees, numGroups);

    // Iterate: reassign groups to best table, rebuild tables
    const int maxIterations = 4;
    for (var iter = 0; iter < maxIterations; ++iter) {
      var changed = false;
      for (var g = 0; g < numGroups; ++g) {
        // Find the table with minimum coded bits for this group
        var bestTable = 0;
        var bestCost = long.MaxValue;
        for (var t = 0; t < numTrees; ++t) {
          long cost = 0;
          for (var s = 0; s < alphaSize; ++s)
            cost += groupFreqs[g][s] * tableLengths[t][s];
          if (cost < bestCost) {
            bestCost = cost;
            bestTable = t;
          }
        }
        if (selectors[g] != bestTable) {
          selectors[g] = (byte)bestTable;
          changed = true;
        }
      }
      if (!changed) break;
      RebuildTables(tableLengths, selectors, groupFreqs, alphaSize, numTrees, numGroups);
    }

    return (tableLengths, selectors);
  }

  private static void RebuildTables(int[][] tableLengths, byte[] selectors,
      long[][] groupFreqs, int alphaSize, int numTrees, int numGroups) {
    for (var t = 0; t < numTrees; ++t) {
      var freq = new long[alphaSize];
      for (var g = 0; g < numGroups; ++g)
        if (selectors[g] == t)
          for (var s = 0; s < alphaSize; ++s)
            freq[s] += groupFreqs[g][s];

      // Ensure all symbols have at least frequency 1
      for (var s = 0; s < alphaSize; ++s)
        if (freq[s] == 0) freq[s] = 1;

      var root = HuffmanTree.BuildFromFrequencies(freq);
      tableLengths[t] = HuffmanTree.GetCodeLengths(root, alphaSize);
      HuffmanTree.LimitCodeLengths(tableLengths[t], Bzip2Constants.MaxCodeLength);
    }
  }

  private static byte[] MtfEncodeSelectors(byte[] selectors, int numTrees) {
    var alpha = new byte[numTrees];
    for (var i = 0; i < numTrees; ++i) alpha[i] = (byte)i;

    var result = new byte[selectors.Length];
    for (var i = 0; i < selectors.Length; ++i) {
      var val = selectors[i];
      var idx = 0;
      while (alpha[idx] != val) ++idx;
      result[i] = (byte)idx;
      if (idx > 0) {
        var tmp = alpha[idx];
        Array.Copy(alpha, 0, alpha, 1, idx);
        alpha[0] = tmp;
      }
    }
    return result;
  }

  private void WriteSymbolBitmap(bool[] symbolUsed) {
    // Which 16-byte groups are used?
    var inUse16 = 0;
    for (var i = 0; i < 16; ++i) {
      var groupUsed = false;
      for (var j = 0; j < 16; ++j) {
        if (symbolUsed[i * 16 + j]) {
          groupUsed = true;
          break;
        }
      }
      if (groupUsed)
        inUse16 |= 1 << (15 - i);
    }

    this._bitWriter.WriteBits((uint)inUse16, 16);

    // For each used group, write 16 bits
    for (var i = 0; i < 16; ++i) {
      if ((inUse16 & (1 << (15 - i))) != 0) {
        var bits = 0;
        for (var j = 0; j < 16; ++j) {
          if (symbolUsed[i * 16 + j])
            bits |= 1 << (15 - j);
        }
        this._bitWriter.WriteBits((uint)bits, 16);
      }
    }
  }

  private void WriteCodeLengths(int[] codeLengths, int alphaSize) {
    var currentLen = codeLengths[0];
    this._bitWriter.WriteBits((uint)currentLen, 5);

    for (var i = 0; i < alphaSize; ++i) {
      var targetLen = codeLengths[i];
      while (currentLen != targetLen) {
        this._bitWriter.WriteBit(1); // More changes
        if (currentLen < targetLen) {
          this._bitWriter.WriteBit(1); // Increment
          ++currentLen;
        }
        else {
          this._bitWriter.WriteBit(0); // Decrement
          --currentLen;
        }
      }
      this._bitWriter.WriteBit(0); // Done with this symbol
    }
  }

  private void WriteBits48(long value) {
    this._bitWriter.WriteBits((uint)(value >> 16), 32);
    this._bitWriter.WriteBits((uint)(value & 0xFFFF), 16);
  }

  private static byte[] MtfEncodeWithAlphabet(ReadOnlySpan<byte> data, ReadOnlySpan<byte> alphabet) {
    var alpha = alphabet.ToArray();
    var result = new byte[data.Length];

    for (var i = 0; i < data.Length; ++i) {
      var b = data[i];
      var idx = 0;
      while (alpha[idx] != b)
        ++idx;

      result[i] = (byte)idx;

      if (idx > 0) {
        var val = alpha[idx];
        alpha.AsSpan(0, idx).CopyTo(alpha.AsSpan(1));
        alpha[0] = val;
      }
    }

    return result;
  }

  /// <summary>
  /// RLE1: Encode runs of 4+ identical bytes.
  /// </summary>
  internal static byte[] Rle1Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var result = new List<byte>(data.Length);
    var i = 0;
    while (i < data.Length) {
      var b = data[i];
      var runLength = 1;
      while (i + runLength < data.Length && data[i + runLength] == b && runLength < 259)
        ++runLength;

      if (runLength >= 4) {
        // Write 4 copies of the byte, then the extra count
        result.Add(b);
        result.Add(b);
        result.Add(b);
        result.Add(b);
        result.Add((byte)(runLength - 4));
        i += runLength;
      }
      else {
        result.Add(b);
        ++i;
      }
    }

    return result.ToArray();
  }

  /// <summary>
  /// RLE1: Decode runs of 4+ identical bytes.
  /// </summary>
  internal static byte[] Rle1Decode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var result = new List<byte>(data.Length);
    var i = 0;
    while (i < data.Length) {
      var b = data[i++];
      result.Add(b);

      var runCount = 1;
      while (i < data.Length && data[i] == b && runCount < 3) {
        result.Add(b);
        ++i;
        ++runCount;
      }

      if (runCount == 3 && i < data.Length && data[i] == b) {
        result.Add(b);
        ++i;
        // Next byte is the extra count
        if (i < data.Length) {
          int extra = data[i++];
          for (var j = 0; j < extra; ++j)
            result.Add(b);
        }
      }
    }

    return result.ToArray();
  }

  /// <summary>
  /// RLE2: Zero-run-length encoding using RUNA/RUNB bijective numeration.
  /// Non-zero MTF indices are shifted by +1 to make room for RUNA/RUNB.
  /// </summary>
  internal static int[] Rle2Encode(ReadOnlySpan<byte> mtfData, int eobSymbol) {
    var result = new List<int>(mtfData.Length);
    var i = 0;

    while (i < mtfData.Length) {
      if (mtfData[i] == 0) {
        // Count consecutive zeros
        var zeroCount = 0;
        while (i < mtfData.Length && mtfData[i] == 0) {
          ++zeroCount;
          ++i;
        }

        // Encode zero count using bijective base-2 (RUNA/RUNB)
        // zeroCount is encoded as: each zero adds 1 to the count
        // RUNA at position k adds 2^k, RUNB at position k adds 2*2^k
        EncodeZeroRun(result, zeroCount);
      }
      else {
        // Non-zero MTF index, shifted by +1
        result.Add(mtfData[i] + 1);
        ++i;
      }
    }

    result.Add(eobSymbol);
    return result.ToArray();
  }

  /// <summary>
  /// RLE2: Decode RUNA/RUNB zero runs and shifted symbols.
  /// </summary>
  internal static byte[] Rle2Decode(ReadOnlySpan<int> symbols, int eobSymbol) {
    var result = new List<byte>();
    var i = 0;

    while (i < symbols.Length && symbols[i] != eobSymbol) {
      if (symbols[i] == Bzip2Constants.RunA || symbols[i] == Bzip2Constants.RunB) {
        // Decode zero run
        var zeroCount = 0;
        var power = 1;
        while (i < symbols.Length &&
           (symbols[i] == Bzip2Constants.RunA || symbols[i] == Bzip2Constants.RunB)) {
          if (symbols[i] == Bzip2Constants.RunA)
            zeroCount += power;
          else
            zeroCount += 2 * power;
          power *= 2;
          ++i;
        }

        for (var j = 0; j < zeroCount; ++j)
          result.Add(0);
      }
      else {
        // Non-zero MTF index (shifted by -1)
        result.Add((byte)(symbols[i] - 1));
        ++i;
      }
    }

    return result.ToArray();
  }

  private static void EncodeZeroRun(List<int> result, int count) {
    // Bijective base-2 encoding: RUNA adds 1*2^k, RUNB adds 2*2^k at each position k
    while (count > 0) {
      if (count % 2 == 1) {
        result.Add(Bzip2Constants.RunA);
        count = (count - 1) / 2;
      }
      else {
        result.Add(Bzip2Constants.RunB);
        count = (count - 2) / 2;
      }
    }
  }

  /// <summary>
  /// Bzip2 uses a variant of CRC-32 with byte-reversed bit order.
  /// </summary>
  private static uint Crc32Bzip2(ReadOnlySpan<byte> data) {
    // Bzip2 uses unreflected CRC-32 (big-endian)
    var crc = 0xFFFFFFFF;
    for (var i = 0; i < data.Length; ++i) {
      crc ^= (uint)data[i] << 24;
      for (var j = 0; j < 8; ++j) {
        if ((crc & 0x80000000) != 0)
          crc = (crc << 1) ^ 0x04C11DB7;
        else
          crc <<= 1;
      }
    }
    return crc ^ 0xFFFFFFFF;
  }
}
