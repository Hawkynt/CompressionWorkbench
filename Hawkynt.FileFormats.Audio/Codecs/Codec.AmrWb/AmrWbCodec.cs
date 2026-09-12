#pragma warning disable CS1591
namespace Codec.AmrWb;

/// <summary>
/// AMR wideband (G.722.2 / 3GPP TS 26.190) speech codec. Nine active ACELP modes
/// (6.60 / 8.85 / 12.65 / 14.25 / 15.85 / 18.25 / 19.85 / 23.05 / 23.85 kbit/s) plus the SID and
/// NO_DATA frame types. Each 20 ms frame decodes to exactly 320 samples of 16-bit PCM at 16 kHz
/// mono.
/// <para>
/// The decoder is a faithful float port of ffmpeg <c>libavcodec/amrwbdec.c</c>: ISF dequantisation → ISP →
/// 16th-order LPC, four subframes of fractional-pitch adaptive codebook + the 5-track algebraic
/// codebook, gain VQ, then the full wideband post-processing — high-band synthesis (white-noise
/// excitation via the seeded lagged-Fibonacci PRNG, ISF extrapolation for 6k60), de-emphasis, the
/// 31/400 Hz high-pass pair and the 5/4 upsampling chain. The high band is fully synthesised here;
/// only DTX/SID comfort noise is left as silence.
/// </para>
/// </summary>
public static partial class AmrWbCodec {

  /// <summary>Samples produced per frame (320 @ 16 kHz = 20 ms).</summary>
  public const int SamplesPerFrame = AmrWbData.SamplesPerFrame;

  /// <summary>The AMR-WB sample rate.</summary>
  public const int SampleRate = 16000;

  /// <summary>Per-frame walk result.</summary>
  public readonly record struct FrameInfo(int Index, AmrWbMode Mode, int SizeBytes);

  /// <summary>Maps a 4-bit frame type to its mode (reserved/lost/no-data → NoData).</summary>
  public static AmrWbMode ModeFromFrameType(int frameType) => frameType switch {
    >= 0 and <= 8 => (AmrWbMode)frameType,
    9 => AmrWbMode.Sid,
    14 => AmrWbMode.SpeechLost,
    _ => AmrWbMode.NoData,
  };

  /// <summary>Total frame byte size (header + payload) for a 4-bit frame type.</summary>
  public static int FrameBytes(int frameType) => AmrWbData.FrameBytes(frameType);

  /// <summary>
  /// Walks an AMR-WB IF1/MIME storage byte stream (magic already stripped). Each frame begins with
  /// a header byte whose bits 3..6 are the frame type. A trailing truncated frame is ignored.
  /// </summary>
  public static IReadOnlyList<FrameInfo> ReadInfo(ReadOnlySpan<byte> stream) {
    var list = new List<FrameInfo>();
    var pos = 0;
    var index = 0;
    while (pos < stream.Length) {
      var frameType = (stream[pos] >> 3) & 0x0F;
      var size = FrameBytes(frameType);
      if (size == 0) {
        // NO_DATA / reserved: 1-byte frame (header only).
        size = 1;
      }
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
  /// Decodes an AMR-WB storage stream (magic already stripped) to 16-bit PCM at 16 kHz mono.
  /// Output length is <c>frameCount × 320</c> samples.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> stream) {
    var infos = ReadInfo(stream);
    if (infos.Count == 0)
      return [];

    var output = new short[infos.Count * SamplesPerFrame];
    var decoder = new AmrWbDecoder();
    var pos = 0;
    for (var f = 0; f < infos.Count; f++) {
      var info = infos[f];
      var payload = info.SizeBytes > 1 ? stream.Slice(pos + 1, info.SizeBytes - 1) : ReadOnlySpan<byte>.Empty;
      decoder.DecodeFrame(payload, info.Mode, output.AsSpan(f * SamplesPerFrame, SamplesPerFrame));
      pos += info.SizeBytes;
    }
    return output;
  }
}
