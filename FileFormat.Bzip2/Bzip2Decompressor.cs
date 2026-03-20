using Compression.Core.BitIO;
using Compression.Core.Entropy.Huffman;
using Compression.Core.Transforms;

namespace FileFormat.Bzip2;

/// <summary>
/// Decompresses bzip2 block-sorted data.
/// </summary>
internal sealed class Bzip2Decompressor {
  private readonly BitBuffer<MsbBitOrder> _bits;
  private byte[]? _currentBlock;
  private int _currentBlockPos;
  private bool _finished;
  private uint _combinedCrc;

  /// <summary>
  /// Gets whether all blocks have been read.
  /// </summary>
  public bool IsFinished => this._finished;

  /// <summary>
  /// Gets the combined CRC of all decoded blocks.
  /// </summary>
  public uint CombinedCrc => this._combinedCrc;

  /// <summary>
  /// Initializes a new bzip2 decompressor.
  /// </summary>
  /// <param name="bits">The bit buffer for input (MSB-first).</param>
  public Bzip2Decompressor(BitBuffer<MsbBitOrder> bits) {
    this._bits = bits;
  }

  /// <summary>
  /// Reads decompressed data into the buffer.
  /// </summary>
  public int Read(byte[] buffer, int offset, int count) {
    var totalRead = 0;

    while (totalRead < count) {
      // If we have remaining data in current block, return it
      if (this._currentBlock != null && this._currentBlockPos < this._currentBlock.Length) {
        var available = this._currentBlock.Length - this._currentBlockPos;
        int toCopy = Math.Min(available, count - totalRead);
        this._currentBlock.AsSpan(this._currentBlockPos, toCopy).CopyTo(buffer.AsSpan(offset + totalRead));
        this._currentBlockPos += toCopy;
        totalRead += toCopy;
        continue;
      }

      if (this._finished)
        break;

      // Try to read the next block
      if (!ReadNextBlock())
        break;
    }

    return totalRead;
  }

  private bool ReadNextBlock() {
    // Read 48-bit magic
    var high = this._bits.ReadBits(24);
    var low = this._bits.ReadBits(24);
    long magic = ((long)high << 24) | low;

    if (magic == Bzip2Constants.BlockEndMagic) {
      // End of stream — read combined CRC
      var storedCombinedCrc = this._bits.ReadBits(32);
      if (storedCombinedCrc != this._combinedCrc)
        throw new InvalidDataException(
          $"Bzip2 combined CRC mismatch: expected 0x{storedCombinedCrc:X8}, computed 0x{this._combinedCrc:X8}.");
      this._finished = true;
      return false;
    }

    if (magic != Bzip2Constants.BlockHeaderMagic)
      throw new InvalidDataException($"Invalid bzip2 block magic: 0x{magic:X12}");

    // Read block CRC (32 bits)
    var blockCrc = this._bits.ReadBits(32);

    // Randomized flag (1 bit)
    var randomized = this._bits.ReadBits(1);
    if (randomized != 0)
      throw new InvalidDataException("Randomized bzip2 blocks are not supported.");

    // BWT original pointer (24 bits)
    int bwtIndex = (int)this._bits.ReadBits(24);

    // Read symbol bitmap
    bool[] symbolUsed = ReadSymbolBitmap();

    // Count symbols in use
    var numSymbolsInUse = 0;
    for (int i = 0; i < 256; ++i)
      if (symbolUsed[i]) ++numSymbolsInUse;

    int eobSymbol = numSymbolsInUse + 1;
    int alphaSize = eobSymbol + 1;

    // Number of trees (3 bits)
    int numTrees = (int)this._bits.ReadBits(3);

    // Number of selectors (15 bits)
    int numSelectors = (int)this._bits.ReadBits(15);

    // Read selectors (unary coded, MTF encoded)
    int[] selectorsMtf = new int[numSelectors];
    for (int i = 0; i < numSelectors; ++i) {
      var v = 0;
      while (this._bits.ReadBits(1) == 1)
        ++v;
      selectorsMtf[i] = v;
    }

    // Undo MTF on selectors
    int[] selectors = new int[numSelectors];
    byte[] selectorAlpha = new byte[numTrees];
    for (int i = 0; i < numTrees; ++i)
      selectorAlpha[i] = (byte)i;

    for (int i = 0; i < numSelectors; ++i) {
      int idx = selectorsMtf[i];
      byte val = selectorAlpha[idx];
      if (idx > 0) {
        selectorAlpha.AsSpan(0, idx).CopyTo(selectorAlpha.AsSpan(1));
        selectorAlpha[0] = val;
      }
      selectors[i] = val;
    }

    // Read Huffman tables (delta-encoded code lengths)
    var huffTables = new CanonicalHuffman[numTrees];
    for (int t = 0; t < numTrees; ++t) {
      int[] codeLens = new int[alphaSize];
      int currentLen = (int)this._bits.ReadBits(5);
      for (int s = 0; s < alphaSize; ++s) {
        while (this._bits.ReadBits(1) == 1) {
          if (this._bits.ReadBits(1) == 0)
            --currentLen;
          else
            ++currentLen;
        }
        codeLens[s] = currentLen;
      }
      huffTables[t] = new CanonicalHuffman(codeLens);
    }

    // Decode symbols
    var symbols = new List<int>();
    var groupIndex = 0;
    var symbolInGroup = 0;

    while (true) {
      if (symbolInGroup >= Bzip2Constants.GroupSize) {
        ++groupIndex;
        symbolInGroup = 0;
      }

      int tableIdx = selectors[Math.Min(groupIndex, numSelectors - 1)];
      int sym = huffTables[tableIdx].DecodeSymbol(this._bits);
      ++symbolInGroup;

      if (sym == eobSymbol)
        break;

      symbols.Add(sym);
    }

    // RLE2 decode
    byte[] mtfData = Bzip2Compressor.Rle2Decode(symbols.ToArray().AsSpan(), eobSymbol);

    // MTF decode
    // Build the MTF alphabet from symbols in use
    byte[] mtfAlphabet = new byte[numSymbolsInUse];
    var idx2 = 0;
    for (int i = 0; i < 256; ++i) {
      if (symbolUsed[i])
        mtfAlphabet[idx2++] = (byte)i;
    }

    // Apply MTF decode with the correct alphabet
    byte[] bwtData = MtfDecodeWithAlphabet(mtfData, mtfAlphabet);

    // BWT inverse
    byte[] rle1Data = BurrowsWheelerTransform.Inverse(bwtData, bwtIndex);

    // RLE1 decode
    byte[] blockData = Bzip2Compressor.Rle1Decode(rle1Data);

    // Verify block CRC
    uint computedCrc = Crc32Bzip2(blockData);
    if (computedCrc != blockCrc)
      throw new InvalidDataException(
        $"Bzip2 block CRC mismatch: expected 0x{blockCrc:X8}, computed 0x{computedCrc:X8}.");

    this._combinedCrc = ((this._combinedCrc << 1) | (this._combinedCrc >> 31)) ^ blockCrc;

    this._currentBlock = blockData;
    this._currentBlockPos = 0;
    return true;
  }

  private bool[] ReadSymbolBitmap() {
    bool[] used = new bool[256];
    var inUse16 = this._bits.ReadBits(16);

    for (int i = 0; i < 16; ++i) {
      if ((inUse16 & (1u << (15 - i))) != 0) {
        var bits = this._bits.ReadBits(16);
        for (int j = 0; j < 16; ++j) {
          if ((bits & (1u << (15 - j))) != 0)
            used[i * 16 + j] = true;
        }
      }
    }

    return used;
  }

  private static byte[] MtfDecodeWithAlphabet(ReadOnlySpan<byte> data, byte[] alphabet) {
    byte[] alpha = (byte[])alphabet.Clone();
    byte[] result = new byte[data.Length];

    for (int i = 0; i < data.Length; ++i) {
      int idx = data[i];
      byte b = alpha[idx];
      result[i] = b;

      if (idx > 0) {
        alpha.AsSpan(0, idx).CopyTo(alpha.AsSpan(1));
        alpha[0] = b;
      }
    }

    return result;
  }

  private static uint Crc32Bzip2(ReadOnlySpan<byte> data) {
    uint crc = 0xFFFFFFFF;
    for (int i = 0; i < data.Length; ++i) {
      crc ^= (uint)data[i] << 24;
      for (int j = 0; j < 8; ++j) {
        if ((crc & 0x80000000) != 0)
          crc = (crc << 1) ^ 0x04C11DB7;
        else
          crc <<= 1;
      }
    }
    return crc ^ 0xFFFFFFFF;
  }
}
