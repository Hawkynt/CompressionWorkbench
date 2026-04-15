#pragma warning disable CS1591

using System.Buffers.Binary;

namespace FileFormat.Flac;

/// <summary>
/// Reads a FLAC stream and outputs raw interleaved little-endian PCM data.
/// Supports CONSTANT, VERBATIM, and FIXED (orders 0-4) subframe types with Rice-coded residuals.
/// Also supports LPC subframes.
/// </summary>
public static class FlacReader {

  /// <summary>
  /// Decompresses a FLAC stream to raw PCM output.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read all input into a byte array for bit-level access
    byte[] data;
    if (input is MemoryStream ms && ms.TryGetBuffer(out var seg))
      data = seg.Array!;
    else {
      using var tmp = new MemoryStream();
      input.CopyTo(tmp);
      data = tmp.ToArray();
    }

    if (data.Length < 4)
      return;

    // Verify "fLaC" magic
    if (data[0] != 0x66 || data[1] != 0x4C || data[2] != 0x61 || data[3] != 0x43)
      throw new InvalidDataException("Not a FLAC stream: missing 'fLaC' magic.");

    var pos = 4;

    // Parse metadata blocks
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
      if (isLast)
        break;
    }

    if (streamInfo == null)
      throw new InvalidDataException("FLAC stream missing STREAMINFO metadata block.");

    var bytesPerSample = (streamInfo.BitsPerSample + 7) / 8;

    // Decode audio frames
    while (pos + 2 <= data.Length) {
      // Scan for sync code: 0xFFF8 or 0xFFF9
      if (data[pos] != 0xFF || (data[pos + 1] & 0xFE) != 0xF8) {
        pos++;
        continue;
      }

      var frameStart = pos;
      var reader = new BitReader(data, pos);

      // Frame header
      var syncCode = reader.ReadBits(15); // 14-bit sync + 1 bit reserved
      if (syncCode != 0x7FF8 >> 1 && syncCode != 0x7FF9 >> 1) {
        // Actually read the full 14 bits of sync
        reader = new BitReader(data, pos);
      }

      // Re-read from frame start properly
      reader = new BitReader(data, pos);
      var sync14 = reader.ReadBits(14); // must be 0x3FFE
      if (sync14 != 0x3FFE) {
        pos++;
        continue;
      }

      reader.ReadBits(1); // reserved, must be 0
      var blockingStrategy = reader.ReadBits(1); // 0 = fixed, 1 = variable
      var blockSizeCode = reader.ReadBits(4);
      var sampleRateCode = reader.ReadBits(4);
      var channelAssignment = reader.ReadBits(4);
      var sampleSizeCode = reader.ReadBits(3);
      reader.ReadBits(1); // reserved

      // UTF-8 coded frame/sample number
      ReadUtf8Number(reader);

      // Block size
      var blockSize = blockSizeCode switch {
        0 => 0, // reserved
        1 => 192,
        2 => 576,
        3 => 1152,
        4 => 2304,
        5 => 4608,
        6 => (int)reader.ReadBits(8) + 1,
        7 => (int)reader.ReadBits(16) + 1,
        >= 8 and <= 15 => 256 << (int)(blockSizeCode - 8),
        _ => 0
      };

      if (blockSize == 0) {
        pos++;
        continue;
      }

      // Sample rate (from header code or STREAMINFO)
      switch (sampleRateCode) {
        case 12: reader.ReadBits(8); break; // sample rate in kHz
        case 13: reader.ReadBits(16); break; // sample rate in Hz
        case 14: reader.ReadBits(16); break; // sample rate in tens of Hz
      }

      // Bits per sample from frame header
      var bps = sampleSizeCode switch {
        0 => streamInfo.BitsPerSample,
        1 => 8,
        2 => 12,
        3 => 0, // reserved
        4 => 16,
        5 => 20,
        6 => 24,
        7 => 32,
        _ => streamInfo.BitsPerSample
      };

      // CRC-8 of frame header (skip verification for now, just read it)
      reader.ReadBits(8);

      // Determine number of channels from channel assignment
      int channels;
      bool isMidSide = false, isLeftSide = false, isRightSide = false;
      if (channelAssignment <= 7) {
        channels = (int)channelAssignment + 1;
      } else if (channelAssignment == 8) {
        channels = 2;
        isLeftSide = true; // left/side stereo
      } else if (channelAssignment == 9) {
        channels = 2;
        isRightSide = true; // side/right stereo
      } else if (channelAssignment == 10) {
        channels = 2;
        isMidSide = true; // mid/side stereo
      } else {
        pos++;
        continue; // reserved
      }

      // Decode subframes
      var samples = new int[channels][];
      var decodeFailed = false;
      for (var ch = 0; ch < channels; ch++) {
        // For side-channel stereo, the side channel gets an extra bit
        var subBps = bps;
        if (isLeftSide && ch == 1) subBps++;
        else if (isRightSide && ch == 0) subBps++;
        else if (isMidSide && ch == 1) subBps++;

        samples[ch] = DecodeSubframe(reader, blockSize, subBps);
        if (samples[ch].Length == 0) {
          decodeFailed = true;
          break;
        }
      }

      if (decodeFailed) {
        pos++;
        continue;
      }

      // Decorrelate stereo channels
      if (isLeftSide) {
        // ch0 = left, ch1 = side; right = left - side
        for (var i = 0; i < blockSize; i++)
          samples[1][i] = samples[0][i] - samples[1][i];
      } else if (isRightSide) {
        // ch0 = side, ch1 = right; left = side + right
        for (var i = 0; i < blockSize; i++)
          samples[0][i] = samples[0][i] + samples[1][i];
      } else if (isMidSide) {
        // ch0 = mid, ch1 = side; left = (mid + side) / 2, right = (mid - side) / 2
        // But FLAC does: mid = (left + right), side = (left - right)
        // So: left = (mid + side) >> 1 after adjusting mid: mid = mid * 2 + (side & 1)
        for (var i = 0; i < blockSize; i++) {
          var mid = samples[0][i];
          var side = samples[1][i];
          mid = (mid << 1) | (side & 1);
          samples[0][i] = (mid + side) >> 1;
          samples[1][i] = (mid - side) >> 1;
        }
      }

      // Write interleaved PCM samples
      bytesPerSample = (bps + 7) / 8;
      var frameBuffer = new byte[blockSize * channels * bytesPerSample];
      var bufPos = 0;
      for (var i = 0; i < blockSize; i++) {
        for (var ch = 0; ch < channels; ch++) {
          var sample = samples[ch][i];
          switch (bytesPerSample) {
            case 1:
              frameBuffer[bufPos++] = (byte)(sample + 128); // 8-bit PCM is unsigned
              break;
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

      // Align to byte boundary and skip CRC-16
      reader.AlignToByte();
      reader.ReadBits(16); // frame CRC-16

      pos = reader.BytePosition;
    }
  }

  private sealed record StreamInfo(
    int MinBlockSize,
    int MaxBlockSize,
    int MinFrameSize,
    int MaxFrameSize,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    long TotalSamples
  );

  private static StreamInfo ParseStreamInfo(ReadOnlySpan<byte> data) {
    var minBlock = BinaryPrimitives.ReadUInt16BigEndian(data);
    var maxBlock = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
    var minFrame = (data[4] << 16) | (data[5] << 8) | data[6];
    var maxFrame = (data[7] << 16) | (data[8] << 8) | data[9];

    // 20-bit sample rate, 3-bit channels-1, 5-bit bps-1, 36-bit total samples
    var sampleRate = (data[10] << 12) | (data[11] << 4) | (data[12] >> 4);
    var channels = ((data[12] >> 1) & 0x07) + 1;
    var bps = (((data[12] & 0x01) << 4) | (data[13] >> 4)) + 1;
    var totalSamples = ((long)(data[13] & 0x0F) << 32) | ((long)data[14] << 24) |
                       ((long)data[15] << 16) | ((long)data[16] << 8) | data[17];

    return new StreamInfo(minBlock, maxBlock, minFrame, maxFrame, sampleRate, channels, bps, totalSamples);
  }

  private static long ReadUtf8Number(BitReader reader) {
    var first = (int)reader.ReadBits(8);
    if (first < 0x80)
      return first;

    int extraBytes;
    long value;
    if ((first & 0xE0) == 0xC0) { extraBytes = 1; value = first & 0x1F; }
    else if ((first & 0xF0) == 0xE0) { extraBytes = 2; value = first & 0x0F; }
    else if ((first & 0xF8) == 0xF0) { extraBytes = 3; value = first & 0x07; }
    else if ((first & 0xFC) == 0xF8) { extraBytes = 4; value = first & 0x03; }
    else if ((first & 0xFE) == 0xFC) { extraBytes = 5; value = first & 0x01; }
    else if (first == 0xFE) { extraBytes = 6; value = 0; }
    else return first; // Invalid

    for (var i = 0; i < extraBytes; i++) {
      var b = reader.ReadBits(8);
      value = (value << 6) | (long)(b & 0x3F);
    }

    return value;
  }

  private static int[] DecodeSubframe(BitReader reader, int blockSize, int bps) {
    // Subframe header
    reader.ReadBits(1); // padding, must be 0
    var typeCode = (int)reader.ReadBits(6);

    // Wasted bits per sample
    var hasWasted = reader.ReadBits(1) == 1;
    var wastedBits = 0;
    if (hasWasted) {
      wastedBits = 1;
      while (reader.ReadBits(1) == 0)
        wastedBits++;
    }

    var effectiveBps = bps - wastedBits;
    int[] samples;

    if (typeCode == 0) {
      // CONSTANT
      samples = new int[blockSize];
      var value = ReadSignedBits(reader, effectiveBps);
      Array.Fill(samples, value);
    } else if (typeCode == 1) {
      // VERBATIM
      samples = new int[blockSize];
      for (var i = 0; i < blockSize; i++)
        samples[i] = ReadSignedBits(reader, effectiveBps);
    } else if (typeCode >= 8 && typeCode <= 12) {
      // FIXED prediction, order = typeCode - 8
      var order = typeCode - 8;
      samples = DecodeFixedSubframe(reader, blockSize, effectiveBps, order);
    } else if (typeCode >= 32) {
      // LPC prediction, order = (typeCode - 32) + 1
      var order = typeCode - 31;
      samples = DecodeLpcSubframe(reader, blockSize, effectiveBps, order);
    } else {
      // Reserved subframe type -- return empty to signal failure
      return [];
    }

    // Shift back wasted bits
    if (wastedBits > 0)
      for (var i = 0; i < samples.Length; i++)
        samples[i] <<= wastedBits;

    return samples;
  }

  private static int ReadSignedBits(BitReader reader, int bits) {
    if (bits == 0)
      return 0;

    var raw = reader.ReadBits(bits);
    // Sign-extend
    if ((raw & (1UL << (bits - 1))) != 0)
      raw |= unchecked((ulong)(-(1L << bits)));
    return (int)(long)raw;
  }

  private static int[] DecodeFixedSubframe(BitReader reader, int blockSize, int bps, int order) {
    var samples = new int[blockSize];

    // Read warm-up samples
    for (var i = 0; i < order; i++)
      samples[i] = ReadSignedBits(reader, bps);

    // Decode Rice-coded residuals
    var residuals = DecodeRiceResiduals(reader, blockSize, order);

    // Apply fixed prediction
    for (var i = order; i < blockSize; i++) {
      var prediction = order switch {
        0 => 0,
        1 => samples[i - 1],
        2 => 2 * samples[i - 1] - samples[i - 2],
        3 => 3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3],
        4 => 4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4],
        _ => 0
      };
      samples[i] = prediction + residuals[i - order];
    }

    return samples;
  }

  private static int[] DecodeLpcSubframe(BitReader reader, int blockSize, int bps, int order) {
    var samples = new int[blockSize];

    // Read warm-up samples
    for (var i = 0; i < order; i++)
      samples[i] = ReadSignedBits(reader, bps);

    // LPC precision (4 bits)
    var lpcPrecision = (int)reader.ReadBits(4) + 1;

    // LPC shift (5 bits, signed)
    var lpcShift = ReadSignedBits(reader, 5);

    // LPC coefficients
    var coefficients = new int[order];
    for (var i = 0; i < order; i++)
      coefficients[i] = ReadSignedBits(reader, lpcPrecision);

    // Decode Rice-coded residuals
    var residuals = DecodeRiceResiduals(reader, blockSize, order);

    // Apply LPC prediction
    for (var i = order; i < blockSize; i++) {
      long prediction = 0;
      for (var j = 0; j < order; j++)
        prediction += (long)coefficients[j] * samples[i - 1 - j];

      prediction >>= lpcShift;
      samples[i] = (int)prediction + residuals[i - order];
    }

    return samples;
  }

  private static int[] DecodeRiceResiduals(BitReader reader, int blockSize, int predictorOrder) {
    var codingMethod = (int)reader.ReadBits(2);
    var paramBits = codingMethod switch {
      0 => 4, // RICE_PARTITION_TYPE_0
      1 => 5, // RICE_PARTITION_TYPE_1
      _ => throw new InvalidDataException($"Unknown Rice coding method: {codingMethod}")
    };
    var escapeCode = (1 << paramBits) - 1;

    var partitionOrder = (int)reader.ReadBits(4);
    var numPartitions = 1 << partitionOrder;
    var residualCount = blockSize - predictorOrder;
    var residuals = new int[residualCount];
    var residualIndex = 0;

    for (var p = 0; p < numPartitions; p++) {
      var partitionSamples = p == 0
        ? (blockSize / numPartitions) - predictorOrder
        : blockSize / numPartitions;

      var riceParam = (int)reader.ReadBits(paramBits);

      if (riceParam == escapeCode) {
        // Escape: unencoded samples with bps bits each
        var escapeBps = (int)reader.ReadBits(5);
        for (var i = 0; i < partitionSamples; i++) {
          if (escapeBps == 0)
            residuals[residualIndex++] = 0;
          else
            residuals[residualIndex++] = ReadSignedBits(reader, escapeBps);
        }
      } else {
        for (var i = 0; i < partitionSamples; i++) {
          // Read unary: count zero bits until a 1
          var quotient = 0;
          while (reader.ReadBits(1) == 0)
            quotient++;

          // Read binary part
          var remainder = riceParam > 0 ? (int)reader.ReadBits(riceParam) : 0;
          var unsigned = (quotient << riceParam) | remainder;

          // Convert to signed: zigzag decoding
          var signed = (unsigned >> 1) ^ -(unsigned & 1);
          residuals[residualIndex++] = signed;
        }
      }
    }

    return residuals;
  }

  /// <summary>
  /// Bit reader for reading individual bits from a byte array (big-endian, MSB-first).
  /// </summary>
  internal sealed class BitReader {
    private readonly byte[] _data;
    private int _bytePos;
    private int _bitPos; // 0-7, 0 = MSB

    public BitReader(byte[] data, int startByte) {
      _data = data;
      _bytePos = startByte;
      _bitPos = 0;
    }

    public int BytePosition => _bitPos == 0 ? _bytePos : _bytePos + 1;

    public ulong ReadBits(int count) {
      ulong result = 0;
      for (var i = 0; i < count; i++) {
        if (_bytePos >= _data.Length)
          throw new InvalidDataException("Unexpected end of FLAC data.");

        result = (result << 1) | (uint)((uint)(_data[_bytePos] >> (7 - _bitPos)) & 1u);
        _bitPos++;
        if (_bitPos == 8) {
          _bitPos = 0;
          _bytePos++;
        }
      }

      return result;
    }

    public void AlignToByte() {
      if (_bitPos != 0) {
        _bitPos = 0;
        _bytePos++;
      }
    }
  }
}
