#pragma warning disable CS1591

using System.Buffers.Binary;

namespace Codec.Qoa;

/// <summary>
/// Quite OK Audio (QOA) lossy-but-deterministic codec — decoder plus a faithful
/// encoder. QOA is a fixed-bitrate (~3.2 bits/sample) DPCM scheme: audio is split
/// into frames of up to 256 slices per channel; each slice codes 20 samples as a
/// 4-bit scale-factor index followed by 20 × 3-bit residual indices. A per-channel
/// order-4 sign-LMS predictor (the <c>&gt;&gt;13</c> prediction shift and
/// <c>residual&gt;&gt;4</c> weight update) reconstructs each sample, and a fixed
/// dequantisation table (<see cref="DequantTab"/>) maps residual indices to signed
/// deltas. The whole pipeline is integer and deterministic, so re-encoding a decoded
/// stream is byte-stable and the decoder reproduces the reference output exactly.
/// <para>
/// The on-disk layout: an 8-byte file header (<c>'qoaf'</c> magic, then a 32-bit
/// big-endian total-samples-per-channel count); then frames. Each frame opens with
/// an 8-byte big-endian header packing channels (8 bits), sample-rate (24 bits),
/// frame samples-per-channel (16 bits) and frame byte-size (16 bits), followed by
/// per-channel 16-byte LMS state (4 × s16 history then 4 × s16 weights) and the
/// interleaved slices. Tables, predictor and bit packing are ported verbatim from
/// the reference <c>qoa.h</c> / ffmpeg <c>libavcodec/qoadec.c</c>.
/// </para>
/// </summary>
public static class QoaCodec {

  internal const uint Magic = 0x716f6166; // 'qoaf'
  internal const int LmsLen = 4;
  internal const int SliceLen = 20;
  internal const int SlicesPerFrame = 256;
  internal const int FrameLen = SlicesPerFrame * SliceLen; // 5120 samples/channel
  internal const int FrameHeaderBytes = 8;
  internal const int LmsStateBytes = LmsLen * 4; // 16 bytes per channel

  /// <summary>qoa_scalefactor_tab — index → reciprocal-style scale used during encode.</summary>
  public static readonly int[] ScaleFactorTab =
    [1, 7, 21, 45, 84, 138, 211, 304, 421, 562, 731, 928, 1157, 1419, 1715, 2048];

  /// <summary>qoa_reciprocal_tab — exact reciprocals matching the reference (used by qoa_div).</summary>
  internal static readonly int[] ReciprocalTab =
    [65536, 9363, 3121, 1457, 781, 475, 311, 216, 156, 117, 90, 71, 57, 47, 39, 32];

  /// <summary>qoa_dequant_tab[16][8] — ported verbatim from qoa.h.</summary>
  public static readonly int[][] DequantTab = [
    [   1,    -1,    3,    -3,    5,    -5,     7,     -7],
    [   5,    -5,   18,   -18,   32,   -32,    49,    -49],
    [  16,   -16,   53,   -53,   95,   -95,   147,   -147],
    [  34,   -34,  113,  -113,  203,  -203,   315,   -315],
    [  63,   -63,  210,  -210,  378,  -378,   588,   -588],
    [ 104,  -104,  345,  -345,  621,  -621,   966,   -966],
    [ 158,  -158,  528,  -528,  950,  -950,  1477,  -1477],
    [ 228,  -228,  760,  -760, 1368, -1368,  2128,  -2128],
    [ 316,  -316, 1053, -1053, 1895, -1895,  2947,  -2947],
    [ 422,  -422, 1405, -1405, 2529, -2529,  3934,  -3934],
    [ 548,  -548, 1828, -1828, 3290, -3290,  5117,  -5117],
    [ 696,  -696, 2320, -2320, 4176, -4176,  6496,  -6496],
    [ 868,  -868, 2893, -2893, 5207, -5207,  8099,  -8099],
    [1064, -1064, 3548, -3548, 6386, -6386,  9933,  -9933],
    [1286, -1286, 4288, -4288, 7718, -7718, 12005, -12005],
    [1536, -1536, 5120, -5120, 9216, -9216, 14336, -14336],
  ];

  /// <summary>qoa_quant_tab[17] — folds a quantised delta back to a 3-bit residual index.</summary>
  internal static readonly int[] QuantTab =
    [7, 7, 7, 5, 5, 3, 3, 1, 0, 0, 2, 2, 4, 4, 6, 6, 6];

  /// <summary>Per-channel order-4 sign-LMS predictor state.</summary>
  internal sealed class Lms {
    public readonly int[] History = new int[LmsLen];
    public readonly int[] Weights = new int[LmsLen];

    public int Predict() {
      var prediction = 0;
      for (var i = 0; i < LmsLen; ++i)
        prediction += this.Weights[i] * this.History[i];
      return prediction >> 13;
    }

    public void Update(int sample, int residual) {
      var delta = residual >> 4;
      for (var i = 0; i < LmsLen; ++i)
        this.Weights[i] += this.History[i] < 0 ? -delta : delta;
      for (var i = 0; i < LmsLen - 1; ++i)
        this.History[i] = this.History[i + 1];
      this.History[LmsLen - 1] = sample;
    }
  }

  /// <summary>Stream geometry exposed to container descriptors.</summary>
  public readonly record struct QoaStreamInfo(int Channels, int SampleRate, long SamplesPerChannel);

  private static int ClampS16(int v) => v < -32768 ? -32768 : v > 32767 ? 32767 : v;

  private static int Div(int v, int scaleFactor) {
    var reciprocal = ReciprocalTab[scaleFactor];
    var n = (v * reciprocal + (1 << 15)) >> 16;
    n = n + (v > 0 ? 1 : v < 0 ? -1 : 0) - (n > 0 ? 1 : n < 0 ? -1 : 0);
    return n;
  }

  // ── Header probe ───────────────────────────────────────────────────────────

  /// <summary>Reads the QOA file/first-frame headers without decoding audio.</summary>
  public static QoaStreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    return ReadStreamInfo(ms.ToArray());
  }

  private static QoaStreamInfo ReadStreamInfo(byte[] data) {
    if (data.Length < FrameHeaderBytes + FrameHeaderBytes)
      throw new InvalidDataException("Stream too short for a QOA header.");
    if (BinaryPrimitives.ReadUInt32BigEndian(data) != Magic)
      throw new InvalidDataException("Not a QOA stream: missing 'qoaf' magic.");

    var samples = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
    var frameHeader = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(8));
    var channels = (int)((frameHeader >> 56) & 0xFF);
    var sampleRate = (int)((frameHeader >> 32) & 0xFFFFFF);
    if (channels < 1)
      throw new InvalidDataException("QOA stream declares zero channels.");
    return new QoaStreamInfo(channels, sampleRate, samples);
  }

  // ── Decode ───────────────────────────────────────────────────────────────

  /// <summary>Decodes a QOA stream to raw interleaved little-endian 16-bit PCM.</summary>
  public static void Decompress(Stream qoaInput, Stream pcmOutput) {
    ArgumentNullException.ThrowIfNull(qoaInput);
    ArgumentNullException.ThrowIfNull(pcmOutput);
    using var ms = new MemoryStream();
    qoaInput.CopyTo(ms);
    var data = ms.ToArray();

    if (data.Length < FrameHeaderBytes)
      throw new InvalidDataException("Stream too short for a QOA file header.");
    if (BinaryPrimitives.ReadUInt32BigEndian(data) != Magic)
      throw new InvalidDataException("Not a QOA stream: missing 'qoaf' magic.");

    var totalSamples = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
    var pos = 8;
    long decoded = 0;

    Lms[]? lms = null;
    var channels = 0;

    while (decoded < totalSamples) {
      if (pos + FrameHeaderBytes > data.Length)
        throw new InvalidDataException("QOA frame header extends past end of stream.");

      var frameHeader = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos));
      var frameChannels = (int)((frameHeader >> 56) & 0xFF);
      var frameSamples = (int)((frameHeader >> 16) & 0xFFFF);
      var frameSize = (int)(frameHeader & 0xFFFF);

      if (frameChannels < 1)
        throw new InvalidDataException("QOA frame declares zero channels.");
      if (pos + frameSize > data.Length || frameSize < FrameHeaderBytes + LmsStateBytes * frameChannels)
        throw new InvalidDataException("QOA frame size is invalid.");

      if (lms == null || frameChannels != channels) {
        channels = frameChannels;
        lms = new Lms[channels];
        for (var c = 0; c < channels; ++c)
          lms[c] = new Lms();
      }

      var p = pos + FrameHeaderBytes;
      for (var c = 0; c < channels; ++c) {
        var history = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p));
        var weights = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p + 8));
        for (var i = 0; i < LmsLen; ++i) {
          lms[c].History[i] = (short)(history >> 48);
          history <<= 16;
          lms[c].Weights[i] = (short)(weights >> 48);
          weights <<= 16;
        }
        p += LmsStateBytes;
      }

      var outBuf = new byte[frameSamples * channels * 2];
      for (var sampleIndex = 0; sampleIndex < frameSamples; sampleIndex += SliceLen) {
        for (var c = 0; c < channels; ++c) {
          var slice = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p));
          p += 8;

          var scaleFactor = (int)((slice >> 60) & 0xF);
          slice <<= 4;

          var sliceEnd = Math.Min(sampleIndex + SliceLen, frameSamples);
          for (var si = sampleIndex; si < sliceEnd; ++si) {
            var predicted = lms[c].Predict();
            var quantized = (int)((slice >> 61) & 0x7);
            var dequantized = DequantTab[scaleFactor][quantized];
            var reconstructed = ClampS16(predicted + dequantized);

            var outIndex = (si * channels + c) * 2;
            BinaryPrimitives.WriteInt16LittleEndian(outBuf.AsSpan(outIndex), (short)reconstructed);
            slice <<= 3;

            lms[c].Update(reconstructed, dequantized);
          }
        }
      }

      pcmOutput.Write(outBuf);
      decoded += frameSamples;
      pos += frameSize;
    }
  }

  // ── Encode ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Encodes raw interleaved little-endian 16-bit PCM to a QOA stream. The encoder
  /// mirrors the reference: it brute-forces the best of the 16 scale-factors per
  /// slice by minimising squared error against the dequantised reconstruction, so
  /// its output decodes back to exactly the samples this codec would reconstruct.
  /// </summary>
  public static void Compress(Stream pcmInput, Stream qoaOutput, int channels, int sampleRate) {
    ArgumentNullException.ThrowIfNull(pcmInput);
    ArgumentNullException.ThrowIfNull(qoaOutput);
    if (channels is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(channels));
    if (sampleRate is < 1 or > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(sampleRate));

    using var ms = new MemoryStream();
    pcmInput.CopyTo(ms);
    var pcm = ms.ToArray();
    var frameBytes = channels * 2;
    if (pcm.Length % frameBytes != 0)
      throw new ArgumentException("PCM length is not a multiple of (channels × 2) bytes.");
    var totalSamples = pcm.Length / frameBytes;

    Span<byte> fileHeader = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(fileHeader, Magic);
    BinaryPrimitives.WriteUInt32BigEndian(fileHeader[4..], (uint)totalSamples);
    qoaOutput.Write(fileHeader);

    var lms = new Lms[channels];
    for (var c = 0; c < channels; ++c) {
      lms[c] = new Lms();
      // Reference initial weights: {0,0,-(1<<13),(1<<14)} for a gentle high-pass start.
      lms[c].Weights[0] = 0;
      lms[c].Weights[1] = 0;
      lms[c].Weights[2] = -(1 << 13);
      lms[c].Weights[3] = 1 << 14;
    }

    var sampleOffset = 0;
    while (sampleOffset < totalSamples) {
      var frameSamples = Math.Min(FrameLen, totalSamples - sampleOffset);
      var slicesPerChannel = (frameSamples + SliceLen - 1) / SliceLen;
      var frameSize = FrameHeaderBytes + LmsStateBytes * channels + slicesPerChannel * channels * 8;

      var frame = new byte[frameSize];
      var fp = 0;
      var frameHeader = ((ulong)(uint)channels << 56)
                      | ((ulong)(uint)sampleRate << 32)
                      | ((ulong)(uint)frameSamples << 16)
                      | (uint)frameSize;
      BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(fp), frameHeader);
      fp += FrameHeaderBytes;

      for (var c = 0; c < channels; ++c) {
        ulong history = 0, weights = 0;
        for (var i = 0; i < LmsLen; ++i) {
          history = (history << 16) | (ushort)(short)lms[c].History[i];
          weights = (weights << 16) | (ushort)(short)lms[c].Weights[i];
        }
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(fp), history);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(fp + 8), weights);
        fp += LmsStateBytes;
      }

      for (var sliceStart = 0; sliceStart < frameSamples; sliceStart += SliceLen) {
        for (var c = 0; c < channels; ++c) {
          var sliceEnd = Math.Min(sliceStart + SliceLen, frameSamples);
          var (slice, _) = EncodeSlice(pcm, sampleOffset, sliceStart, sliceEnd, channels, c, lms[c]);
          BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(fp), slice);
          fp += 8;
        }
      }

      qoaOutput.Write(frame);
      sampleOffset += frameSamples;
    }
  }

  private static (ulong Slice, double Error) EncodeSlice(
      byte[] pcm, int sampleOffset, int sliceStart, int sliceEnd, int channels, int channel, Lms lms) {
    var bestRank = double.MaxValue;
    ulong bestSlice = 0;
    Lms? bestLms = null;

    for (var sfi = 0; sfi < 16; ++sfi) {
      // Work on a private copy of the LMS state so the trial doesn't corrupt the real one.
      var trial = new Lms();
      Array.Copy(lms.History, trial.History, LmsLen);
      Array.Copy(lms.Weights, trial.Weights, LmsLen);

      var scaleFactor = (sfi + 0) & 0xF;
      var slice = (ulong)scaleFactor;
      double currentRank = 0;

      for (var si = sliceStart; si < sliceEnd; ++si) {
        var sampleIdx = (sampleOffset + si) * channels + channel;
        var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(sampleIdx * 2));

        var predicted = trial.Predict();
        var residual = sample - predicted;
        var scaled = Div(residual, scaleFactor);
        var clamped = scaled < -8 ? -8 : scaled > 8 ? 8 : scaled;
        var quantized = QuantTab[clamped + 8];
        var dequantized = DequantTab[scaleFactor][quantized];
        var reconstructed = ClampS16(predicted + dequantized);

        long err = sample - reconstructed;
        currentRank += err * err;

        slice = (slice << 3) | (uint)quantized;
        trial.Update(reconstructed, dequantized);
      }

      // Pad the slice when the trailing slice carries fewer than 20 samples.
      for (var si = sliceEnd - sliceStart; si < SliceLen; ++si)
        slice <<= 3;

      if (currentRank < bestRank) {
        bestRank = currentRank;
        bestSlice = slice;
        bestLms = trial;
      }
    }

    if (bestLms != null) {
      Array.Copy(bestLms.History, lms.History, LmsLen);
      Array.Copy(bestLms.Weights, lms.Weights, LmsLen);
    }
    return (bestSlice, bestRank);
  }
}
