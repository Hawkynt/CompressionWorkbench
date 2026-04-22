#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.Flac;

/// <summary>
/// FLAC codec: decodes a FLAC stream to interleaved little-endian PCM, and reads
/// STREAMINFO metadata without a full decode. Moved out of <c>FileFormat.Flac</c> so
/// it can be reused by any container that carries FLAC payloads (Matroska audio
/// tracks, MP4 FLAC-in-ISOBMFF, …) without depending on the container descriptor.
/// <para>
/// Supports CONSTANT, VERBATIM, FIXED (orders 0–4) and LPC subframes with Rice-coded
/// residuals. Stereo channel-assignment modes (left/side, side/right, mid/side) are
/// decorrelated before output.
/// </para>
/// </summary>
public static class FlacCodec {

  /// <summary>
  /// Decompresses a FLAC stream into raw interleaved little-endian PCM on
  /// <paramref name="output"/>.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    byte[] data;
    if (input is MemoryStream ms && ms.TryGetBuffer(out var seg))
      data = seg.Array!;
    else {
      using var tmp = new MemoryStream();
      input.CopyTo(tmp);
      data = tmp.ToArray();
    }

    if (data.Length < 4) return;

    if (data[0] != 0x66 || data[1] != 0x4C || data[2] != 0x61 || data[3] != 0x43)
      throw new InvalidDataException("Not a FLAC stream: missing 'fLaC' magic.");

    var pos = 4;
    StreamInfo? streamInfo = null;
    while (pos + 4 <= data.Length) {
      var blockHeader = data[pos];
      var isLast = (blockHeader & 0x80) != 0;
      var blockType = blockHeader & 0x7F;
      var blockLength = (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
      pos += 4;

      if (blockType == 0 && blockLength >= 34 && pos + blockLength <= data.Length)
        streamInfo = ParseStreamInfo(data.AsSpan(pos, blockLength));

      pos += blockLength;
      if (isLast) break;
    }

    if (streamInfo == null)
      throw new InvalidDataException("FLAC stream missing STREAMINFO metadata block.");

    var bytesPerSample = (streamInfo.BitsPerSample + 7) / 8;

    while (pos + 2 <= data.Length) {
      if (data[pos] != 0xFF || (data[pos + 1] & 0xFE) != 0xF8) { pos++; continue; }

      var reader = new BitReader(data, pos);
      var sync14 = reader.ReadBits(14);
      if (sync14 != 0x3FFE) { pos++; continue; }

      reader.ReadBits(1);
      var blockingStrategy = reader.ReadBits(1);
      _ = blockingStrategy;
      var blockSizeCode = reader.ReadBits(4);
      var sampleRateCode = reader.ReadBits(4);
      var channelAssignment = reader.ReadBits(4);
      var sampleSizeCode = reader.ReadBits(3);
      reader.ReadBits(1);

      ReadUtf8Number(reader);

      var blockSize = blockSizeCode switch {
        0 => 0,
        1 => 192,
        2 => 576,
        3 => 1152,
        4 => 2304,
        5 => 4608,
        6 => (int)reader.ReadBits(8) + 1,
        7 => (int)reader.ReadBits(16) + 1,
        >= 8 and <= 15 => 256 << (int)(blockSizeCode - 8),
        _ => 0,
      };

      if (blockSize == 0) { pos++; continue; }

      switch (sampleRateCode) {
        case 12: reader.ReadBits(8); break;
        case 13: reader.ReadBits(16); break;
        case 14: reader.ReadBits(16); break;
      }

      var bps = sampleSizeCode switch {
        0 => streamInfo.BitsPerSample,
        1 => 8,
        2 => 12,
        3 => 0,
        4 => 16,
        5 => 20,
        6 => 24,
        7 => 32,
        _ => streamInfo.BitsPerSample,
      };

      reader.ReadBits(8); // CRC-8

      int channels;
      bool isMidSide = false, isLeftSide = false, isRightSide = false;
      if (channelAssignment <= 7) channels = (int)channelAssignment + 1;
      else if (channelAssignment == 8) { channels = 2; isLeftSide = true; }
      else if (channelAssignment == 9) { channels = 2; isRightSide = true; }
      else if (channelAssignment == 10) { channels = 2; isMidSide = true; }
      else { pos++; continue; }

      var samples = new int[channels][];
      var decodeFailed = false;
      for (var ch = 0; ch < channels; ++ch) {
        var subBps = bps;
        if (isLeftSide && ch == 1) subBps++;
        else if (isRightSide && ch == 0) subBps++;
        else if (isMidSide && ch == 1) subBps++;

        samples[ch] = DecodeSubframe(reader, blockSize, subBps);
        if (samples[ch].Length == 0) { decodeFailed = true; break; }
      }

      if (decodeFailed) { pos++; continue; }

      if (isLeftSide) {
        for (var i = 0; i < blockSize; ++i)
          samples[1][i] = samples[0][i] - samples[1][i];
      } else if (isRightSide) {
        for (var i = 0; i < blockSize; ++i)
          samples[0][i] = samples[0][i] + samples[1][i];
      } else if (isMidSide) {
        for (var i = 0; i < blockSize; ++i) {
          var mid = samples[0][i];
          var side = samples[1][i];
          mid = (mid << 1) | (side & 1);
          samples[0][i] = (mid + side) >> 1;
          samples[1][i] = (mid - side) >> 1;
        }
      }

      bytesPerSample = (bps + 7) / 8;
      var frameBuffer = new byte[blockSize * channels * bytesPerSample];
      var bufPos = 0;
      for (var i = 0; i < blockSize; ++i) {
        for (var ch = 0; ch < channels; ++ch) {
          var sample = samples[ch][i];
          switch (bytesPerSample) {
            case 1: frameBuffer[bufPos++] = (byte)(sample + 128); break;
            case 2:
              frameBuffer[bufPos++] = (byte)(sample & 0xFF);
              frameBuffer[bufPos++] = (byte)((sample >> 8) & 0xFF);
              break;
            case 3:
              frameBuffer[bufPos++] = (byte)(sample & 0xFF);
              frameBuffer[bufPos++] = (byte)((sample >> 8) & 0xFF);
              frameBuffer[bufPos++] = (byte)((sample >> 16) & 0xFF);
              break;
            case 4:
              frameBuffer[bufPos++] = (byte)(sample & 0xFF);
              frameBuffer[bufPos++] = (byte)((sample >> 8) & 0xFF);
              frameBuffer[bufPos++] = (byte)((sample >> 16) & 0xFF);
              frameBuffer[bufPos++] = (byte)((sample >> 24) & 0xFF);
              break;
          }
        }
      }

      output.Write(frameBuffer, 0, bufPos);
      reader.AlignToByte();
      reader.ReadBits(16);
      pos = reader.BytePosition;
    }
  }

  /// <summary>
  /// STREAMINFO fields callers need to drive channel-splitting / PCM-header construction.
  /// </summary>
  public readonly record struct AudioProperties(int SampleRate, int Channels, int BitsPerSample, long TotalSamples);

  /// <summary>
  /// Reads STREAMINFO without a full decode; used by archive descriptors to build
  /// per-channel WAV headers.
  /// </summary>
  public static AudioProperties ReadAudioProperties(ReadOnlySpan<byte> flacBytes) {
    if (flacBytes.Length < 42 ||
        flacBytes[0] != 0x66 || flacBytes[1] != 0x4C || flacBytes[2] != 0x61 || flacBytes[3] != 0x43)
      throw new InvalidDataException("Not a FLAC stream.");
    var si = ParseStreamInfo(flacBytes.Slice(8, 34));
    return new AudioProperties(si.SampleRate, si.Channels, si.BitsPerSample, si.TotalSamples);
  }

  private sealed record StreamInfo(
    int MinBlockSize, int MaxBlockSize, int MinFrameSize, int MaxFrameSize,
    int SampleRate, int Channels, int BitsPerSample, long TotalSamples);

  private static StreamInfo ParseStreamInfo(ReadOnlySpan<byte> data) {
    var minBlock = BinaryPrimitives.ReadUInt16BigEndian(data);
    var maxBlock = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
    var minFrame = (data[4] << 16) | (data[5] << 8) | data[6];
    var maxFrame = (data[7] << 16) | (data[8] << 8) | data[9];
    var sampleRate = (data[10] << 12) | (data[11] << 4) | (data[12] >> 4);
    var channels = ((data[12] >> 1) & 0x07) + 1;
    var bps = (((data[12] & 0x01) << 4) | (data[13] >> 4)) + 1;
    var totalSamples = ((long)(data[13] & 0x0F) << 32) | ((long)data[14] << 24) |
                       ((long)data[15] << 16) | ((long)data[16] << 8) | data[17];
    return new StreamInfo(minBlock, maxBlock, minFrame, maxFrame, sampleRate, channels, bps, totalSamples);
  }

  private static long ReadUtf8Number(BitReader reader) {
    var first = (int)reader.ReadBits(8);
    if (first < 0x80) return first;

    int extraBytes;
    long value;
    if ((first & 0xE0) == 0xC0) { extraBytes = 1; value = first & 0x1F; }
    else if ((first & 0xF0) == 0xE0) { extraBytes = 2; value = first & 0x0F; }
    else if ((first & 0xF8) == 0xF0) { extraBytes = 3; value = first & 0x07; }
    else if ((first & 0xFC) == 0xF8) { extraBytes = 4; value = first & 0x03; }
    else if ((first & 0xFE) == 0xFC) { extraBytes = 5; value = first & 0x01; }
    else if (first == 0xFE) { extraBytes = 6; value = 0; }
    else return first;

    for (var i = 0; i < extraBytes; ++i) {
      var b = reader.ReadBits(8);
      value = (value << 6) | (long)(b & 0x3F);
    }
    return value;
  }

  private static int[] DecodeSubframe(BitReader reader, int blockSize, int bps) {
    reader.ReadBits(1);
    var typeCode = (int)reader.ReadBits(6);

    var hasWasted = reader.ReadBits(1) == 1;
    var wastedBits = 0;
    if (hasWasted) {
      wastedBits = 1;
      while (reader.ReadBits(1) == 0) wastedBits++;
    }

    var effectiveBps = bps - wastedBits;
    int[] samples;

    if (typeCode == 0) {
      samples = new int[blockSize];
      var value = ReadSignedBits(reader, effectiveBps);
      Array.Fill(samples, value);
    } else if (typeCode == 1) {
      samples = new int[blockSize];
      for (var i = 0; i < blockSize; ++i)
        samples[i] = ReadSignedBits(reader, effectiveBps);
    } else if (typeCode >= 8 && typeCode <= 12) {
      samples = DecodeFixedSubframe(reader, blockSize, effectiveBps, typeCode - 8);
    } else if (typeCode >= 32) {
      samples = DecodeLpcSubframe(reader, blockSize, effectiveBps, typeCode - 31);
    } else {
      return [];
    }

    if (wastedBits > 0)
      for (var i = 0; i < samples.Length; ++i) samples[i] <<= wastedBits;
    return samples;
  }

  private static int ReadSignedBits(BitReader reader, int bits) {
    if (bits == 0) return 0;
    var raw = reader.ReadBits(bits);
    if ((raw & (1UL << (bits - 1))) != 0)
      raw |= unchecked((ulong)(-(1L << bits)));
    return (int)(long)raw;
  }

  private static int[] DecodeFixedSubframe(BitReader reader, int blockSize, int bps, int order) {
    var samples = new int[blockSize];
    for (var i = 0; i < order; ++i) samples[i] = ReadSignedBits(reader, bps);

    var residuals = DecodeRiceResiduals(reader, blockSize, order);
    for (var i = order; i < blockSize; ++i) {
      var prediction = order switch {
        0 => 0,
        1 => samples[i - 1],
        2 => 2 * samples[i - 1] - samples[i - 2],
        3 => 3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3],
        4 => 4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4],
        _ => 0,
      };
      samples[i] = prediction + residuals[i - order];
    }
    return samples;
  }

  private static int[] DecodeLpcSubframe(BitReader reader, int blockSize, int bps, int order) {
    var samples = new int[blockSize];
    for (var i = 0; i < order; ++i) samples[i] = ReadSignedBits(reader, bps);

    var lpcPrecision = (int)reader.ReadBits(4) + 1;
    var lpcShift = ReadSignedBits(reader, 5);

    var coefficients = new int[order];
    for (var i = 0; i < order; ++i) coefficients[i] = ReadSignedBits(reader, lpcPrecision);

    var residuals = DecodeRiceResiduals(reader, blockSize, order);
    for (var i = order; i < blockSize; ++i) {
      long prediction = 0;
      for (var j = 0; j < order; ++j)
        prediction += (long)coefficients[j] * samples[i - 1 - j];
      prediction >>= lpcShift;
      samples[i] = (int)prediction + residuals[i - order];
    }
    return samples;
  }

  private static int[] DecodeRiceResiduals(BitReader reader, int blockSize, int predictorOrder) {
    var codingMethod = (int)reader.ReadBits(2);
    var paramBits = codingMethod switch {
      0 => 4, 1 => 5, _ => throw new InvalidDataException($"Unknown Rice coding method: {codingMethod}"),
    };
    var escapeCode = (1 << paramBits) - 1;

    var partitionOrder = (int)reader.ReadBits(4);
    var numPartitions = 1 << partitionOrder;
    var residualCount = blockSize - predictorOrder;
    var residuals = new int[residualCount];
    var residualIndex = 0;

    for (var p = 0; p < numPartitions; ++p) {
      var partitionSamples = p == 0
        ? (blockSize / numPartitions) - predictorOrder
        : blockSize / numPartitions;

      var riceParam = (int)reader.ReadBits(paramBits);

      if (riceParam == escapeCode) {
        var escapeBps = (int)reader.ReadBits(5);
        for (var i = 0; i < partitionSamples; ++i)
          residuals[residualIndex++] = escapeBps == 0 ? 0 : ReadSignedBits(reader, escapeBps);
      } else {
        for (var i = 0; i < partitionSamples; ++i) {
          var quotient = 0;
          while (reader.ReadBits(1) == 0) quotient++;
          var remainder = riceParam > 0 ? (int)reader.ReadBits(riceParam) : 0;
          var unsigned = (quotient << riceParam) | remainder;
          var signed = (unsigned >> 1) ^ -(unsigned & 1);
          residuals[residualIndex++] = signed;
        }
      }
    }
    return residuals;
  }

  private sealed class BitReader {
    private readonly byte[] _data;
    private int _bytePos;
    private int _bitPos;

    public BitReader(byte[] data, int startByte) {
      this._data = data;
      this._bytePos = startByte;
      this._bitPos = 0;
    }

    public int BytePosition => this._bitPos == 0 ? this._bytePos : this._bytePos + 1;

    public ulong ReadBits(int count) {
      ulong result = 0;
      for (var i = 0; i < count; ++i) {
        if (this._bytePos >= this._data.Length)
          throw new InvalidDataException("Unexpected end of FLAC data.");
        result = (result << 1) | (uint)((uint)(this._data[this._bytePos] >> (7 - this._bitPos)) & 1u);
        this._bitPos++;
        if (this._bitPos == 8) {
          this._bitPos = 0;
          this._bytePos++;
        }
      }
      return result;
    }

    public void AlignToByte() {
      if (this._bitPos != 0) {
        this._bitPos = 0;
        this._bytePos++;
      }
    }
  }
}
