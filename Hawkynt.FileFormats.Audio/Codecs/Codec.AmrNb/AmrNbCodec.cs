#pragma warning disable CS1591
namespace Codec.AmrNb;

/// <summary>
/// AMR narrowband (GSM-AMR, 3GPP TS 26.090) speech <b>decoder</b>. Eight active ACELP modes
/// (4.75 / 5.15 / 5.90 / 6.70 / 7.40 / 7.95 / 10.2 / 12.2 kbit/s) plus the SID comfort-noise and
/// NO_DATA frame types. Each 20 ms frame decodes to exactly 160 samples of 16-bit PCM at 8 kHz mono.
/// <para>
/// The decode pipeline is a faithful float port of ffmpeg <c>libavcodec/amrnbdec.c</c>: per-mode
/// bit unpacking via the <c>order_*</c> reordering tables, split-vector LSF dequantisation → LSP →
/// interpolated LPC, fractional-pitch adaptive codebook, the mode-specific algebraic fixed codebook
/// (2/3/4/8/10 pulses), pitch/fixed gain VQ with MA prediction, LP synthesis, the AMR adaptive
/// postfilter (formant + tilt + AGC) and the order-2 high-pass. The IF1/.amr storage layout (mode
/// byte then sorted payload bits) is what the descriptor feeds in.
/// </para>
/// <para>The implementation uses floats, so it is not bit-exact with the 3GPP fixed-point
/// reference (ffmpeg measures PSNR 30..80 dB versus the reference), but the algorithm is
/// reproduced structurally. SID/NO_DATA frames emit 160 samples of silence (DTX comfort-noise
/// synthesis is not modelled); this is surfaced honestly in the container metadata.</para>
/// </summary>
public static partial class AmrNbCodec {

  /// <summary>Samples produced per frame (160 @ 8 kHz = 20 ms).</summary>
  public const int SamplesPerFrame = AmrNbData.SamplesPerFrame;

  /// <summary>The AMR-NB sample rate.</summary>
  public const int SampleRate = 8000;

  /// <summary>Per-frame walk result: the frame index, its decoded mode and its total byte size
  /// (header byte + payload).</summary>
  public readonly record struct FrameInfo(int Index, AmrNbMode Mode, int SizeBytes);

  /// <summary>
  /// Maps a 4-bit frame type to its mode, or <see cref="AmrNbMode.NoData"/> for any
  /// reserved/lost/no-data type. Provenance: 3GPP TS 26.101 Table 1a.
  /// </summary>
  public static AmrNbMode ModeFromFrameType(int frameType) => frameType switch {
    >= 0 and <= 7 => (AmrNbMode)frameType,
    8 => AmrNbMode.MrdtxSid,
    _ => AmrNbMode.NoData,
  };

  /// <summary>The payload byte count (excluding the 1-byte header) for a 4-bit frame type.</summary>
  public static int PayloadBytes(int frameType) =>
    frameType >= 0 && frameType < AmrNbData.PayloadBytes.Length ? AmrNbData.PayloadBytes[frameType] : 0;

  /// <summary>
  /// Walks an IF1 storage-format byte stream (no <c>#!AMR\n</c> magic), where each frame begins
  /// with a header byte whose bits 3..6 are the frame type, sizing each frame from the mode's byte
  /// table. A trailing fragment too short for its declared payload is ignored.
  /// </summary>
  public static IReadOnlyList<FrameInfo> ReadInfo(ReadOnlySpan<byte> stream) {
    var list = new List<FrameInfo>();
    var pos = 0;
    var index = 0;
    while (pos < stream.Length) {
      var frameType = (stream[pos] >> 3) & 0x0F;
      var size = 1 + PayloadBytes(frameType);
      if (pos + size > stream.Length)
        break;
      list.Add(new FrameInfo(index++, ModeFromFrameType(frameType), size));
      pos += size;
    }
    return list;
  }

  /// <summary>Counts fully-present frames (a trailing truncated frame is not counted).</summary>
  public static int CountFrames(ReadOnlySpan<byte> stream) => ReadInfo(stream).Count;

  /// <summary>
  /// Decodes an IF1 AMR-NB storage stream (magic already stripped) to 16-bit PCM at 8 kHz mono.
  /// Output length is <c>frameCount × 160</c> samples.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> stream) {
    var infos = ReadInfo(stream);
    if (infos.Count == 0)
      return [];

    var output = new short[infos.Count * SamplesPerFrame];
    var decoder = new AmrNbDecoder();
    var pos = 0;
    for (var f = 0; f < infos.Count; f++) {
      var info = infos[f];
      var payload = stream.Slice(pos + 1, info.SizeBytes - 1);
      decoder.DecodeFrame(payload, info.Mode, output.AsSpan(f * SamplesPerFrame, SamplesPerFrame));
      pos += info.SizeBytes;
    }
    return output;
  }
}
