#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.InterplayAcm;

/// <summary>
/// Clean-room Interplay ACM encoder derived from the public decoder transform.
/// The synthesis stage used by Interplay/FFmpeg is inverted with a regularized
/// least-squares analysis filter, then quantized into the format's amplitude
/// table and universally-defined zero/linear filler modes. No historical encoder
/// implementation is copied or translated.
/// </summary>
public static class InterplayAcmEncoder {
  private const int HeaderSize = 14;
  private const int MaxRows = 0x0FFF;
  private const int MaxLinearBits = 15;
  private const int MaxPositiveIndex = (1 << (MaxLinearBits - 1)) - 1;
  private const int MaxNegativeIndexMagnitude = 1 << (MaxLinearBits - 1);

  /// <summary>Encodes interleaved signed PCM16 samples as a standalone Interplay ACM file.</summary>
  public static byte[] Encode(ReadOnlySpan<short> samples, int channels, int sampleRate, int level = 7, int rows = 16) {
    using var output = new MemoryStream();
    Encode(samples, output, channels, sampleRate, level, rows);
    return output.ToArray();
  }

  /// <summary>Encodes interleaved signed PCM16 samples as a standalone Interplay ACM file.</summary>
  public static void Encode(ReadOnlySpan<short> samples, Stream output, int channels, int sampleRate, int level = 7, int rows = 16) {
    ArgumentNullException.ThrowIfNull(output);
    ValidateGeometry(channels, sampleRate, level, rows, samples.Length);

    Span<byte> header = stackalloc byte[HeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header, InterplayAcmCodec.Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)samples.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(header[8..], checked((ushort)channels));
    BinaryPrimitives.WriteUInt16LittleEndian(header[10..], checked((ushort)sampleRate));
    BinaryPrimitives.WriteUInt16LittleEndian(header[12..], checked((ushort)((rows << 4) | level)));
    output.Write(header);

    if (samples.IsEmpty)
      return;

    var cols = 1 << level;
    var blockLength = checked(rows * cols);
    var wrap = new int[Math.Max(0, 2 * cols - 2)];
    var writer = new LsbBitWriter(output);

    for (var offset = 0; offset < samples.Length; offset += blockLength) {
      var target = new int[blockLength];
      var count = Math.Min(blockLength, samples.Length - offset);
      for (var i = 0; i < count; ++i)
        target[i] = samples[offset + i] << level;

      var coefficients = AnalyzeBlock(target, level, rows, wrap);
      var packed = Quantize(coefficients, cols, rows);
      WriteBlock(writer, packed.Indices, packed.Step, packed.LinearBits, cols, rows);

      if (level > 0) {
        var reconstructed = new int[blockLength];
        for (var i = 0; i < reconstructed.Length; ++i)
          reconstructed[i] = packed.Indices[i] * packed.Step;
        SynthesizeBlock(reconstructed, level, rows, wrap);
      }
    }

    writer.Flush();
  }

  /// <summary>Returns whether this header geometry can carry at least a zero block within FFmpeg's block buffer.</summary>
  public static bool IsGeometryEncodable(int level, int rows) {
    if (level is < 0 or > 15 || rows is < 1 or > MaxRows)
      return false;
    var cols = 1L << level;
    var blockBits = checked(rows * cols * 8L);
    var minimumBits = 20L + cols * 5L;
    return minimumBits <= blockBits;
  }

  private static void ValidateGeometry(int channels, int sampleRate, int level, int rows, int sampleCount) {
    if (channels is < 1 or > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(channels), "Interplay ACM stores the channel count in an unsigned 16-bit field.");
    if (sampleRate is < 1 or > ushort.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(sampleRate), "Interplay ACM stores the sample rate in an unsigned 16-bit field.");
    if (level is < 0 or > 15)
      throw new ArgumentOutOfRangeException(nameof(level), "Interplay ACM level is a four-bit value (0..15).");
    if (rows is < 1 or > MaxRows)
      throw new ArgumentOutOfRangeException(nameof(rows), "Interplay ACM rows is a twelve-bit value (1..4095).");
    if ((long)sampleCount > uint.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(sampleCount), "Interplay ACM stores total samples in an unsigned 32-bit field.");
    if (!IsGeometryEncodable(level, rows))
      throw new NotSupportedException($"ACM level={level}, rows={rows} cannot fit even the mandatory block header/filler indices into its {rows * (1 << level)}-byte decoder block buffer.");
  }

  private static int[] AnalyzeBlock(int[] target, int level, int rows, int[] decoderWrap) {
    if (level == 0)
      return (int[])target.Clone();

    var cols = 1 << level;
    var result = (int[])target.Clone();
    var analysisWrap = (int[])decoderWrap.Clone();
    var stepRows = level > 9 ? 1 : (2048 >> level) - 2;
    var remaining = rows;
    var blockOffset = 0;
    var lambda = level >= 10 ? 0.5 : level >= 8 ? 0.1 : 0.02;

    while (true) {
      var chunkRows = Math.Min(stepRows, remaining);
      AnalyzeChunk(result, analysisWrap, level, cols, blockOffset, chunkRows, lambda);

      var chunkLength = checked(chunkRows * cols);
      var synthesized = result.AsSpan(blockOffset, chunkLength).ToArray();
      SynthesizeChunk(synthesized, analysisWrap, level, cols, 0, chunkRows);

      if (remaining <= stepRows)
        break;
      remaining -= stepRows;
      blockOffset += stepRows << level;
    }

    return result;
  }

  private static void AnalyzeChunk(int[] block, int[] wrap, int level, int cols, int blockOffset, int rows, double lambda) {
    var stages = BuildStages(cols, rows);
    for (var i = stages.Length - 1; i >= 1; --i)
      AnalyzeStage(block, wrap, stages[i], blockOffset, lambda);

    var first = stages[0];
    var p = blockOffset;
    for (var i = 0; i < first.SubCount; ++i) {
      --block[p];
      p += first.SubLength;
    }
    AnalyzeStage(block, wrap, first, blockOffset, lambda);
  }

  private static void AnalyzeStage(int[] block, int[] wrap, Stage stage, int blockOffset, double lambda) {
    for (var lane = 0; lane < stage.SubLength; ++lane) {
      var count = stage.SubCount;
      var desired = new double[count];
      var p = blockOffset + lane;
      for (var i = 0; i < count; ++i, p += stage.SubLength)
        desired[i] = block[p];

      var x = SolveLeastSquares(
        desired,
        wrap[stage.WrapOffset + lane * 2],
        wrap[stage.WrapOffset + lane * 2 + 1],
        lambda);

      p = blockOffset + lane;
      for (var i = 0; i < count; ++i, p += stage.SubLength)
        block[p] = ClampToInt(Math.Round(x[i], MidpointRounding.AwayFromZero));
    }
  }

  private static double[] SolveLeastSquares(double[] desired, int previous0, int previous1, double lambda) {
    var n = desired.Length;
    var b = (double[])desired.Clone();
    b[0] -= previous0 + 2d * previous1;
    if (n > 1)
      b[1] += previous1;

    var rhs = new double[n];
    ApplyTranspose(b, rhs);
    var x = new double[n];
    var r = (double[])rhs.Clone();
    var p = (double[])rhs.Clone();
    var ap = new double[n];
    var synthesis = new double[n];
    var rr = Dot(r, r);
    if (rr == 0)
      return x;

    const int iterations = 20;
    for (var iteration = 0; iteration < iterations; ++iteration) {
      Array.Clear(synthesis);
      ApplySynthesis(p, synthesis);
      Array.Clear(ap);
      ApplyTranspose(synthesis, ap);
      for (var i = 0; i < n; ++i)
        ap[i] += lambda * p[i];

      var denominator = Dot(p, ap);
      if (denominator <= 1e-20)
        break;
      var alpha = rr / denominator;
      for (var i = 0; i < n; ++i) {
        x[i] += alpha * p[i];
        r[i] -= alpha * ap[i];
      }

      var next = Dot(r, r);
      if (next <= rr * 1e-12)
        break;
      var beta = next / rr;
      for (var i = 0; i < n; ++i)
        p[i] = r[i] + beta * p[i];
      rr = next;
    }

    return x;
  }

  private static void ApplySynthesis(ReadOnlySpan<double> x, Span<double> y) {
    if (x.IsEmpty)
      return;
    y[0] = x[0];
    if (x.Length > 1)
      y[1] = 2 * x[0] - x[1];
    for (var pair = 1; pair < x.Length / 2; ++pair) {
      var even = pair * 2;
      y[even] = x[even - 2] + 2 * x[even - 1] + x[even];
      if (even + 1 < x.Length)
        y[even + 1] = 2 * x[even] - x[even - 1] - x[even + 1];
    }
  }

  private static void ApplyTranspose(ReadOnlySpan<double> y, Span<double> x) {
    if (y.IsEmpty)
      return;
    x[0] += y[0];
    if (y.Length > 1) {
      x[0] += 2 * y[1];
      x[1] -= y[1];
    }
    for (var pair = 1; pair < y.Length / 2; ++pair) {
      var even = pair * 2;
      x[even - 2] += y[even];
      x[even - 1] += 2 * y[even];
      x[even] += y[even];
      if (even + 1 < y.Length) {
        x[even] += 2 * y[even + 1];
        x[even - 1] -= y[even + 1];
        x[even + 1] -= y[even + 1];
      }
    }
  }

  private static double Dot(ReadOnlySpan<double> left, ReadOnlySpan<double> right) {
    var result = 0d;
    for (var i = 0; i < left.Length; ++i)
      result += left[i] * right[i];
    return result;
  }

  private static PackedBlock Quantize(int[] coefficients, int cols, int rows) {
    long maxPositive = 0, maxNegativeMagnitude = 0;
    foreach (var coefficient in coefficients) {
      if (coefficient >= 0)
        maxPositive = Math.Max(maxPositive, coefficient);
      else
        maxNegativeMagnitude = Math.Max(maxNegativeMagnitude, -(long)coefficient);
    }

    var step = (int)Math.Max(1,
      Math.Max(CeilDiv(maxPositive, MaxPositiveIndex), CeilDiv(maxNegativeMagnitude, MaxNegativeIndexMagnitude)));
    step = Math.Min(ushort.MaxValue, step);
    var blockBitBudget = checked((long)coefficients.Length * 8);

    for (var attempt = 0; attempt < 20; ++attempt) {
      var indices = QuantizeAtStep(coefficients, step);
      var (linearBits, usedBits) = Measure(indices, cols, rows);
      if (usedBits <= blockBitBudget)
        return new PackedBlock(indices, step, linearBits);
      if (step == ushort.MaxValue)
        break;
      step = (int)Math.Min(ushort.MaxValue, Math.Max(step + 1L, step * 2L));
    }

    var zeros = new int[coefficients.Length];
    var (_, minimumBits) = Measure(zeros, cols, rows);
    if (minimumBits > blockBitBudget)
      throw new NotSupportedException("ACM geometry cannot fit its mandatory per-block framing.");
    return new PackedBlock(zeros, 0, 3);
  }

  private static int[] QuantizeAtStep(int[] coefficients, int step) {
    var result = new int[coefficients.Length];
    for (var i = 0; i < result.Length; ++i) {
      var value = (long)coefficients[i];
      var q = value >= 0
        ? (value + step / 2) / step
        : -((-value + step / 2) / step);
      result[i] = (int)Math.Clamp(q, -MaxNegativeIndexMagnitude, MaxPositiveIndex);
    }
    return result;
  }

  private static (int LinearBits, long UsedBits) Measure(int[] indices, int cols, int rows) {
    var min = 0;
    var max = 0;
    foreach (var value in indices) {
      min = Math.Min(min, value);
      max = Math.Max(max, value);
    }

    var bits = 3;
    while (bits < MaxLinearBits && (min < -(1 << (bits - 1)) || max > (1 << (bits - 1)) - 1))
      ++bits;

    long used = 20;
    for (var col = 0; col < cols; ++col) {
      var nonZero = false;
      for (var row = 0; row < rows && !nonZero; ++row)
        nonZero = indices[(row << BitOperations.Log2((uint)cols)) + col] != 0;
      used += 5 + (nonZero ? (long)rows * bits : 0);
    }
    return (bits, used);
  }

  private static void WriteBlock(LsbBitWriter writer, int[] indices, int step, int linearBits, int cols, int rows) {
    writer.WriteBits((uint)(step == 0 ? 0 : linearBits - 1), 4);
    writer.WriteBits((uint)step, 16);
    var middle = 1 << (linearBits - 1);
    var level = BitOperations.Log2((uint)cols);

    for (var col = 0; col < cols; ++col) {
      var nonZero = false;
      for (var row = 0; row < rows && !nonZero; ++row)
        nonZero = indices[(row << level) + col] != 0;
      if (!nonZero) {
        writer.WriteBits(0, 5);
        continue;
      }

      writer.WriteBits((uint)linearBits, 5);
      for (var row = 0; row < rows; ++row)
        writer.WriteBits((uint)(indices[(row << level) + col] + middle), linearBits);
    }
  }

  private static void SynthesizeBlock(int[] block, int level, int rows, int[] wrap) {
    var cols = 1 << level;
    var stepRows = level > 9 ? 1 : (2048 >> level) - 2;
    var remaining = rows;
    var blockOffset = 0;
    while (true) {
      var chunkRows = Math.Min(stepRows, remaining);
      SynthesizeChunk(block, wrap, level, cols, blockOffset, chunkRows);
      if (remaining <= stepRows)
        break;
      remaining -= stepRows;
      blockOffset += stepRows << level;
    }
  }

  private static void SynthesizeChunk(int[] block, int[] wrap, int level, int cols, int blockOffset, int rows) {
    var stages = BuildStages(cols, rows);
    SynthesizeStage(block, wrap, stages[0], blockOffset);
    var first = stages[0];
    var p = blockOffset;
    for (var i = 0; i < first.SubCount; ++i) {
      unchecked { ++block[p]; }
      p += first.SubLength;
    }
    for (var i = 1; i < stages.Length; ++i)
      SynthesizeStage(block, wrap, stages[i], blockOffset);
  }

  private static void SynthesizeStage(int[] block, int[] wrap, Stage stage, int blockOffset) {
    for (var lane = 0; lane < stage.SubLength; ++lane) {
      var p = blockOffset + lane;
      var wrapOffset = stage.WrapOffset + lane * 2;
      var r0 = unchecked((uint)wrap[wrapOffset]);
      var r1 = unchecked((uint)wrap[wrapOffset + 1]);
      for (var pair = 0; pair < stage.SubCount / 2; ++pair) {
        var r2 = unchecked((uint)block[p]);
        block[p] = unchecked((int)(r1 * 2 + r0 + r2));
        p += stage.SubLength;
        var r3 = unchecked((uint)block[p]);
        block[p] = unchecked((int)(r2 * 2 - r1 - r3));
        p += stage.SubLength;
        r0 = r2;
        r1 = r3;
      }
      wrap[wrapOffset] = unchecked((int)r0);
      wrap[wrapOffset + 1] = unchecked((int)r1);
    }
  }

  private static Stage[] BuildStages(int cols, int rows) {
    var level = BitOperations.Log2((uint)cols);
    var stages = new Stage[level];
    var subLength = cols / 2;
    var subCount = rows * 2;
    var wrapOffset = 0;
    for (var i = 0; i < stages.Length; ++i) {
      stages[i] = new Stage(wrapOffset, subLength, subCount);
      wrapOffset += subLength * 2;
      subLength /= 2;
      subCount *= 2;
    }
    return stages;
  }

  private static long CeilDiv(long value, long divisor) => value == 0 ? 0 : (value + divisor - 1) / divisor;

  private static int ClampToInt(double value) => value switch {
    > int.MaxValue => int.MaxValue,
    < int.MinValue => int.MinValue,
    _ => (int)value,
  };

  private readonly record struct Stage(int WrapOffset, int SubLength, int SubCount);
  private readonly record struct PackedBlock(int[] Indices, int Step, int LinearBits);

  private sealed class LsbBitWriter(Stream output) {
    private ulong _buffer;
    private int _count;

    public void WriteBits(uint value, int count) {
      this._buffer |= (ulong)(value & ((1u << count) - 1)) << this._count;
      this._count += count;
      while (this._count >= 8) {
        output.WriteByte((byte)this._buffer);
        this._buffer >>= 8;
        this._count -= 8;
      }
    }

    public void Flush() {
      if (this._count > 0)
        output.WriteByte((byte)this._buffer);
      this._buffer = 0;
      this._count = 0;
    }
  }
}
