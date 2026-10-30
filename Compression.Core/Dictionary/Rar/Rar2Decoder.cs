namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Decoder for the RAR 2.0 compression format (RAR 2.x archives, unpack versions 20 and 26):
/// LZ77 with three Huffman codes, and the multi-channel audio predictor.
/// </summary>
/// <remarks>
/// Implemented from <c>docs/RAR20-ON-DISK.md</c> (sourcing rung 2, written from reading unrar);
/// checked against archives WinRAR 2.90 wrote. One instance decodes one file, or the files
/// of a solid archive in order.
/// </remarks>
public sealed class Rar2Decoder {
  private const int MainCodeSize = 298;
  private const int DistanceCodeSize = 48;
  private const int RepeatCodeSize = 28;
  private const int AudioCodeSize = 257;
  private const int PrecodeSize = 19;
  private const int WindowSize = 1 << 22;
  private const int WindowMask = WindowSize - 1;

  private static readonly byte[] LengthBases = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224];
  private static readonly byte[] LengthBits = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5];
  private static readonly int[] DistanceBases = [
    0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048, 3072, 4096, 6144,
    8192, 12288, 16384, 24576, 32768, 49152, 65536, 98304, 131072, 196608, 262144, 327680, 393216, 458752, 524288,
    589824, 655360, 720896, 786432, 851968, 917504, 983040];
  private static readonly byte[] DistanceBits = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15,
    16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16];
  private static readonly byte[] ShortBases = [0, 4, 8, 16, 32, 64, 128, 192];
  private static readonly byte[] ShortBits = [2, 2, 3, 4, 5, 6, 6, 6];

  private readonly byte[] _window = new byte[WindowSize];
  private long _position;
  private long _fileEnd;

  private readonly byte[] _oldLengths = new byte[AudioCodeSize * 4];
  private bool _tablesRead;
  private bool _audioBlock;
  private int _channels = 1, _channel;
  private int _channelDelta;
  private Rar3Decoder.HuffmanTable? _mainCode, _distanceCode, _repeatCode;
  private readonly Rar3Decoder.HuffmanTable?[] _audioCodes = new Rar3Decoder.HuffmanTable?[4];
  private readonly AudioChannel[] _audio = [new(), new(), new(), new()];

  private readonly int[] _oldDistances = new int[4];
  private int _oldDistancePointer;
  private int _lastDistance, _lastLength;

  private sealed class AudioChannel {
    public int K1, K2, K3, K4, K5;
    public int D1, D2, D3, D4;
    public int LastDelta, LastByte;
    public uint ByteCount;
    public readonly uint[] Errors = new uint[11];
  }

  /// <summary>Decompresses the packed data of one file.</summary>
  /// <exception cref="InvalidDataException">The data is corrupt.</exception>
  public byte[] Decompress(ReadOnlySpan<byte> compressed, int unpackedSize) {
    var fileStart = this._fileEnd;
    var fileEnd = fileStart + unpackedSize;
    this._fileEnd = fileEnd;
    var output = new byte[unpackedSize];
    for (var p = fileStart; p < Math.Min(this._position, fileEnd); ++p) output[p - fileStart] = this._window[p & WindowMask];
    if (unpackedSize == 0 || this._position >= fileEnd) return output;

    var reader = new Rar3Decoder.BitReader(compressed.ToArray());
    if (!this._tablesRead) this.ReadTables(reader);

    while (this._position < fileEnd) {
      if (this._audioBlock) {
        var number = this._audioCodes[this._channel]!.Decode(reader);
        if (number == 256) {
          this.ReadTables(reader);
          continue;
        }
        this.Emit(this.DecodeAudio(number), output, fileStart, fileEnd);
        if (++this._channel == this._channels) this._channel = 0;
        continue;
      }

      var symbol = this._mainCode!.Decode(reader);
      if (symbol < 256) {
        this.Emit((byte)symbol, output, fileStart, fileEnd);
        continue;
      }
      if (symbol > 269) {
        var index = symbol - 270;
        var length = LengthBases[index] + 3 + reader.Bits(LengthBits[index]);
        var d = this._distanceCode!.Decode(reader);
        var distance = DistanceBases[d] + 1 + reader.Bits(DistanceBits[d]);
        if (distance >= 0x2000) {
          ++length;
          if (distance >= 0x40000) ++length;
        }
        this.Copy(length, distance, output, fileStart, fileEnd);
        continue;
      }
      if (symbol == 269) {
        this.ReadTables(reader);
        continue;
      }
      if (symbol == 256) {
        this.Copy(this._lastLength, this._lastDistance, output, fileStart, fileEnd);
        continue;
      }
      if (symbol < 261) {
        var distance = this._oldDistances[(this._oldDistancePointer - (symbol - 256)) & 3];
        var k = this._repeatCode!.Decode(reader);
        var length = LengthBases[k] + 2 + reader.Bits(LengthBits[k]);
        if (distance >= 0x101) {
          ++length;
          if (distance >= 0x2000) {
            ++length;
            if (distance >= 0x40000) ++length;
          }
        }
        this.Copy(length, distance, output, fileStart, fileEnd);
        continue;
      }
      var s = symbol - 261;
      this.Copy(2, ShortBases[s] + 1 + reader.Bits(ShortBits[s]), output, fileStart, fileEnd);
    }

    // A following solid file may need the tables announced right after this one.
    if (reader.Remaining >= 5 * 8) {
      try {
        if (this._audioBlock ? this._audioCodes[this._channel]!.Decode(reader) == 256 : this._mainCode!.Decode(reader) == 269)
          this.ReadTables(reader);
      } catch (InvalidDataException) {
        // padding
      }
    }
    return output;
  }

  private void Emit(byte value, byte[] output, long fileStart, long fileEnd) {
    var p = this._position++;
    this._window[p & WindowMask] = value;
    if (p >= fileStart && p < fileEnd) output[p - fileStart] = value;
  }

  private void Copy(int length, int distance, byte[] output, long fileStart, long fileEnd) {
    this._lastDistance = distance;
    this._oldDistances[this._oldDistancePointer] = distance;
    this._oldDistancePointer = (this._oldDistancePointer + 1) & 3;
    this._lastLength = length;
    for (var i = 0; i < length; ++i) {
      var p = this._position++;
      var b = this._window[(p - distance) & WindowMask];
      this._window[p & WindowMask] = b;
      if (p >= fileStart && p < fileEnd) output[p - fileStart] = b;
    }
  }

  private void ReadTables(Rar3Decoder.BitReader reader) {
    this._audioBlock = reader.Bits(1) != 0;
    if (reader.Bits(1) == 0) Array.Clear(this._oldLengths);
    int tableSize;
    if (this._audioBlock) {
      this._channels = reader.Bits(2) + 1;
      if (this._channel >= this._channels) this._channel = 0;
      tableSize = AudioCodeSize * this._channels;
    } else
      tableSize = MainCodeSize + DistanceCodeSize + RepeatCodeSize;

    Span<byte> precodeLengths = stackalloc byte[PrecodeSize];
    for (var i = 0; i < PrecodeSize; ++i) precodeLengths[i] = (byte)reader.Bits(4);
    var precode = new Rar3Decoder.HuffmanTable(precodeLengths);

    var table = new byte[tableSize];
    for (var i = 0; i < tableSize;) {
      var number = precode.Decode(reader);
      if (number < 16) {
        table[i] = (byte)((number + this._oldLengths[i]) & 0xF);
        ++i;
      } else if (number == 16) {
        if (i == 0) throw new InvalidDataException("RAR2: length repeat at the start of the tables.");
        var n = reader.Bits(2) + 3;
        for (; n > 0 && i < tableSize; --n, ++i) table[i] = table[i - 1];
      } else {
        var n = number == 17 ? reader.Bits(3) + 3 : reader.Bits(7) + 11;
        for (; n > 0 && i < tableSize; --n) table[i++] = 0;
      }
    }
    this._tablesRead = true;

    var t = table.AsSpan();
    if (this._audioBlock)
      for (var c = 0; c < this._channels; ++c) this._audioCodes[c] = new(t.Slice(c * AudioCodeSize, AudioCodeSize));
    else {
      this._mainCode = new(t[..MainCodeSize]);
      this._distanceCode = new(t.Slice(MainCodeSize, DistanceCodeSize));
      this._repeatCode = new(t.Slice(MainCodeSize + DistanceCodeSize, RepeatCodeSize));
    }
    t.CopyTo(this._oldLengths);
  }

  private byte DecodeAudio(int delta) {
    var v = this._audio[this._channel];
    ++v.ByteCount;
    v.D4 = v.D3;
    v.D3 = v.D2;
    v.D2 = v.LastDelta - v.D1;
    v.D1 = v.LastDelta;
    var predicted = ((8 * v.LastByte + v.K1 * v.D1 + v.K2 * v.D2 + v.K3 * v.D3 + v.K4 * v.D4 + v.K5 * this._channelDelta) >> 3) & 0xFF;
    var value = (byte)(predicted - delta);

    var e = (sbyte)delta << 3;
    var cd = this._channelDelta;
    v.Errors[0] += (uint)Math.Abs(e);
    v.Errors[1] += (uint)Math.Abs(e - v.D1);
    v.Errors[2] += (uint)Math.Abs(e + v.D1);
    v.Errors[3] += (uint)Math.Abs(e - v.D2);
    v.Errors[4] += (uint)Math.Abs(e + v.D2);
    v.Errors[5] += (uint)Math.Abs(e - v.D3);
    v.Errors[6] += (uint)Math.Abs(e + v.D3);
    v.Errors[7] += (uint)Math.Abs(e - v.D4);
    v.Errors[8] += (uint)Math.Abs(e + v.D4);
    v.Errors[9] += (uint)Math.Abs(e - cd);
    v.Errors[10] += (uint)Math.Abs(e + cd);

    this._channelDelta = v.LastDelta = (sbyte)(value - v.LastByte);
    v.LastByte = value;

    if ((v.ByteCount & 0x1F) == 0) {
      uint min = v.Errors[0];
      var best = 0;
      v.Errors[0] = 0;
      for (var i = 1; i < v.Errors.Length; ++i) {
        if (v.Errors[i] < min) {
          min = v.Errors[i];
          best = i;
        }
        v.Errors[i] = 0;
      }
      switch (best) {
        case 1: if (v.K1 >= -16) --v.K1; break;
        case 2: if (v.K1 < 16) ++v.K1; break;
        case 3: if (v.K2 >= -16) --v.K2; break;
        case 4: if (v.K2 < 16) ++v.K2; break;
        case 5: if (v.K3 >= -16) --v.K3; break;
        case 6: if (v.K3 < 16) ++v.K3; break;
        case 7: if (v.K4 >= -16) --v.K4; break;
        case 8: if (v.K4 < 16) ++v.K4; break;
        case 9: if (v.K5 >= -16) --v.K5; break;
        case 10: if (v.K5 < 16) ++v.K5; break;
      }
    }
    return value;
  }
}
