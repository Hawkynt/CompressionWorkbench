#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// Stream-level metadata extracted from an AC-3 / E-AC-3 stream's first sync frame.
/// </summary>
public sealed record Ac3StreamInfo(
  int SampleRate, int Channels, int Bitrate, int Acmod, bool Lfe, bool IsEnhanced, long DurationSamples);

/// <summary>
/// Clean-room AC-3 (ATSC A/52, Dolby Digital) decoder. Decodes legacy AC-3 sync frames (bsid ≤ 10)
/// to interleaved little-endian signed 16-bit PCM at the stream's native channel count (full-
/// bandwidth channels in acmod order, with the LFE channel last when present). The full A/52 audio
/// pipeline is implemented: per-block exponent strategies (D15/D25/D45 + reuse), the parametric bit
/// allocation model (slow/fast decay, gains, floor, snroffset, delta bit allocation), channel
/// coupling (coupling-channel reconstruction via coupling coordinates), 2/0 rematrixing, grouped /
/// linear mantissa dequantization with deterministic dither, and the 512/256-point IMDCT with the
/// A/52 window and overlap-add.
/// <para>
/// E-AC-3 (Dolby Digital Plus, ATSC A/52 Annex E, bsid 11..16) is also decoded: independent
/// substreams (frame type 0/2) decode to PCM — including the variable block count (1/2/3/6 blocks),
/// the half-rate sample rates (fscod 3 + fscod2), the LUT-based per-frame exponent strategy, the
/// adaptive hybrid transform (AHT with GAQ vector-quantized pre-mantissas + 6-point inverse DCT) and
/// standard coupling. Dependent substreams (frame type 1) are skipped; enhanced coupling (ecplinu)
/// raises <see cref="NotSupportedException"/>; spectral extension (spx) is parsed but its
/// high-frequency reconstruction is not synthesised. <see cref="ReadStreamInfo"/> reports the
/// enhanced flag.
/// </para>
/// </summary>
public static class Ac3Codec {

  private const int BlocksPerFrame = 6;
  private const int SamplesPerBlock = 256;

  /// <summary>Reads stream-level info (sample rate, native channel count, bitrate, duration) from the first sync frame.</summary>
  public static Ac3StreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var data = ReadAll(input);
    var offset = FindSync(data, 0);
    if (offset < 0 || Ac3FrameHeader.TryParse(data, offset) is not { } h)
      throw new InvalidDataException("AC-3 stream contains no parseable sync frame.");

    var channels = Ac3FrameHeader.AcmodChannelCount(h.Acmod) + (h.LowFrequencyEffects ? 1 : 0);

    // Accumulate decoded sample frames. Each AC-3 frame is 6 blocks; an E-AC-3 frame carries a
    // variable block count, and only its primary independent substream (id 0) contributes samples.
    long sampleFrames = 0;
    var pos = offset;
    while (pos + 6 <= data.Length && Ac3FrameHeader.TryParse(data, pos) is { } fh && fh.FrameSize > 0) {
      if (!fh.IsDependentSubstream && !(fh.IsEnhanced && fh.SubstreamId != 0))
        sampleFrames += fh.IsEnhanced ? fh.NumBlocks : BlocksPerFrame;
      pos += fh.FrameSize;
    }
    var duration = sampleFrames * SamplesPerBlock;

    return new Ac3StreamInfo(h.SampleRate, channels, h.Bitrate, h.Acmod, h.LowFrequencyEffects, h.IsEnhanced, duration);
  }

  /// <summary>
  /// Decodes an AC-3 / E-AC-3 stream into raw interleaved little-endian signed 16-bit PCM on
  /// <paramref name="output"/>. Channels are emitted in acmod order with LFE last. AC-3 (bsid ≤ 10)
  /// and E-AC-3 independent substreams (bsid 11..16, frame type 0/2) decode; E-AC-3 dependent
  /// substreams (frame type 1) are skipped. Throws <see cref="NotSupportedException"/> only for
  /// E-AC-3 enhanced coupling.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var data = ReadAll(input);

    var pos = FindSync(data, 0);
    if (pos < 0)
      throw new InvalidDataException("AC-3 stream contains no parseable sync frame.");

    var decoder = new FrameDecoder();
    var eacDecoder = new Ac3EnhancedFrameDecoder();
    while (pos + 6 <= data.Length) {
      if (Ac3FrameHeader.TryParse(data, pos) is not { } header || header.FrameSize <= 0)
        break;
      if (pos + header.FrameSize > data.Length)
        break;                                  // truncated trailing frame

      if (header.IsDependentSubstream || (header.IsEnhanced && header.SubstreamId != 0)) {
        pos += header.FrameSize;                // skip dependent / non-primary substreams
        continue;
      }

      var pcm = header.IsEnhanced
        ? eacDecoder.DecodeFrame(data, pos, header)
        : decoder.DecodeFrame(data, pos, header);
      if (pcm == null)
        break;                                  // undecodable frame — stop gracefully
      output.Write(pcm, 0, pcm.Length);
      pos += header.FrameSize;
    }
  }

  // -- helpers ---------------------------------------------------------------

  private static byte[] ReadAll(Stream input) {
    if (input is MemoryStream ms && ms.TryGetBuffer(out var seg)) {
      var copy = new byte[seg.Count];
      Array.Copy(seg.Array!, seg.Offset, copy, 0, seg.Count);
      return copy;
    }
    using var tmp = new MemoryStream();
    input.CopyTo(tmp);
    return tmp.ToArray();
  }

  private static int FindSync(byte[] data, int start) {
    for (var i = start; i + 1 < data.Length; ++i)
      if (data[i] == 0x0B && data[i + 1] == 0x77 && Ac3FrameHeader.TryParse(data, i) is not null)
        return i;
    return -1;
  }
}
