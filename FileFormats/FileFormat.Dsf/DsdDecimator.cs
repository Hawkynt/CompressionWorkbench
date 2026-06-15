#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Dsf;

/// <summary>
/// Crude DSD (1-bit, ~2.8224&#160;MHz) → 16-bit LE PCM decimator shared by the DSF and
/// DSDIFF channel pseudo-archives. It is a plain windowed accumulator, NOT a proper
/// anti-aliasing FIR: for every output sample it sums the next 64 input bits mapped to
/// ±1 (a 1-bit equals +1, a 0-bit equals −1), giving a running value in the range
/// −64..+64, then scales that by 512 (clamped to the 16-bit range) to fill a recognizable
/// portion of the PCM dynamic range. The result is a heavily aliased but audible/inspectable
/// mono signal at <c>fs / 64</c>. This is deliberately an approximation suitable for channel
/// inspection only; high-fidelity playback would require a multi-tap low-pass decimation FIR.
/// </summary>
public static class DsdDecimator {

  /// <summary>The DSD-to-PCM decimation ratio (one PCM sample per 64 DSD bits).</summary>
  public const int DecimationFactor = 64;

  /// <summary>
  /// Decimates one channel's raw DSD bitstream into 16-bit little-endian PCM. Bits are read
  /// from <paramref name="dsdBytes"/> in <paramref name="lsbFirst"/> order within each byte
  /// (DSF uses LSB-first; DSDIFF uses MSB-first). At most <paramref name="bitCount"/> bits are
  /// consumed; if it is negative the whole buffer is used. Only whole windows of 64 bits
  /// produce output, so a partial trailing window is dropped.
  /// </summary>
  public static byte[] DecimateToPcm16(ReadOnlySpan<byte> dsdBytes, bool lsbFirst, long bitCount = -1) {
    var totalBits = (long)dsdBytes.Length * 8;
    if (bitCount < 0 || bitCount > totalBits)
      bitCount = totalBits;

    var outSamples = (int)(bitCount / DecimationFactor);
    var pcm = new byte[outSamples * 2];

    var bitIndex = 0L;
    for (var s = 0; s < outSamples; ++s) {
      var sum = 0;
      for (var k = 0; k < DecimationFactor; ++k, ++bitIndex) {
        var byteIndex = (int)(bitIndex >> 3);
        var bitInByte = (int)(bitIndex & 7);
        var shift = lsbFirst ? bitInByte : 7 - bitInByte;
        var bit = (dsdBytes[byteIndex] >> shift) & 1;
        sum += bit == 1 ? 1 : -1;
      }

      var scaled = sum * 512;
      if (scaled > short.MaxValue) scaled = short.MaxValue;
      else if (scaled < short.MinValue) scaled = short.MinValue;
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(s * 2), (short)scaled);
    }

    return pcm;
  }
}
