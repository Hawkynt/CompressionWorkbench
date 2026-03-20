using Compression.Core.Transforms;

namespace Compression.Core.Dictionary.Ace;

/// <summary>
/// Decodes ACE 1.0 and 2.0 compressed data (LZ77 + dual Huffman trees).
/// Instance-based: the sliding window and repeat offsets persist across calls for solid archive support.
/// ACE 2.0 adds blocked sub-modes: LZ77, EXE (E8/E9), DELTA, SOUND, PIC.
/// </summary>
public sealed class AceDecoder {
  private const int MaxCodeLength = 16;

  private const int SubModeLz77 = 0;
  private const int SubModeExe = 1;
  private const int SubModeDelta = 2;
  private const int SubModeSound = 3;
  private const int SubModePic = 4;

  private readonly byte[] _window;
  private readonly int _windowMask;
  private readonly int _dictBits;
  private int _windowPos;
  private readonly int[] _repOffsets;

  /// <summary>
  /// Initializes a new <see cref="AceDecoder"/> with the specified dictionary size.
  /// </summary>
  /// <param name="dictBits">Dictionary size in bits (10-22).</param>
  public AceDecoder(int dictBits = AceConstants.DefaultDictBits) {
    this._dictBits = dictBits;
    int dictSize = 1 << dictBits;
    this._window = new byte[dictSize];
    this._windowMask = dictSize - 1;
    this._repOffsets = new int[AceConstants.NumRepOffsets];
    Array.Fill(this._repOffsets, 1);
  }

  /// <summary>
  /// Decompresses ACE data, preserving window state for subsequent solid calls.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected uncompressed size.</param>
  /// <param name="compressionType">1 for ACE 1.0, 2 for ACE 2.0.</param>
  /// <returns>The decompressed data.</returns>
  public byte[] Decode(byte[] compressed, int originalSize, int compressionType = AceConstants.CompAce10) {
    var bits = new AceBitReader(compressed);
    var output = new byte[originalSize];
    int outPos = 0;

    bool isAce20 = compressionType == AceConstants.CompAce20;
    int currentSubMode = SubModeLz77;
    int segmentStart = 0;

    while (outPos < originalSize) {
      // Read Huffman trees for this block
      int[] mainTree = ReadHuffmanTree(bits, AceConstants.MainSymbols);
      int[] lenTree = ReadHuffmanTree(bits, AceConstants.LenSymbols);

      // Decode symbols until end-of-block or output full
      while (outPos < originalSize) {
        int sym = DecodeSymbol(bits, mainTree);

        if (sym < 256) {
          byte b = (byte)sym;
          output[outPos++] = b;
          this._window[this._windowPos] = b;
          this._windowPos = (this._windowPos + 1) & this._windowMask;
        }
        else if (sym == AceConstants.SymbolEndOfBlock) {
          break;
        }
        else if (isAce20 && sym == AceConstants.SymbolModeSwitch) {
          ApplyInverseTransform(output, segmentStart, outPos, currentSubMode);
          int newMode = (int)bits.ReadBits(3);
          currentSubMode = newMode;
          segmentStart = outPos;
        }
        else {
          // Match: symbol 257+ encodes length
          int lenIdx = sym - AceConstants.SymbolMatchBase;
          if (lenIdx < 0 || lenIdx >= AceConstants.LengthBase.Length)
            throw new InvalidDataException($"Invalid ACE match symbol: {sym}");

          int length = AceConstants.LengthBase[lenIdx];
          int extra = AceConstants.LengthExtra[lenIdx];
          if (extra > 0)
            length += (int)bits.ReadBits(extra);

          int distance = ReadDistance(bits, this._dictBits, this._repOffsets);
          ShiftRepOffsets(this._repOffsets, distance);

          // Copy match from window
          int srcPos = (this._windowPos - distance + this._window.Length) & this._windowMask;
          for (int j = 0; j < length && outPos < originalSize; ++j) {
            byte b = this._window[srcPos];
            output[outPos++] = b;
            this._window[this._windowPos] = b;
            this._windowPos = (this._windowPos + 1) & this._windowMask;
            srcPos = (srcPos + 1) & this._windowMask;
          }
        }
      }
    }

    // Apply inverse transform to the final segment
    if (isAce20)
      ApplyInverseTransform(output, segmentStart, outPos, currentSubMode);

    return output;
  }

  /// <summary>
  /// Static convenience method for non-solid decoding (creates a fresh decoder).
  /// </summary>
  public static byte[] DecodeBlock(byte[] compressed, int originalSize,
      int dictBits = AceConstants.DefaultDictBits, int compressionType = AceConstants.CompAce10) {
    var decoder = new AceDecoder(dictBits);
    return decoder.Decode(compressed, originalSize, compressionType);
  }

  private static void ApplyInverseTransform(byte[] output, int start, int end, int subMode) {
    int length = end - start;
    if (length <= 0)
      return;

    switch (subMode) {
      case SubModeLz77:
        break;
      case SubModeExe:
        var exeSegment = BcjFilter.DecodeX86(output.AsSpan(start, length), start);
        exeSegment.AsSpan().CopyTo(output.AsSpan(start, length));
        break;
      case SubModeDelta:
        var deltaSegment = DeltaFilter.Decode(output.AsSpan(start, length));
        deltaSegment.AsSpan().CopyTo(output.AsSpan(start, length));
        break;
      case SubModeSound:
        var soundResult = AceSoundFilter.Decode(output.AsSpan(start, length));
        soundResult.AsSpan().CopyTo(output.AsSpan(start, soundResult.Length));
        break;
      case SubModePic:
        var picResult = AcePicFilter.Decode(output.AsSpan(start, length));
        picResult.AsSpan().CopyTo(output.AsSpan(start, picResult.Length));
        break;
    }
  }

  private static int ReadDistance(AceBitReader bits, int dictBits, int[] repOffsets) {
    uint mode = bits.ReadBits(2);

    if (mode < 4 && mode > 0)
      return repOffsets[(int)mode - 1];

    if (mode == 0)
      return (int)bits.ReadBits(dictBits) + 1;

    return (int)bits.ReadBits(dictBits) + 1;
  }

  private static void ShiftRepOffsets(int[] offsets, int newOffset) {
    for (int i = offsets.Length - 1; i > 0; --i)
      offsets[i] = offsets[i - 1];
    offsets[0] = newOffset;
  }

  private static int[] ReadHuffmanTree(AceBitReader bits, int numSymbols) {
    int numCodes = (int)bits.ReadBits(9);

    if (numCodes == 0) {
      int sym = (int)bits.ReadBits(9);
      var table = new int[1 << MaxCodeLength];
      Array.Fill(table, sym | (0 << 16));
      return table;
    }

    var preLengths = new int[numCodes];
    for (int i = 0; i < numCodes; ++i)
      preLengths[i] = (int)bits.ReadBits(4);

    int[] preTree = BuildDecodeTable(preLengths, numCodes, MaxCodeLength);

    var codeLengths = new int[numSymbols];
    int idx = 0;
    while (idx < numSymbols) {
      int code = DecodeSymbol(bits, preTree);

      if (code < 16) {
        codeLengths[idx++] = code;
      }
      else if (code == 16) {
        int count = (int)bits.ReadBits(2) + 3;
        int prev = idx > 0 ? codeLengths[idx - 1] : 0;
        for (int j = 0; j < count && idx < numSymbols; ++j)
          codeLengths[idx++] = prev;
      }
      else if (code == 17) {
        int count = (int)bits.ReadBits(3) + 3;
        idx += count;
      }
      else if (code == 18) {
        int count = (int)bits.ReadBits(7) + 11;
        idx += count;
      }
    }

    return BuildDecodeTable(codeLengths, numSymbols, MaxCodeLength);
  }

  private static int DecodeSymbol(AceBitReader bits, int[] table) {
    int tableBits = 0;
    int size = table.Length;
    while ((1 << tableBits) < size) ++tableBits;

    uint peek = bits.PeekBits(tableBits);
    int entry = table[(int)peek];
    int symbol = entry & 0xFFFF;
    int codeLen = entry >> 16;
    if (codeLen > 0)
      bits.DropBits(codeLen);
    return symbol;
  }

  private static int[] BuildDecodeTable(int[] codeLengths, int numSymbols, int maxBits) {
    int maxLen = 0;
    for (int i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > maxLen) maxLen = codeLengths[i];
    if (maxLen == 0) maxLen = 1;
    maxLen = Math.Min(maxLen, maxBits);

    int tableSize = 1 << maxLen;
    var table = new int[tableSize];
    Array.Fill(table, -1);

    var blCount = new int[maxLen + 1];
    for (int i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > 0 && codeLengths[i] <= maxLen)
        ++blCount[codeLengths[i]];

    var nextCode = new int[maxLen + 1];
    int code = 0;
    for (int b = 1; b <= maxLen; ++b) {
      code = (code + blCount[b - 1]) << 1;
      nextCode[b] = code;
    }

    for (int sym = 0; sym < numSymbols; ++sym) {
      int len = codeLengths[sym];
      if (len == 0 || len > maxLen) continue;

      int c = nextCode[len]++;
      int fill = 1 << (maxLen - len);
      int packedValue = sym | (len << 16);
      for (int j = 0; j < fill; ++j)
        table[(c << (maxLen - len)) + j] = packedValue;
    }

    for (int i = 0; i < tableSize; ++i)
      if (table[i] == -1)
        table[i] = 0;

    return table;
  }
}
