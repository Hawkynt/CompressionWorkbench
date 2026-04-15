#pragma warning disable CS1591

using System.Security.Cryptography;

namespace FileFormat.Flac;

/// <summary>
/// Writes a FLAC stream from raw interleaved little-endian PCM data.
/// Assumes 16-bit stereo 44100 Hz input by default.
/// Uses FIXED prediction (orders 0-4) and LPC prediction (orders 1-8)
/// with Rice-coded residuals, choosing whichever produces smaller output.
/// </summary>
public static class FlacWriter {

  private const int DefaultSampleRate = 44100;
  private const int DefaultChannels = 2;
  private const int DefaultBitsPerSample = 16;
  private const int DefaultBlockSize = 4096;

  /// <summary>
  /// Compresses raw PCM data to FLAC format.
  /// Input is expected to be interleaved little-endian 16-bit signed PCM (stereo 44100 Hz).
  /// </summary>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read all PCM data
    byte[] pcmData;
    if (input is MemoryStream ms && ms.TryGetBuffer(out var seg)) {
      pcmData = seg.Array!;
      if (seg.Offset != 0 || seg.Count != pcmData.Length) {
        pcmData = new byte[seg.Count];
        Buffer.BlockCopy(seg.Array!, seg.Offset, pcmData, 0, seg.Count);
      }
    } else {
      using var tmp = new MemoryStream();
      input.CopyTo(tmp);
      pcmData = tmp.ToArray();
    }

    if (pcmData.Length == 0) {
      // Write minimal valid FLAC: magic + empty STREAMINFO
      WriteMagic(output);
      WriteStreamInfoBlock(output, isLast: true, DefaultBlockSize, DefaultBlockSize,
        0, 0, DefaultSampleRate, DefaultChannels, DefaultBitsPerSample, 0, new byte[16]);
      return;
    }

    var bytesPerSample = DefaultBitsPerSample / 8;
    var bytesPerFrame = bytesPerSample * DefaultChannels;
    var totalSamples = pcmData.Length / bytesPerFrame;
    var blockSize = Math.Min(DefaultBlockSize, totalSamples);
    if (blockSize == 0)
      blockSize = 1;

    // Compute MD5 of raw PCM
    var md5 = MD5.HashData(pcmData.AsSpan(0, totalSamples * bytesPerFrame));

    // We need to know frame sizes for STREAMINFO, so encode frames to a buffer first
    using var frameData = new MemoryStream();
    var minFrameSize = int.MaxValue;
    var maxFrameSize = 0;
    var frameNumber = 0;

    for (var sampleOffset = 0; sampleOffset < totalSamples; sampleOffset += blockSize) {
      var currentBlockSize = Math.Min(blockSize, totalSamples - sampleOffset);

      // Deinterleave samples
      var left = new int[currentBlockSize];
      var right = new int[currentBlockSize];
      for (var i = 0; i < currentBlockSize; i++) {
        var byteOffset = (sampleOffset + i) * bytesPerFrame;
        left[i] = (short)(pcmData[byteOffset] | (pcmData[byteOffset + 1] << 8));
        if (DefaultChannels == 2)
          right[i] = (short)(pcmData[byteOffset + 2] | (pcmData[byteOffset + 3] << 8));
      }

      var channels = new int[][] { left, right };
      var frameSizeBefore = frameData.Position;

      WriteFrame(frameData, channels, currentBlockSize, frameNumber, DefaultSampleRate,
        DefaultChannels, DefaultBitsPerSample);

      var frameSize = (int)(frameData.Position - frameSizeBefore);
      minFrameSize = Math.Min(minFrameSize, frameSize);
      maxFrameSize = Math.Max(maxFrameSize, frameSize);
      frameNumber++;
    }

    if (minFrameSize == int.MaxValue)
      minFrameSize = 0;

    // Now write the complete FLAC file
    WriteMagic(output);
    WriteStreamInfoBlock(output, isLast: true, blockSize, blockSize,
      minFrameSize, maxFrameSize, DefaultSampleRate, DefaultChannels,
      DefaultBitsPerSample, totalSamples, md5);

    frameData.Position = 0;
    frameData.CopyTo(output);
  }

  private static void WriteMagic(Stream output) {
    output.Write("fLaC"u8);
  }

  private static void WriteStreamInfoBlock(Stream output, bool isLast,
    int minBlockSize, int maxBlockSize, int minFrameSize, int maxFrameSize,
    int sampleRate, int channels, int bitsPerSample, long totalSamples, byte[] md5) {
    // Metadata block header: 1 byte (is_last + type) + 3 bytes (length)
    output.WriteByte((byte)((isLast ? 0x80 : 0x00) | 0x00)); // type 0 = STREAMINFO
    var length = 34;
    output.WriteByte((byte)(length >> 16));
    output.WriteByte((byte)(length >> 8));
    output.WriteByte((byte)length);

    // STREAMINFO: 34 bytes
    // min block size (16 bits)
    output.WriteByte((byte)(minBlockSize >> 8));
    output.WriteByte((byte)minBlockSize);
    // max block size (16 bits)
    output.WriteByte((byte)(maxBlockSize >> 8));
    output.WriteByte((byte)maxBlockSize);
    // min frame size (24 bits)
    output.WriteByte((byte)(minFrameSize >> 16));
    output.WriteByte((byte)(minFrameSize >> 8));
    output.WriteByte((byte)minFrameSize);
    // max frame size (24 bits)
    output.WriteByte((byte)(maxFrameSize >> 16));
    output.WriteByte((byte)(maxFrameSize >> 8));
    output.WriteByte((byte)maxFrameSize);
    // sample rate (20 bits) | channels-1 (3 bits) | bps-1 (5 bits) | total samples (36 bits)
    // = 8 bytes total
    var ch = channels - 1;
    var bpsM1 = bitsPerSample - 1;
    output.WriteByte((byte)(sampleRate >> 12));
    output.WriteByte((byte)(sampleRate >> 4));
    output.WriteByte((byte)(((sampleRate & 0x0F) << 4) | ((ch & 0x07) << 1) | ((bpsM1 >> 4) & 0x01)));
    output.WriteByte((byte)(((bpsM1 & 0x0F) << 4) | (int)((totalSamples >> 32) & 0x0F)));
    output.WriteByte((byte)(totalSamples >> 24));
    output.WriteByte((byte)(totalSamples >> 16));
    output.WriteByte((byte)(totalSamples >> 8));
    output.WriteByte((byte)totalSamples);
    // MD5 (16 bytes)
    output.Write(md5.AsSpan(0, 16));
  }

  private static void WriteFrame(Stream output, int[][] channels, int blockSize,
    int frameNumber, int sampleRate, int numChannels, int bitsPerSample) {
    var writer = new BitWriter();

    // Frame header
    writer.WriteBits(14, 0x3FFE); // sync code
    writer.WriteBits(1, 0); // reserved
    writer.WriteBits(1, 0); // blocking strategy: fixed-size blocks

    // Block size code
    var blockSizeCode = GetBlockSizeCode(blockSize, out var blockSizeExtra, out var blockSizeExtraBits);
    writer.WriteBits(4, (uint)blockSizeCode);

    // Sample rate code
    var sampleRateCode = GetSampleRateCode(sampleRate);
    writer.WriteBits(4, (uint)sampleRateCode);

    // Channel assignment: 0 = independent (n channels), 1 = left/right for stereo
    uint channelAssignment;
    if (numChannels == 2)
      channelAssignment = 1; // left/right independent stereo
    else
      channelAssignment = (uint)(numChannels - 1);
    writer.WriteBits(4, channelAssignment);

    // Sample size code
    var sampleSizeCode = bitsPerSample switch {
      8 => 1,
      12 => 2,
      16 => 4,
      20 => 5,
      24 => 6,
      32 => 7,
      _ => 0
    };
    writer.WriteBits(3, (uint)sampleSizeCode);
    writer.WriteBits(1, 0); // reserved

    // Frame number (UTF-8 coded)
    WriteUtf8Number(writer, (ulong)frameNumber);

    // Extra block size bytes if needed
    if (blockSizeExtraBits == 8)
      writer.WriteBits(8, (uint)(blockSize - 1));
    else if (blockSizeExtraBits == 16)
      writer.WriteBits(16, (uint)(blockSize - 1));

    // CRC-8 of frame header
    var headerBytes = writer.ToByteArray();
    var crc8 = ComputeCrc8(headerBytes);
    writer.WriteBits(8, crc8);

    // Subframes
    for (var ch = 0; ch < numChannels; ch++)
      WriteSubframe(writer, channels[ch], blockSize, bitsPerSample);

    // Pad to byte boundary
    writer.PadToByte();

    // Get all frame bytes so far for CRC-16
    var frameBytes = writer.ToByteArray();
    var crc16 = ComputeCrc16(frameBytes);
    writer.WriteBits(16, crc16);

    // Write the complete frame
    var finalBytes = writer.ToByteArray();
    output.Write(finalBytes);
  }

  private const int MaxLpcOrder = 8;
  private const int LpcCoeffPrecision = 12; // bits for quantized LPC coefficients

  private static void WriteSubframe(BitWriter writer, int[] samples, int blockSize, int bps) {
    // Try each fixed predictor order (0-4) using trial encoding to measure actual bit cost
    var bestFixedOrder = 0;
    var bestFixedBits = int.MaxValue;

    for (var order = 0; order <= 4 && order <= blockSize; order++) {
      var trial = new BitWriter();
      WriteFixedSubframe(trial, samples, blockSize, bps, order);
      var bits = trial.BitCount;
      if (bits < bestFixedBits) {
        bestFixedBits = bits;
        bestFixedOrder = order;
      }
    }

    // Try LPC prediction orders 1-8
    var bestLpcOrder = -1;
    var bestLpcBits = int.MaxValue;
    int[]? bestLpcResiduals = null;
    int[]? bestLpcCoeffs = null;
    var bestLpcShift = 0;

    if (blockSize > MaxLpcOrder) {
      var maxOrder = Math.Min(MaxLpcOrder, blockSize - 1);
      var autocorr = ComputeAutocorrelation(samples, blockSize, maxOrder);

      if (autocorr[0] > 0) {
        for (var order = 1; order <= maxOrder; order++) {
          var lpcCoeffs = LevinsonDurbin(autocorr, order);
          if (lpcCoeffs == null)
            continue;

          var quantized = QuantizeLpcCoefficients(lpcCoeffs, LpcCoeffPrecision, out var shift);
          var residuals = ComputeLpcResiduals(samples, blockSize, quantized, shift, order);

          // Trial encode to measure actual bit cost
          var trial = new BitWriter();
          WriteLpcSubframe(trial, samples, blockSize, bps, order, quantized, shift, residuals);
          var bits = trial.BitCount;

          if (bits < bestLpcBits) {
            bestLpcBits = bits;
            bestLpcOrder = order;
            bestLpcResiduals = residuals;
            bestLpcCoeffs = quantized;
            bestLpcShift = shift;
          }
        }
      }
    }

    // Choose whichever produces fewer bits
    if (bestLpcOrder > 0 && bestLpcBits < bestFixedBits) {
      WriteLpcSubframe(writer, samples, blockSize, bps, bestLpcOrder, bestLpcCoeffs!, bestLpcShift, bestLpcResiduals!);
    } else {
      WriteFixedSubframe(writer, samples, blockSize, bps, bestFixedOrder);
    }
  }

  private static void WriteFixedSubframe(BitWriter writer, int[] samples, int blockSize, int bps, int order) {
    writer.WriteBits(1, 0); // padding
    var typeCode = 8 + order; // FIXED with order
    writer.WriteBits(6, (uint)typeCode);
    writer.WriteBits(1, 0); // no wasted bits

    for (var i = 0; i < order; i++)
      WriteSignedBits(writer, samples[i], bps);

    var residuals = ComputeFixedResiduals(samples, blockSize, order);
    WriteRiceResiduals(writer, residuals, blockSize, order);
  }

  private static void WriteLpcSubframe(BitWriter writer, int[] samples, int blockSize, int bps,
    int order, int[] quantizedCoeffs, int shift, int[] residuals) {
    writer.WriteBits(1, 0); // padding
    var typeCode = 32 + (order - 1); // LPC: 0x20 | (order-1)
    writer.WriteBits(6, (uint)typeCode);
    writer.WriteBits(1, 0); // no wasted bits

    // Warm-up samples
    for (var i = 0; i < order; i++)
      WriteSignedBits(writer, samples[i], bps);

    // LPC precision - 1 (4 bits)
    writer.WriteBits(4, (uint)(LpcCoeffPrecision - 1));

    // LPC shift (5 bits, signed two's complement)
    WriteSignedBits(writer, shift, 5);

    // Quantized LPC coefficients
    for (var i = 0; i < order; i++)
      WriteSignedBits(writer, quantizedCoeffs[i], LpcCoeffPrecision);

    // Rice-coded residuals
    WriteRiceResiduals(writer, residuals, blockSize, order);
  }

  private static double[] ComputeAutocorrelation(int[] samples, int blockSize, int maxOrder) {
    var result = new double[maxOrder + 1];
    for (var lag = 0; lag <= maxOrder; lag++) {
      double sum = 0;
      for (var i = lag; i < blockSize; i++)
        sum += (double)samples[i] * samples[i - lag];
      result[lag] = sum;
    }
    return result;
  }

  /// <summary>
  /// Levinson-Durbin recursion to compute LPC coefficients from autocorrelation.
  /// Returns null if the matrix is singular or numerically unstable.
  /// </summary>
  private static double[]? LevinsonDurbin(double[] autocorr, int order) {
    var a = new double[order + 1]; // coefficients (1-based indexing)
    var aTemp = new double[order + 1];
    var error = autocorr[0];

    if (error <= 0)
      return null;

    for (var i = 1; i <= order; i++) {
      // Check that we have enough error to continue
      if (error < autocorr[0] * 1e-15)
        return null; // Signal is essentially modeled perfectly by previous order

      // Compute reflection coefficient
      double sum = 0;
      for (var j = 1; j < i; j++)
        sum += a[j] * autocorr[i - j];

      var k = -(autocorr[i] + sum) / error;

      // Check for numerical stability
      if (double.IsNaN(k) || double.IsInfinity(k) || Math.Abs(k) > 1.0)
        return null;

      // Update coefficients
      aTemp[i] = k;
      for (var j = 1; j < i; j++)
        aTemp[j] = a[j] + k * a[i - j];

      Array.Copy(aTemp, 1, a, 1, i);

      error *= 1.0 - k * k;
    }

    // Return coefficients negated for FLAC convention:
    // Levinson-Durbin gives a[] where x[n] + a[1]*x[n-1] + ... = 0
    // FLAC expects prediction = coeff[0]*x[n-1] + coeff[1]*x[n-2] + ...
    // So coeff[j] = -a[j+1]
    var result = new double[order];
    for (var i = 0; i < order; i++)
      result[i] = -a[i + 1];
    return result;
  }

  /// <summary>
  /// Quantizes double LPC coefficients to fixed-point integers.
  /// Returns quantized coefficients and the shift value.
  /// </summary>
  private static int[] QuantizeLpcCoefficients(double[] coeffs, int precision, out int shift) {
    var order = coeffs.Length;
    var quantized = new int[order];

    // Find the maximum absolute coefficient value
    var maxAbs = 0.0;
    for (var i = 0; i < order; i++) {
      var abs = Math.Abs(coeffs[i]);
      if (abs > maxAbs)
        maxAbs = abs;
    }

    if (maxAbs == 0) {
      shift = 0;
      return quantized;
    }

    // Determine shift: we want maxAbs * 2^shift to fit in (precision-1) bits
    // maxAbs * 2^shift <= 2^(precision-1) - 1
    // shift <= log2((2^(precision-1) - 1) / maxAbs)
    var maxCoeffValue = (1 << (precision - 1)) - 1;
    shift = (int)Math.Floor(Math.Log2(maxCoeffValue / maxAbs));
    shift = Math.Max(0, Math.Min(shift, 15)); // shift must fit in 5 signed bits (0..15 for positive)

    var scale = 1 << shift;
    for (var i = 0; i < order; i++) {
      quantized[i] = (int)Math.Round(coeffs[i] * scale);
      // Clamp to precision bits (signed)
      quantized[i] = Math.Clamp(quantized[i], -(1 << (precision - 1)), maxCoeffValue);
    }

    return quantized;
  }

  private static int[] ComputeLpcResiduals(int[] samples, int blockSize, int[] quantizedCoeffs, int shift, int order) {
    var count = blockSize - order;
    if (count <= 0)
      return [];

    var residuals = new int[count];
    for (var i = 0; i < count; i++) {
      var idx = i + order;
      long prediction = 0;
      for (var j = 0; j < order; j++)
        prediction += (long)quantizedCoeffs[j] * samples[idx - 1 - j];

      prediction >>= shift;
      residuals[i] = samples[idx] - (int)prediction;
    }

    return residuals;
  }

  private static int[] ComputeFixedResiduals(int[] samples, int blockSize, int order) {
    var count = blockSize - order;
    if (count <= 0)
      return [];

    var residuals = new int[count];
    for (var i = 0; i < count; i++) {
      var idx = i + order;
      residuals[i] = order switch {
        0 => samples[idx],
        1 => samples[idx] - samples[idx - 1],
        2 => samples[idx] - 2 * samples[idx - 1] + samples[idx - 2],
        3 => samples[idx] - 3 * samples[idx - 1] + 3 * samples[idx - 2] - samples[idx - 3],
        4 => samples[idx] - 4 * samples[idx - 1] + 6 * samples[idx - 2] - 4 * samples[idx - 3] + samples[idx - 4],
        _ => samples[idx]
      };
    }

    return residuals;
  }

  private static void WriteRiceResiduals(BitWriter writer, int[] residuals, int blockSize, int predictorOrder) {
    // Use Rice partition type 0 (4-bit parameters)
    writer.WriteBits(2, 0); // coding method: 0 = RICE_PARTITION_TYPE_0

    // Choose partition order: try to find one where block size is evenly divisible
    var partitionOrder = 0;
    // For simplicity, use partition order 0 (single partition) unless block size allows higher
    for (var po = 4; po >= 0; po--) {
      var np = 1 << po;
      if (blockSize % np == 0 && (blockSize / np) > predictorOrder) {
        partitionOrder = po;
        break;
      }
    }

    writer.WriteBits(4, (uint)partitionOrder);
    var numPartitions = 1 << partitionOrder;
    var residualIndex = 0;

    for (var p = 0; p < numPartitions; p++) {
      var partitionSamples = p == 0
        ? (blockSize / numPartitions) - predictorOrder
        : blockSize / numPartitions;

      // Compute optimal Rice parameter for this partition
      long absSum = 0;
      for (var i = 0; i < partitionSamples; i++)
        absSum += Math.Abs(residuals[residualIndex + i]);

      var riceParam = 0;
      if (partitionSamples > 0 && absSum > 0) {
        var meanAbs = (double)absSum / partitionSamples;
        riceParam = Math.Max(0, (int)Math.Floor(Math.Log2(meanAbs)));
        riceParam = Math.Min(riceParam, 14); // Max for 4-bit parameter encoding
      }

      writer.WriteBits(4, (uint)riceParam);

      // Encode each residual
      for (var i = 0; i < partitionSamples; i++) {
        var value = residuals[residualIndex++];

        // Zigzag encode: signed -> unsigned
        var unsigned = value >= 0 ? (uint)(value << 1) : (uint)((-value << 1) - 1);

        // Rice encode: quotient in unary, remainder in binary
        var quotient = (int)(unsigned >> riceParam);
        var remainder = unsigned & ((1u << riceParam) - 1);

        // Write unary (quotient zeros + one 1)
        for (var q = 0; q < quotient; q++)
          writer.WriteBits(1, 0);
        writer.WriteBits(1, 1);

        // Write binary remainder
        if (riceParam > 0)
          writer.WriteBits(riceParam, remainder);
      }
    }
  }

  private static int GetBlockSizeCode(int blockSize, out int extra, out int extraBits) {
    extra = 0;
    extraBits = 0;
    return blockSize switch {
      192 => 1,
      576 => 2,
      1152 => 3,
      2304 => 4,
      4608 => 5,
      256 => 8,
      512 => 9,
      1024 => 10,
      2048 => 11,
      4096 => 12,
      8192 => 13,
      16384 => 14,
      32768 => 15,
      _ when blockSize <= 256 => SetExtra(6, out extra, out extraBits, 8),
      _ => SetExtra(7, out extra, out extraBits, 16)
    };

    static int SetExtra(int code, out int extra, out int extraBits, int bits) {
      extra = 1;
      extraBits = bits;
      return code;
    }
  }

  private static int GetSampleRateCode(int sampleRate) =>
    sampleRate switch {
      88200 => 1,
      176400 => 2,
      192000 => 3,
      8000 => 4,
      16000 => 5,
      22050 => 6,
      24000 => 7,
      32000 => 8,
      44100 => 9,
      48000 => 10,
      96000 => 11,
      _ => 0 // use STREAMINFO value
    };

  private static void WriteUtf8Number(BitWriter writer, ulong value) {
    if (value < 0x80) {
      writer.WriteBits(8, (uint)value);
    } else if (value < 0x800) {
      writer.WriteBits(8, (uint)(0xC0 | (value >> 6)));
      writer.WriteBits(8, (uint)(0x80 | (value & 0x3F)));
    } else if (value < 0x10000) {
      writer.WriteBits(8, (uint)(0xE0 | (value >> 12)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 6) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | (value & 0x3F)));
    } else if (value < 0x200000) {
      writer.WriteBits(8, (uint)(0xF0 | (value >> 18)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 12) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 6) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | (value & 0x3F)));
    } else if (value < 0x4000000) {
      writer.WriteBits(8, (uint)(0xF8 | (value >> 24)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 18) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 12) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 6) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | (value & 0x3F)));
    } else if (value < 0x80000000) {
      writer.WriteBits(8, (uint)(0xFC | (value >> 30)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 24) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 18) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 12) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 6) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | (value & 0x3F)));
    } else {
      writer.WriteBits(8, 0xFE);
      writer.WriteBits(8, (uint)(0x80 | ((value >> 30) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 24) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 18) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 12) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | ((value >> 6) & 0x3F)));
      writer.WriteBits(8, (uint)(0x80 | (value & 0x3F)));
    }
  }

  private static void WriteSignedBits(BitWriter writer, int value, int bits) {
    // Write the bottom 'bits' bits of the value (two's complement)
    var mask = (1u << bits) - 1;
    writer.WriteBits(bits, (uint)value & mask);
  }

  // CRC-8 polynomial 0x07 (x^8 + x^2 + x + 1) used in FLAC frame headers
  private static uint ComputeCrc8(byte[] data) {
    uint crc = 0;
    foreach (var b in data) {
      crc ^= b;
      for (var i = 0; i < 8; i++)
        crc = (crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1;
      crc &= 0xFF;
    }

    return crc;
  }

  // CRC-16 polynomial 0x8005 used in FLAC frames
  private static uint ComputeCrc16(byte[] data) {
    uint crc = 0;
    foreach (var b in data) {
      crc ^= (uint)b << 8;
      for (var i = 0; i < 8; i++)
        crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1;
      crc &= 0xFFFF;
    }

    return crc;
  }

  /// <summary>
  /// Bit writer for building FLAC frames bit by bit (big-endian, MSB-first).
  /// </summary>
  internal sealed class BitWriter {
    private readonly List<byte> _bytes = [];
    private byte _currentByte;
    private int _bitPos; // 0-7, next bit to write (MSB=0)

    /// <summary>Total number of bits written so far.</summary>
    public int BitCount => _bytes.Count * 8 + _bitPos;

    public void WriteBits(int count, uint value) {
      for (var i = count - 1; i >= 0; i--) {
        _currentByte = (byte)(((uint)_currentByte << 1) | ((value >> i) & 1u));
        _bitPos++;
        if (_bitPos == 8) {
          _bytes.Add(_currentByte);
          _currentByte = 0;
          _bitPos = 0;
        }
      }
    }

    public void PadToByte() {
      if (_bitPos > 0) {
        _currentByte <<= 8 - _bitPos;
        _bytes.Add(_currentByte);
        _currentByte = 0;
        _bitPos = 0;
      }
    }

    public byte[] ToByteArray() {
      if (_bitPos > 0) {
        var result = new byte[_bytes.Count + 1];
        _bytes.CopyTo(result);
        result[^1] = (byte)(_currentByte << (8 - _bitPos));
        return result;
      }

      return [.. _bytes];
    }
  }
}
