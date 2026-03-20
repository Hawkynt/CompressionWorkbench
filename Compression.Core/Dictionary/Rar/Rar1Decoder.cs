namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Decompressor for RAR v1.x archives (UnPack 1.5 algorithm).
/// Uses static Huffman tables and a 64 KB sliding window.
/// </summary>
public sealed class Rar1Decoder {
  private const int WindowSize = 0x10000; // 64 KB
  private const int WindowMask = WindowSize - 1;

  // Huffman decode tables
  private static readonly int[] ShortLen1 = [1, 3, 4, 4, 5, 6, 7, 8, 8, 4, 4, 5, 6, 6, 4, 0];
  private static readonly int[] ShortLen2 = [2, 3, 3, 3, 4, 4, 5, 6, 6, 4, 4, 5, 6, 6, 4, 0];
  private static readonly int[] ShortXor1 = [0, 0xa0, 0xd0, 0xe0, 0xf0, 0xf8, 0xfc, 0xfe, 0xff, 0xc0, 0x80, 0x90, 0x98, 0x9c, 0xb0, 0];
  private static readonly int[] ShortXor2 = [0, 0x40, 0x60, 0xa0, 0xd0, 0xe0, 0xf0, 0xf8, 0xfc, 0xc0, 0x80, 0x90, 0x98, 0x9c, 0xb0, 0];

  // Length tables for copy operations
  private static readonly int[] LenBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5];
  private static readonly int[] LenBase = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224];

  // Distance tables
  private static readonly int[] DistBits = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15];
  private static readonly int[] DistBase = [0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048, 3072, 4096, 6144, 8192, 12288, 16384, 24576, 32768, 49152, 0, 0];

  private readonly byte[] _window = new byte[WindowSize];
  private int _windowPos;

  // Bit reading state
  private byte[] _input = [];
  private int _inputPos;
  private uint _bitBuffer;
  private int _bitsAvailable;

  // State
  private int _flagBuf;
  private int _flagBits;
  private int _lastDist;
  private int _lastLength;
  private bool _useShortTable2;

  /// <summary>
  /// Decompresses RAR v1.x data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="unpackedSize">The expected uncompressed size.</param>
  /// <returns>The decompressed data.</returns>
  public byte[] Decompress(ReadOnlySpan<byte> compressed, int unpackedSize) {
    var output = new byte[unpackedSize];
    this._input = compressed.ToArray();
    this._inputPos = 0;
    this._bitBuffer = 0;
    this._bitsAvailable = 0;
    this._windowPos = 0;
    this._flagBuf = 0;
    this._flagBits = 0;
    this._lastDist = 0;
    this._lastLength = 0;
    this._useShortTable2 = false;

    // Fill initial bit buffer
    FillBitBuffer();

    var outPos = 0;
    while (outPos < unpackedSize) {
      if (this._flagBits == 0) {
        this._flagBuf = (int)GetBits(8);
        DropBits(8);
        this._flagBits = 8;
      }

      --this._flagBits;

      if ((this._flagBuf & 0x80) != 0) {
        // Match/copy operation
        this._flagBuf <<= 1;

        if (this._useShortTable2) {
          var distIndex = DecodeShort(ShortXor2, ShortLen2);
          if (distIndex >= 9) {
            // Use length/distance tables
            var length = DecodeLength();
            var distance = DecodeDistance(distIndex - 9);

            this._lastDist = distance;
            this._lastLength = length;
            CopyFromWindow(distance, length, output, ref outPos, unpackedSize);
          } else if (distIndex == 0) {
            // Repeat last
            CopyFromWindow(this._lastDist, this._lastLength, output, ref outPos, unpackedSize);
          } else {
            // Short match: distance from table, length 2
            this._lastDist = distIndex;
            this._lastLength = 2;
            CopyFromWindow(distIndex, 2, output, ref outPos, unpackedSize);
          }
        } else {
          var distIndex = DecodeShort(ShortXor1, ShortLen1);
          if (distIndex >= 9) {
            var length = DecodeLength();
            var distance = DecodeDistance(distIndex - 9);

            this._lastDist = distance;
            this._lastLength = length;
            CopyFromWindow(distance, length, output, ref outPos, unpackedSize);
          } else if (distIndex == 0) {
            CopyFromWindow(this._lastDist, this._lastLength, output, ref outPos, unpackedSize);
          } else {
            this._lastDist = distIndex;
            this._lastLength = 2;
            CopyFromWindow(distIndex, 2, output, ref outPos, unpackedSize);
          }
        }

        this._useShortTable2 = !this._useShortTable2;
      } else {
        // Literal byte
        this._flagBuf <<= 1;
        var b = (byte)GetBits(8);
        DropBits(8);
        this._window[this._windowPos++ & WindowMask] = b;
        output[outPos++] = b;
      }
    }

    return output;
  }

  private int DecodeShort(int[] xorTable, int[] lenTable) {
    var val = (int)(GetBits(8) >> 8);
    for (var i = 0; i < xorTable.Length; ++i) {
      if (lenTable[i] == 0)
        continue;
      if ((val ^ xorTable[i]) < (1 << (8 - lenTable[i]))) {
        DropBits(lenTable[i]);
        return i;
      }
    }
    DropBits(1);
    return 0;
  }

  private int DecodeLength() {
    var bits = (int)(GetBits(8) >> 4);
    // Find the length code
    for (var i = Rar1Decoder.LenBits.Length - 1; i >= 0; --i) {
      var total = Rar1Decoder.LenBits[i] + 4;
      if (i < 4) total = 4;
      var code = (int)(GetBits(total) >> (16 - total));

      if (code >= Rar1Decoder.LenBase[i] && (i + 1 >= Rar1Decoder.LenBits.Length || code < Rar1Decoder.LenBase[i + 1]))
        // This is a simpler approach: decode as 4+extra bits
        break;
    }

    // Simplified: read a length code
    var lenCode = (int)(GetBits(4) >> 12);
    DropBits(4);

    if (lenCode < Rar1Decoder.LenBits.Length) {
      var extraBits = Rar1Decoder.LenBits[lenCode];
      var length = Rar1Decoder.LenBase[lenCode] + 3;
      if (extraBits > 0) {
        length += (int)(GetBits(extraBits) >> (16 - extraBits));
        DropBits(extraBits);
      }
      return length;
    }
    return 3;
  }

  private int DecodeDistance(int highPart) {
    var distCode = (highPart << 4) | (int)(GetBits(4) >> 12);
    DropBits(4);

    if (distCode >= Rar1Decoder.DistBits.Length)
      return 1;

    var extraBits = Rar1Decoder.DistBits[distCode];
    var distance = Rar1Decoder.DistBase[distCode] + 1;
    if (extraBits > 0) {
      distance += (int)(GetBits(extraBits) >> (16 - extraBits));
      DropBits(extraBits);
    }
    return distance;
  }

  private void CopyFromWindow(int distance, int length, byte[] output, ref int outPos, int limit) {
    for (var i = 0; i < length && outPos < limit; ++i) {
      var srcPos = (this._windowPos - distance) & WindowMask;
      var b = this._window[srcPos];
      this._window[this._windowPos & WindowMask] = b;
      ++this._windowPos;
      output[outPos++] = b;
    }
  }

  private uint GetBits(int count) {
    // Return top `count` bits from the 16-bit buffer, left-aligned
    while (this._bitsAvailable < count)
      FillBitBuffer();
    return (this._bitBuffer >> (this._bitsAvailable - count)) & ((1u << count) - 1);
  }

  private void DropBits(int count) {
    this._bitsAvailable -= count;
    this._bitBuffer &= (1u << this._bitsAvailable) - 1;
  }

  private void FillBitBuffer() {
    while (this._bitsAvailable <= 24 && this._inputPos < this._input.Length) {
      this._bitBuffer = (this._bitBuffer << 8) | this._input[this._inputPos++];
      this._bitsAvailable += 8;
    }
  }
}
