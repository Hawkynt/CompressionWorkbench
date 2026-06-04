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
/// E-AC-3 (Dolby Digital Plus, bsid 16) is out of scope for decoding: <see cref="ReadStreamInfo"/>
/// still reports its header info, but <see cref="Decompress"/> throws <see cref="NotSupportedException"/>.
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

    long frames = 0;
    var pos = offset;
    while (pos + 6 <= data.Length && Ac3FrameHeader.TryParse(data, pos) is { } fh && fh.FrameSize > 0) {
      ++frames;
      pos += fh.FrameSize;
    }
    var duration = frames * BlocksPerFrame * SamplesPerBlock;

    return new Ac3StreamInfo(h.SampleRate, channels, h.Bitrate, h.Acmod, h.LowFrequencyEffects, h.IsEnhanced, duration);
  }

  /// <summary>
  /// Decodes an AC-3 stream into raw interleaved little-endian signed 16-bit PCM on
  /// <paramref name="output"/>. Channels are emitted in acmod order with LFE last. Throws
  /// <see cref="NotSupportedException"/> for E-AC-3 input.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var data = ReadAll(input);

    var pos = FindSync(data, 0);
    if (pos < 0)
      throw new InvalidDataException("AC-3 stream contains no parseable sync frame.");

    if (Ac3FrameHeader.TryParse(data, pos) is { IsEnhanced: true })
      throw new NotSupportedException("E-AC-3 (Dolby Digital Plus) decoding is not supported.");

    var decoder = new FrameDecoder();
    while (pos + 6 <= data.Length) {
      if (Ac3FrameHeader.TryParse(data, pos) is not { } header || header.FrameSize <= 0)
        break;
      if (header.IsEnhanced)
        break;                                  // mixed substreams: stop at first E-AC-3 frame
      if (pos + header.FrameSize > data.Length)
        break;                                  // truncated trailing frame

      var pcm = decoder.DecodeFrame(data, pos, header);
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
