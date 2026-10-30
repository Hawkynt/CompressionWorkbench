namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Decompressor for RAR 2.0 packed data (unpack version 20 and 26): LZSS over a window of up to
/// 1 MB with three transmitted Huffman tables, or the multimedia mode that predicts up to four
/// interleaved audio channels.
/// </summary>
/// <remarks>
/// Converted from XADRAR20Handle.m and RARAudioDecoder.c of The Unarchiver's XADMaster
/// (LGPL-2.1-or-later), see THIRD-PARTY-NOTICE.RarLegacy.txt. One instance decodes one solid
/// stream: call <see cref="Decompress"/> once per file, in archive order, on the same instance for
/// the files of a solid archive and on a new instance for a file that does not continue one.
/// </remarks>
public sealed class Rar2Decoder {
  private const int WindowSize = 0x100000;
  private const int WindowMask = Rar2Decoder.WindowSize - 1;
  private const int MainSymbols = 298;
  private const int OffsetSymbols = 48;
  private const int LengthSymbols = 28;
  private const int AudioSymbols = 257;

  private static readonly int[] LengthBases = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224];
  private static readonly int[] LengthBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5];

  private static readonly int[] OffsetBases = [
    0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048, 3072, 4096, 6144,
    8192, 12288, 16384, 24576, 32768, 49152, 65536, 98304, 131072, 196608, 262144, 327680, 393216, 458752, 524288,
    589824, 655360, 720896, 786432, 851968, 917504, 983040,
  ];

  private static readonly int[] OffsetBits = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16,
    16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
  ];

  private static readonly int[] ShortBases = [0, 4, 8, 16, 32, 64, 128, 192];
  private static readonly int[] ShortBits = [2, 2, 3, 4, 5, 6, 6, 6];

  private readonly byte[] _window = new byte[Rar2Decoder.WindowSize];
  private long _position;

  private RarLegacyBitReader _input = new([]);
  private byte[] _output = [];
  private int _outputPos;
  private bool _tablesRead;

  private RarLegacyPrefixCode? _mainCode, _offsetCode, _lengthCode;
  private readonly RarLegacyPrefixCode?[] _audioCodes = new RarLegacyPrefixCode?[4];
  private readonly int[] _lengthTable = new int[4 * Rar2Decoder.AudioSymbols];

  private int _lastOffset, _lastLength;
  private readonly int[] _oldOffsets = new int[4];
  private int _oldOffsetIndex;

  private bool _audioBlock;
  private int _channel, _channelDelta, _channels;
  private readonly AudioState[] _audio = [new(), new(), new(), new()];

  /// <summary>
  /// Decodes the next file of the stream.
  /// </summary>
  /// <param name="compressed">The file's packed data.</param>
  /// <param name="unpackedSize">The file's unpacked size.</param>
  /// <returns>The unpacked bytes.</returns>
  public byte[] Decompress(ReadOnlySpan<byte> compressed, int unpackedSize) {
    ArgumentOutOfRangeException.ThrowIfNegative(unpackedSize);
    this._input = new(compressed.ToArray());
    this._output = new byte[unpackedSize];
    this._outputPos = 0;

    // The tables travel once per solid stream, ahead of the first file's data.
    if (!this._tablesRead && unpackedSize > 0) {
      this.ReadTables();
      this._tablesRead = true;
    }

    while (this._outputPos < unpackedSize) {
      if (this._audioBlock) {
        var symbol = this._audioCodes[this._channel]!.Decode(this._input);
        if (symbol == 256) {
          this.ReadTables();
          continue;
        }

        var value = this._audio[this._channel].Decode(ref this._channelDelta, symbol);
        if (++this._channel == this._channels)
          this._channel = 0;

        this.EmitByte((byte)value);
        continue;
      }

      var main = this._mainCode!.Decode(this._input);
      int offset, length;
      switch (main) {
        case < 256:
          this.EmitByte((byte)main);
          continue;
        case 256:
          offset = this._lastOffset;
          length = this._lastLength;
          break;
        case <= 260: {
          offset = this._oldOffsets[(this._oldOffsetIndex - (main - 256)) & 3];
          var lengthSymbol = this._lengthCode!.Decode(this._input);
          length = Rar2Decoder.LengthBases[lengthSymbol] + 2 + this._input.ReadBits(Rar2Decoder.LengthBits[lengthSymbol]);
          if (offset >= 0x40000)
            ++length;
          if (offset >= 0x2000)
            ++length;
          if (offset >= 0x101)
            ++length;
          break;
        }
        case <= 268:
          offset = Rar2Decoder.ShortBases[main - 261] + 1 + this._input.ReadBits(Rar2Decoder.ShortBits[main - 261]);
          length = 2;
          break;
        case 269:
          this.ReadTables();
          continue;
        default: {
          length = Rar2Decoder.LengthBases[main - 270] + 3 + this._input.ReadBits(Rar2Decoder.LengthBits[main - 270]);
          var offsetSymbol = this._offsetCode!.Decode(this._input);
          offset = Rar2Decoder.OffsetBases[offsetSymbol] + 1 + this._input.ReadBits(Rar2Decoder.OffsetBits[offsetSymbol]);
          if (offset >= 0x40000)
            ++length;
          if (offset >= 0x2000)
            ++length;
          break;
        }
      }

      this._lastOffset = this._oldOffsets[this._oldOffsetIndex++ & 3] = offset;
      this._lastLength = length;
      this.EmitMatch(offset, length);
    }

    return this._output;
  }

  private void ReadTables() {
    this._audioBlock = this._input.ReadBit();
    if (!this._input.ReadBit())
      Array.Clear(this._lengthTable);

    int count;
    if (this._audioBlock) {
      this._channels = this._input.ReadBits(2) + 1;
      count = this._channels * Rar2Decoder.AudioSymbols;
      if (this._channel >= this._channels)
        this._channel = 0;
    } else
      count = Rar2Decoder.MainSymbols + Rar2Decoder.OffsetSymbols + Rar2Decoder.LengthSymbols;

    Span<int> preLengths = stackalloc int[19];
    for (var i = 0; i < preLengths.Length; ++i)
      preLengths[i] = this._input.ReadBits(4);

    var preCode = RarLegacyPrefixCode.FromLengths(preLengths);
    var table = this._lengthTable;
    for (var i = 0; i < count;) {
      var value = preCode.Decode(this._input);
      switch (value) {
        case < 16:
          table[i] = (table[i] + value) & 0x0f;
          ++i;
          break;
        case 16: {
          if (i == 0)
            throw new InvalidDataException("RAR 2.0 table repeats a length before the first one.");

          var run = this._input.ReadBits(2) + 3;
          for (var j = 0; j < run && i < count; ++j, ++i)
            table[i] = table[i - 1];
          break;
        }
        default: {
          var run = value == 17 ? this._input.ReadBits(3) + 3 : this._input.ReadBits(7) + 11;
          for (var j = 0; j < run && i < count; ++j)
            table[i++] = 0;
          break;
        }
      }
    }

    if (this._audioBlock) {
      for (var i = 0; i < this._channels; ++i)
        this._audioCodes[i] = RarLegacyPrefixCode.FromLengths(table.AsSpan(i * Rar2Decoder.AudioSymbols, Rar2Decoder.AudioSymbols));
    } else {
      this._mainCode = RarLegacyPrefixCode.FromLengths(table.AsSpan(0, Rar2Decoder.MainSymbols));
      this._offsetCode = RarLegacyPrefixCode.FromLengths(table.AsSpan(Rar2Decoder.MainSymbols, Rar2Decoder.OffsetSymbols));
      this._lengthCode = RarLegacyPrefixCode.FromLengths(table.AsSpan(Rar2Decoder.MainSymbols + Rar2Decoder.OffsetSymbols, Rar2Decoder.LengthSymbols));
    }
  }

  private void EmitByte(byte value) {
    this._window[(int)(this._position++ & Rar2Decoder.WindowMask)] = value;
    this._output[this._outputPos++] = value;
  }

  private void EmitMatch(int offset, int length) {
    var end = Math.Min(this._output.Length, this._outputPos + length);
    while (this._outputPos < end)
      this.EmitByte(this._window[(int)((this._position - offset) & Rar2Decoder.WindowMask)]);
  }

  /// <summary>The adaptive linear predictor of one multimedia channel.</summary>
  private sealed class AudioState {
    private int _weight1, _weight2, _weight3, _weight4, _weight5;
    private int _delta1, _delta2, _delta3, _delta4;
    private int _lastDelta;
    private readonly int[] _error = new int[11];
    private int _count;
    private int _lastByte;

    /// <summary>Turns the coded prediction error <paramref name="delta"/> into the next sample byte.</summary>
    public int Decode(ref int channelDelta, int delta) {
      ++this._count;

      this._delta4 = this._delta3;
      this._delta3 = this._delta2;
      this._delta2 = this._lastDelta - this._delta1;
      this._delta1 = this._lastDelta;

      var predicted = ((8 * this._lastByte
                        + this._weight1 * this._delta1
                        + this._weight2 * this._delta2
                        + this._weight3 * this._delta3
                        + this._weight4 * this._delta4
                        + this._weight5 * channelDelta) >> 3) & 0xff;

      var value = (predicted - delta) & 0xff;

      var error = (sbyte)delta << 3;
      var errors = this._error;
      errors[0] += Math.Abs(error);
      errors[1] += Math.Abs(error - this._delta1);
      errors[2] += Math.Abs(error + this._delta1);
      errors[3] += Math.Abs(error - this._delta2);
      errors[4] += Math.Abs(error + this._delta2);
      errors[5] += Math.Abs(error - this._delta3);
      errors[6] += Math.Abs(error + this._delta3);
      errors[7] += Math.Abs(error - this._delta4);
      errors[8] += Math.Abs(error + this._delta4);
      errors[9] += Math.Abs(error - channelDelta);
      errors[10] += Math.Abs(error + channelDelta);

      channelDelta = this._lastDelta = (sbyte)(value - this._lastByte);
      this._lastByte = value;

      if ((this._count & 0x1f) != 0)
        return value;

      var minIndex = 0;
      for (var i = 1; i < errors.Length; ++i)
        if (errors[i] < errors[minIndex])
          minIndex = i;

      Array.Clear(errors);
      switch (minIndex) {
        case 1: if (this._weight1 >= -16) --this._weight1; break;
        case 2: if (this._weight1 < 16) ++this._weight1; break;
        case 3: if (this._weight2 >= -16) --this._weight2; break;
        case 4: if (this._weight2 < 16) ++this._weight2; break;
        case 5: if (this._weight3 >= -16) --this._weight3; break;
        case 6: if (this._weight3 < 16) ++this._weight3; break;
        case 7: if (this._weight4 >= -16) --this._weight4; break;
        case 8: if (this._weight4 < 16) ++this._weight4; break;
        case 9: if (this._weight5 >= -16) --this._weight5; break;
        case 10: if (this._weight5 < 16) ++this._weight5; break;
      }

      return value;
    }
  }
}
