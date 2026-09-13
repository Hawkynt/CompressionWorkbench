#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.AdpcmX;

/// <summary>
/// The Electronic Arts "revision" ADPCM family (ffmpeg <c>adpcm_ea_r1</c> / <c>adpcm_ea_r2</c> /
/// <c>adpcm_ea_r3</c>). All three decode the same per-channel chunk math; they differ only in
/// container framing:
/// <list type="bullet">
///   <item>R1 — channel data laid out back-to-back, each channel chunk prefixed by its two
///         16-bit little-endian start samples (current, previous).</item>
///   <item>R2 — like R1 but the start samples persist in decoder state across chunks (the
///         chunk has no in-band seed; the caller supplies it).</item>
///   <item>R3 — big-endian framing (the offsets and raw escape samples are big-endian).</item>
/// </list>
/// The per-frame body is a 1-byte header: high nibble = coefficient index into the EA-XA
/// coefficient set (<see cref="CoefK0"/>/<see cref="CoefK1"/>), low nibble selects the shift
/// as <c>shift = 20 - (header &amp; 0x0F)</c>. Each subsequent nibble <c>n</c> yields
/// <c>sample = clamp16(((n &lt;&lt; shift) + hist1*K0 + hist2*K1) &gt;&gt; 8)</c>. A header byte of
/// <c>0xEE</c> instead introduces a run of 28 raw 16-bit samples.
/// <para>
/// The coefficient tables are the canonical EA-XA pairs (the same four entries used by
/// <c>Codec.EaXa</c>); they are duplicated here so this codec stays dependency-free, with the
/// extended R1/R2/R3 indices that ffmpeg's <c>ea_adpcm_table</c> exposes beyond the first four.
/// </para>
/// </summary>
public static class EaR {

  /// <summary>EA-XA predictor coefficient 0 (ffmpeg <c>ea_adpcm_table[0..3]</c>; matches Codec.EaXa).</summary>
  internal static readonly int[] CoefK0 = [0, 240, 460, 392];

  /// <summary>EA-XA predictor coefficient 1 (ffmpeg <c>ea_adpcm_table[4..7]</c>; matches Codec.EaXa).</summary>
  internal static readonly int[] CoefK1 = [0, 0, -208, -220];

  /// <summary>Samples emitted by one compressed EA-R frame.</summary>
  public const int SamplesPerFrame = 28;

  /// <summary>Header byte flagging a run of 28 raw 16-bit samples.</summary>
  public const byte RawFrameMarker = 0xEE;

  /// <summary>EA revision selector.</summary>
  public enum Revision { R1, R2, R3 }

  /// <summary>
  /// Decodes a single channel's R1/R2/R3 chunk into PCM16. For R1 the two start samples are
  /// read from the front of <paramref name="channelData"/>; for R2/R3 they are taken from
  /// <paramref name="seedHist1"/>/<paramref name="seedHist2"/>. <paramref name="sampleCount"/>
  /// caps output (the final partial frame is truncated).
  /// </summary>
  public static short[] DecodeChannel(ReadOnlySpan<byte> channelData, Revision revision, int sampleCount,
                                      int seedHist1 = 0, int seedHist2 = 0) {
    if (sampleCount < 0)
      throw new ArgumentOutOfRangeException(nameof(sampleCount));

    var bigEndian = revision == Revision.R3;
    var pos = 0;
    int hist1, hist2;
    if (revision == Revision.R1) {
      hist1 = ReadSample(channelData, 0, bigEndian);
      hist2 = ReadSample(channelData, 2, bigEndian);
      pos = 4;
    } else {
      hist1 = seedHist1;
      hist2 = seedHist2;
    }

    var output = new short[sampleCount];
    var produced = 0;

    while (produced < sampleCount && pos < channelData.Length) {
      var header = channelData[pos++];
      if (header == RawFrameMarker) {
        // The raw escape carries 16-bit big-endian samples in every revision.
        for (var i = 0; i < SamplesPerFrame && produced < sampleCount && pos + 1 < channelData.Length; ++i) {
          var s = ReadSample(channelData, pos, bigEndian: true);
          pos += 2;
          output[produced++] = (short)s;
          hist2 = hist1;
          hist1 = s;
        }
        continue;
      }

      var coef = (header >> 4) & 0x0F;
      var shift = 20 - (header & 0x0F);
      if (coef > 3) coef = 3; // R1/R2/R3 use the four EA-XA pairs
      var k0 = CoefK0[coef];
      var k1 = CoefK1[coef];

      for (var i = 0; i < SamplesPerFrame && produced < sampleCount && pos < channelData.Length; i += 2) {
        var b = channelData[pos++];
        var hi = b >> 4;
        var lo = b & 0x0F;
        var s = ImaCore.Clamp16(((ImaCore.SignExtend4(hi) << shift) + hist1 * k0 + hist2 * k1) >> 8);
        output[produced++] = (short)s;
        hist2 = hist1;
        hist1 = s;
        if (produced >= sampleCount) break;
        s = ImaCore.Clamp16(((ImaCore.SignExtend4(lo) << shift) + hist1 * k0 + hist2 * k1) >> 8);
        output[produced++] = (short)s;
        hist2 = hist1;
        hist1 = s;
      }
    }

    return output;
  }

  private static int ReadSample(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    => bigEndian
      ? BinaryPrimitives.ReadInt16BigEndian(data[offset..])
      : BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
}
