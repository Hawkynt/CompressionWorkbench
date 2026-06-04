#pragma warning disable CS1591
namespace Codec.G7231;

/// <summary>
/// ITU-T G.723.1 dual-rate (5.3 / 6.3 kbit/s) speech <b>decoder</b>, a faithful fixed-point
/// port of FFmpeg's <c>libavcodec/g723_1dec.c</c> + <c>g723_1.c</c>. G.723.1 has no encoder here
/// (FFmpeg ships none either), so this type only synthesizes 16-bit linear PCM at 8000 Hz mono.
/// <para>
/// The coded stream is a sequence of frames; the low two bits of each frame's first byte select
/// the frame type and therefore its size (<see cref="G7231Tables.FrameSize"/>):
/// <list type="bullet">
///   <item><b>0</b> — 24-byte active frame, 6.3 kbit/s MP-MLQ excitation.</item>
///   <item><b>1</b> — 20-byte active frame, 5.3 kbit/s ACELP algebraic codebook.</item>
///   <item><b>2</b> — 4-byte SID (Silence Insertion Descriptor) frame → comfort noise.</item>
///   <item><b>3</b> — 1-byte untransmitted frame → comfort noise continuation.</item>
/// </list>
/// Every frame decodes to exactly <see cref="G7231Tables.FrameLen"/> (240) samples.
/// </para>
/// <para>
/// Pipeline (all faithful to the reference, fixed-point bit-exact where the reference is): frame
/// unpack → LSP inverse-quantization (DC + 3-band VQ, predictive add-back, stability ordering) →
/// LSP→LPC interpolation over the four subframes (the reference <c>lsp2lpc</c>) → adaptive-codebook
/// excitation (fractional pitch via the residual-gain codebooks) + fixed-codebook excitation
/// (MP-MLQ pulses/grid/dirac-train at 6.3k, the ACELP algebraic codebook + harmonic enhancement at
/// 5.3k) → LP synthesis filter → pitch + formant postfilter with adaptive gain control. Frame
/// erasure (a forbidden code) is concealed by LSP freezing and residual interpolation; SID and
/// untransmitted frames drive the comfort-noise generator (CNG). The postfilter is enabled by
/// default, matching FFmpeg's default option.
/// </para>
/// </summary>
public static class G7231Codec {

  /// <summary>Decoded samples produced by every frame (240 @ 8000 Hz = 30 ms).</summary>
  public const int SamplesPerFrame = G7231Tables.FrameLen;

  /// <summary>Per-frame walk result: the frame type and its byte size.</summary>
  public readonly record struct FrameInfo(int Index, G7231FrameType Type, int SizeBytes);

  /// <summary>
  /// Walks <paramref name="frames"/> dispatching each frame by its 2-bit selector, returning one
  /// <see cref="FrameInfo"/> per fully-present frame. A trailing fragment too short for its
  /// declared size is ignored (truncation tolerance).
  /// </summary>
  public static IReadOnlyList<FrameInfo> ReadInfo(ReadOnlySpan<byte> frames) {
    var list = new List<FrameInfo>();
    var pos = 0;
    var index = 0;
    while (pos < frames.Length) {
      var mode = frames[pos] & 3;
      var size = G7231Tables.FrameSize[mode];
      if (pos + size > frames.Length)
        break; // truncated trailing frame
      list.Add(new FrameInfo(index++, (G7231FrameType)mode, size));
      pos += size;
    }

    return list;
  }

  /// <summary>
  /// Counts the fully-present frames in <paramref name="frames"/> (a trailing truncated frame is
  /// not counted). The decoded sample count is <c>count × 240</c>.
  /// </summary>
  public static int CountFrames(ReadOnlySpan<byte> frames) => ReadInfo(frames).Count;

  /// <summary>
  /// Decodes a G.723.1 bitstream (one or more concatenated frames, auto-detecting each frame's
  /// size from its 2-bit selector) to 16-bit linear PCM at 8000 Hz mono. The output length is
  /// <c>frameCount × 240</c> samples.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> frames) => Decode(frames, postfilter: true);

  /// <summary>
  /// Decodes as <see cref="Decode(ReadOnlySpan{byte})"/>, with control over the postfilter chain
  /// (pitch + formant postfilter and adaptive gain). Disabling it matches FFmpeg's
  /// <c>-postfilter 0</c> path (output is simply scaled by 2).
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> frames, bool postfilter) {
    var infos = ReadInfo(frames);
    if (infos.Count == 0)
      return [];

    var output = new short[infos.Count * G7231Tables.FrameLen];
    var decoder = new G7231Decoder(postfilter);
    for (var f = 0; f < infos.Count; ++f) {
      var info = infos[f];
      decoder.DecodeFrame(frames.Slice(SumSizes(infos, f), info.SizeBytes),
                          output.AsSpan(f * G7231Tables.FrameLen, G7231Tables.FrameLen));
    }

    return output;
  }

  private static int SumSizes(IReadOnlyList<FrameInfo> infos, int upTo) {
    var sum = 0;
    for (var i = 0; i < upTo; ++i)
      sum += infos[i].SizeBytes;
    return sum;
  }
}

/// <summary>G.723.1 frame type, matching the 2-bit frame selector.</summary>
public enum G7231FrameType {
  /// <summary>Active speech (6.3k if 0-rate bit, 5.3k otherwise).</summary>
  Active = 0,
  /// <summary>5.3 kbit/s active speech (selector value 1).</summary>
  Active5300 = 1,
  /// <summary>Silence Insertion Descriptor frame.</summary>
  Sid = 2,
  /// <summary>Untransmitted frame (comfort-noise continuation).</summary>
  Untransmitted = 3,
}
