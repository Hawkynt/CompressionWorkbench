using Compression.Core.BitIO;
using Compression.Core.Entropy.Ppmd;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Decompressor for RAR v3.x/v4.x archives (UnPack 2.9 algorithm).
/// Uses LZ77 + multi-table adaptive Huffman with repeated offsets and PPMd fallback.
/// 4 Huffman tables: Main(299) + Dist(60) + LowDist(17) + RepLen(28).
/// </summary>
public sealed class Rar3Decoder {
  private const int MainTableSize = 299;
  private const int DistTableSize = 60;
  private const int LowDistTableSize = 17;
  private const int RepLenTableSize = 28;
  private const int MaxCodeLength = 15;

  // Length table for new matches (symbol 263+ in main table, 36 slots)
  private static readonly int[] LenBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0, 0, 0, 0, 0, 0, 0, 0];
  private static readonly int[] LenBase = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768];

  // Standard filter CRCs for identification
  private const uint CrcE8 = 0xAD576887;       // E8/E9 x86 call/jump filter
  private const uint CrcE8E9 = 0x3CD7E57E;     // E8+E9 filter
  private const uint CrcDelta = 0x884DC8CF;     // Delta filter
  private const uint CrcAudio = 0x6859F14D;     // Audio prediction filter
  private const uint CrcRgb = 0xD8BC85E1;      // RGB image filter
  private const uint CrcItanium = 0x3EBD0990;   // IA-64 filter

  private int _windowSize;
  private int _windowMask;
  private byte[] _window = [];
  private int _windowPos;

  // Repeated offsets
  private readonly int[] _rep = [0, 0, 0, 0];

  // Pending filters to apply
  private readonly List<PendingFilter> _filters = [];
  private int _lastFilterId;

  /// <summary>Describes a pending post-decompression filter.</summary>
  private sealed class PendingFilter {
    public int BlockStart;
    public int BlockLength;
    public Rar3FilterType Type;
    public int Channels; // For delta/audio/RGB
    public int Width;    // For RGB
    public int PosR;     // For RGB
  }

  /// <summary>Known RAR3 filter types.</summary>
  private enum Rar3FilterType { None, E8, E8E9, Delta, Audio, Rgb, Itanium }

  // Previous code lengths for delta coding (4 tables)
  private readonly int[] _prevMainLens = new int[MainTableSize];
  private readonly int[] _prevDistLens = new int[DistTableSize];
  private readonly int[] _prevLowDistLens = new int[LowDistTableSize];
  private readonly int[] _prevRepLenLens = new int[RepLenTableSize];

  private bool _tablesRead;

  /// <summary>
  /// Decompresses RAR v3.x/v4.x data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="unpackedSize">The expected uncompressed size.</param>
  /// <param name="windowBits">Window size as power of 2 (15-22 for RAR3, up to 30 for RAR4). Default 22 (4MB).</param>
  /// <returns>The decompressed data.</returns>
  public byte[] Decompress(ReadOnlySpan<byte> compressed, int unpackedSize, int windowBits = 22) {
    this._windowSize = 1 << Math.Min(windowBits, 26); // Cap at 64MB for safety
    this._windowMask = this._windowSize - 1;
    if (this._window.Length != this._windowSize)
      this._window = new byte[this._windowSize];

    // Reset tables-read flag for new compressed block (tables are re-read per block)
    this._tablesRead = false;

    var output = new byte[unpackedSize];
    var outPos = 0;

    using var ms = new MemoryStream(compressed.ToArray());
    var bitReader = new BitBuffer<MsbBitOrder>(ms);

    while (outPos < unpackedSize) {
      // Check for PPMd mode or read tables
      if (!this._tablesRead) {
        var usePpm = bitReader.ReadBits(1) != 0;
        if (usePpm) {
          outPos = DecodePpmd(bitReader, output, outPos, unpackedSize);
          break; // PPMd decodes until end of data
        }

        ReadTables(bitReader);
        this._tablesRead = true;
      }

      var mainDecoder = BuildDecoder(this._prevMainLens, MainTableSize);
      var distDecoder = BuildDecoder(this._prevDistLens, DistTableSize);
      var lowDistDecoder = BuildDecoder(this._prevLowDistLens, LowDistTableSize);
      var repLenDecoder = BuildDecoder(this._prevRepLenLens, RepLenTableSize);

      // Decode block
      while (outPos < unpackedSize) {
        var sym = DecodeSymbol(bitReader, mainDecoder);

        if (sym < 256) {
          // Literal byte
          this._window[this._windowPos++ & this._windowMask] = (byte)sym;
          output[outPos++] = (byte)sym;
        } else if (sym == 256) {
          // End of block or new table signal
          var blockEnd = bitReader.ReadBits(1) != 0;
          if (blockEnd) {
            this._tablesRead = false;
            break; // Re-read tables
          }
          // Read and queue filter for post-decompression application
          ReadFilter(bitReader, outPos);
        } else if (sym == 257) {
          // End of data
          break;
        } else if (sym == 258) {
          // Repeat last match with length 2
          CopyMatch(output, ref outPos, this._rep[0], 2, unpackedSize);
        } else if (sym < 263) {
          // Repeat old distance with length from RepLen table (28 symbols)
          var repIdx = sym - 259;
          var dist = this._rep[repIdx];

          // Rotate distances
          for (var i = repIdx; i > 0; --i)
            this._rep[i] = this._rep[i - 1];
          this._rep[0] = dist;

          var lenSym = DecodeSymbol(bitReader, repLenDecoder);
          var length = LenBase[lenSym] + 2;
          if (LenBits[lenSym] > 0)
            length += (int)bitReader.ReadBits(LenBits[lenSym]);

          CopyMatch(output, ref outPos, dist, length, unpackedSize);
        } else {
          // sym 263-298: new distance + length
          var lenCode = sym - 263;
          var length = LenBase[lenCode] + 3;
          if (LenBits[lenCode] > 0)
            length += (int)bitReader.ReadBits(LenBits[lenCode]);

          var distSym = DecodeSymbol(bitReader, distDecoder);
          int distance;
          if (distSym < 4) {
            distance = distSym + 1;
          } else {
            // RAR3 distance: bits = slot/2 - 1, base = ((2 | (slot & 1)) << bits) + 1
            var bits = distSym / 2 - 1;
            distance = ((2 | (distSym & 1)) << bits) + 1;
            if (bits >= 4) {
              // High bits as raw, low 4 bits via LowDist Huffman table
              if (bits > 4)
                distance += (int)(bitReader.ReadBits(bits - 4) << 4);
              distance += DecodeSymbol(bitReader, lowDistDecoder);
            } else if (bits > 0) {
              distance += (int)bitReader.ReadBits(bits);
            }
          }

          // Update rep distances
          this._rep[3] = this._rep[2];
          this._rep[2] = this._rep[1];
          this._rep[1] = this._rep[0];
          this._rep[0] = distance;

          CopyMatch(output, ref outPos, distance, length, unpackedSize);
        }
      }
    }

    // Apply any pending filters
    ApplyFilters(output);

    return output;
  }

  private void ReadFilter(BitBuffer<MsbBitOrder> bitReader, int outPos) {
    // Read filter block address (relative to current output position)
    var blockStart = ReadVmNumber(bitReader);
    if ((blockStart & 0x80000000u) != 0) {
      blockStart = (int)((uint)blockStart & 0x7FFFFFFF);
      blockStart += outPos;
    } else
      blockStart += outPos;

    var blockLength = ReadVmNumber(bitReader);
    if (blockLength > 0x1000000) blockLength = 0x1000000; // Cap at 16MB

    var isNew = bitReader.ReadBits(1) != 0;
    int filterNum;

    if (isNew) {
      filterNum = ++_lastFilterId;
      var vmCodeSize = ReadVmNumber(bitReader);
      var vmCode = new byte[Math.Min(vmCodeSize, 0x10000)];
      for (var i = 0; i < vmCode.Length; ++i)
        vmCode[i] = (byte)bitReader.ReadBits(8);
      for (var i = vmCode.Length; i < vmCodeSize; ++i)
        bitReader.ReadBits(8);

      var crc = ComputeVmCrc(vmCode);
      var filter = new PendingFilter {
        BlockStart = blockStart,
        BlockLength = blockLength,
        Type = IdentifyFilter(crc)
      };

      if (bitReader.ReadBits(1) != 0) {
        var initMask = (int)bitReader.ReadBits(7);
        for (var i = 0; i < 7; ++i) {
          if ((initMask & (1 << i)) == 0) continue;
          var regVal = ReadVmNumber(bitReader);
          switch (i) {
            case 5: filter.Channels = regVal; filter.Width = regVal; break;
            case 6: filter.PosR = regVal; break;
          }
        }
      }
      _filters.Add(filter);
    } else {
      filterNum = ReadVmNumber(bitReader);
      var filter = new PendingFilter {
        BlockStart = blockStart,
        BlockLength = blockLength,
        Type = Rar3FilterType.E8E9
      };

      if (bitReader.ReadBits(1) != 0) {
        var initMask = (int)bitReader.ReadBits(7);
        for (var i = 0; i < 7; ++i) {
          if ((initMask & (1 << i)) == 0) continue;
          var regVal = ReadVmNumber(bitReader);
          switch (i) {
            case 5: filter.Channels = regVal; filter.Width = regVal; break;
            case 6: filter.PosR = regVal; break;
          }
        }
      }
      _filters.Add(filter);
    }
  }

  private void ApplyFilters(byte[] output) {
    foreach (var f in _filters) {
      if (f.BlockStart < 0 || f.BlockStart >= output.Length) continue;
      var len = Math.Min(f.BlockLength, output.Length - f.BlockStart);
      if (len <= 0) continue;

      switch (f.Type) {
        case Rar3FilterType.E8:
        case Rar3FilterType.E8E9:
          Rar3Filters.ApplyE8E9(output, f.BlockStart, len, f.BlockStart);
          break;
        case Rar3FilterType.Delta:
          if (f.Channels > 0)
            Rar3Filters.ApplyDelta(output, f.BlockStart, len, f.Channels);
          break;
        case Rar3FilterType.Audio:
          if (f.Channels > 0)
            Rar3Filters.ApplyAudio(output, f.BlockStart, len, f.Channels);
          break;
        case Rar3FilterType.Rgb:
          if (f.Width > 0)
            Rar3Filters.ApplyRgb(output, f.BlockStart, len, f.Width, f.PosR);
          break;
        case Rar3FilterType.Itanium:
          Rar3Filters.ApplyItanium(output, f.BlockStart, len);
          break;
      }
    }
    _filters.Clear();
  }

  private static Rar3FilterType IdentifyFilter(uint crc) => crc switch {
    CrcE8 => Rar3FilterType.E8,
    CrcE8E9 => Rar3FilterType.E8E9,
    CrcDelta => Rar3FilterType.Delta,
    CrcAudio => Rar3FilterType.Audio,
    CrcRgb => Rar3FilterType.Rgb,
    CrcItanium => Rar3FilterType.Itanium,
    _ => Rar3FilterType.None
  };

  private static int ReadVmNumber(BitBuffer<MsbBitOrder> bitReader) {
    var firstByte = (int)bitReader.ReadBits(8);
    if ((firstByte & 0x80) == 0)
      return firstByte;

    if ((firstByte & 0x40) == 0)
      return ((firstByte & 0x3F) << 8) | (int)bitReader.ReadBits(8);

    if ((firstByte & 0x20) == 0) {
      var b1 = (int)bitReader.ReadBits(8);
      var b2 = (int)bitReader.ReadBits(8);
      return ((firstByte & 0x1F) << 16) | (b1 << 8) | b2;
    }

    if ((firstByte & 0x10) == 0) {
      var b1 = (int)bitReader.ReadBits(8);
      var b2 = (int)bitReader.ReadBits(8);
      var b3 = (int)bitReader.ReadBits(8);
      return ((firstByte & 0x0F) << 24) | (b1 << 16) | (b2 << 8) | b3;
    }

    var v1 = (int)bitReader.ReadBits(8);
    var v2 = (int)bitReader.ReadBits(8);
    var v3 = (int)bitReader.ReadBits(8);
    var v4 = (int)bitReader.ReadBits(8);
    return (v1 << 24) | (v2 << 16) | (v3 << 8) | v4;
  }

  private static uint ComputeVmCrc(byte[] data) {
    var crc = 0xFFFFFFFF;
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; ++i)
        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
    }
    return crc ^ 0xFFFFFFFF;
  }

  private static int DecodePpmd(BitBuffer<MsbBitOrder> bitReader, byte[] output, int outPos, int limit) {
    var ppmFlags = (int)bitReader.ReadBits(7);
    var order = (ppmFlags & 0x1F) + 1;
    var memIdx = ((ppmFlags >> 5) & 0x03) | ((int)bitReader.ReadBits(5) << 2);
    var memSize = (memIdx + 1) << 20;

    var model = new PpmdModelH(order, memSize);

    var remainingBytes = new MemoryStream();
    try {
      while (true) {
        var b = (byte)bitReader.ReadBits(8);
        remainingBytes.WriteByte(b);
      }
    } catch (EndOfStreamException) { }
    remainingBytes.Position = 0;

    var rangeDecoder = new PpmdRangeDecoder(remainingBytes);
    while (outPos < limit) {
      try {
        var sym = model.DecodeSymbol(rangeDecoder);
        output[outPos++] = sym;
      } catch (EndOfStreamException) {
        break;
      }
    }
    return outPos;
  }

  private void CopyMatch(byte[] output, ref int outPos, int distance, int length, int limit) {
    for (var i = 0; i < length && outPos < limit; ++i) {
      var srcPos = (this._windowPos - distance) & this._windowMask;
      var b = this._window[srcPos];
      this._window[this._windowPos++ & this._windowMask] = b;
      output[outPos++] = b;
    }
  }

  private void ReadTables(BitBuffer<MsbBitOrder> bitReader) {
    // Read code length code lengths (20 symbols, 4 bits each)
    var clLens = new int[20];
    for (var i = 0; i < 20; ++i)
      clLens[i] = (int)bitReader.ReadBits(4);

    var clDecoder = BuildDecoder(clLens, 20);

    // Read 4 tables in order: Main(299), Dist(60), LowDist(17), RepLen(28)
    ReadTableLengths(bitReader, clDecoder, this._prevMainLens, MainTableSize);
    ReadTableLengths(bitReader, clDecoder, this._prevDistLens, DistTableSize);
    ReadTableLengths(bitReader, clDecoder, this._prevLowDistLens, LowDistTableSize);
    ReadTableLengths(bitReader, clDecoder, this._prevRepLenLens, RepLenTableSize);
  }

  private static void ReadTableLengths(BitBuffer<MsbBitOrder> bitReader,
      (int[] Symbols, int[] Lengths, int MaxBits) clDecoder,
      int[] lengths, int count) {
    var i = 0;
    while (i < count) {
      var sym = DecodeSymbol(bitReader, clDecoder);
      if (sym < 16) {
        lengths[i] = (lengths[i] + sym) & 0x0F;
        ++i;
      } else if (sym == 16) {
        if (i == 0)
          throw new InvalidDataException("RAR3 table repeat at start.");
        var repeat = 3 + (int)bitReader.ReadBits(2);
        var prev = lengths[i - 1];
        while (repeat-- > 0 && i < count)
          lengths[i++] = prev;
      } else if (sym == 17) {
        var repeat = 3 + (int)bitReader.ReadBits(3);
        while (repeat-- > 0 && i < count)
          lengths[i++] = 0;
      } else if (sym == 18) {
        var repeat = 11 + (int)bitReader.ReadBits(7);
        while (repeat-- > 0 && i < count)
          lengths[i++] = 0;
      }
    }
  }

  private static (int[] Symbols, int[] Lengths, int MaxBits) BuildDecoder(int[] codeLengths, int numSymbols) {
    var maxBits = 0;
    for (var i = 0; i < numSymbols; ++i)
      maxBits = Math.Max(maxBits, codeLengths[i]);

    if (maxBits == 0) maxBits = 1;
    if (maxBits > 15) maxBits = 15;

    var tableSize = 1 << maxBits;
    var symbols = new int[tableSize];
    var lengths = new int[tableSize];

    var blCount = new int[MaxCodeLength + 1];
    for (var i = 0; i < numSymbols; ++i)
      if (codeLengths[i] > 0)
        ++blCount[codeLengths[i]];

    var nextCode = new int[MaxCodeLength + 1];
    var code = 0;
    for (var bits = 1; bits <= maxBits; ++bits) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    Array.Fill(symbols, -1);
    for (var sym = 0; sym < numSymbols; ++sym) {
      var len = codeLengths[sym];
      if (len <= 0 || len > maxBits) continue;
      var c = nextCode[len]++;
      var prefix = c << (maxBits - len);
      var count = 1 << (maxBits - len);
      for (var j = 0; j < count && prefix + j < tableSize; ++j) {
        symbols[prefix + j] = sym;
        lengths[prefix + j] = len;
      }
    }

    return (symbols, lengths, maxBits);
  }

  private static int DecodeSymbol(BitBuffer<MsbBitOrder> bitReader,
      (int[] Symbols, int[] Lengths, int MaxBits) decoder) {
    var peekBits = (int)bitReader.PeekBits(decoder.MaxBits);
    var sym = decoder.Symbols[peekBits];
    if (sym < 0)
      throw new InvalidDataException("Invalid Huffman code in RAR v3 data.");
    bitReader.DropBits(decoder.Lengths[peekBits]);
    return sym;
  }
}
