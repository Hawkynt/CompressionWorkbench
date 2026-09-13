#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.AdpcmX;

/// <summary>
/// The two Electronic Arts IMA nibble variants from ffmpeg <c>libavcodec/adpcm.c</c>.
/// Both walk plain IMA channels but differ in their headers and in which nibble of a byte
/// is consumed first:
/// <list type="bullet">
///   <item><see cref="DecodeEacs"/> — <c>adpcm_ima_eacs</c>: a per-channel header of a
///         little-endian 32-bit step index followed (for all channels) by a little-endian
///         32-bit start predictor, then nibble bytes with the HIGH nibble feeding channel 0
///         and the LOW nibble the last channel. Shift 3.</item>
///   <item><see cref="DecodeSead"/> — <c>adpcm_ima_sead</c>: no header (the caller supplies
///         the start predictors/indices), same HIGH-then-LOW nibble order, but shift 6.</item>
/// </list>
/// </summary>
public static class ImaEa {

  /// <summary>
  /// Decodes an EACS block: per-channel 32-bit LE step indices, then per-channel 32-bit LE
  /// predictors, then the nibble payload (HIGH nibble → ch0, LOW nibble → last channel).
  /// </summary>
  public static short[] DecodeEacs(ReadOnlySpan<byte> data, int channels) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("EACS supports 1 or 2 channels.", nameof(channels));
    var headerSize = 8 * channels; // 4-byte step index + 4-byte predictor, each per channel
    if (data.Length < headerSize)
      throw new ArgumentException("EACS data shorter than its header.", nameof(data));

    Span<int> predictor = stackalloc int[channels];
    Span<int> index = stackalloc int[channels];

    for (var c = 0; c < channels; ++c) {
      index[c] = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(c * 4)..]);
      if (index[c] > 88) index[c] = 88;
      else if (index[c] < 0) index[c] = 0;
    }
    var predBase = 4 * channels;
    for (var c = 0; c < channels; ++c)
      predictor[c] = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(predBase + c * 4)..]);

    return DecodeNibbles(data[headerSize..], predictor, index, shift: 3);
  }

  /// <summary>
  /// Decodes a SEAD nibble payload. <paramref name="startPredictors"/>/<paramref name="startIndices"/>
  /// seed the channels (SEAD carries no in-band header). HIGH nibble → ch0, LOW → last channel,
  /// shift 6.
  /// </summary>
  public static short[] DecodeSead(ReadOnlySpan<byte> data, int channels,
                                   ReadOnlySpan<int> startPredictors, ReadOnlySpan<int> startIndices) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("SEAD supports 1 or 2 channels.", nameof(channels));
    if (startPredictors.Length < channels || startIndices.Length < channels)
      throw new ArgumentException("SEAD needs a start predictor and index per channel.");

    Span<int> predictor = stackalloc int[channels];
    Span<int> index = stackalloc int[channels];
    for (var c = 0; c < channels; ++c) {
      predictor[c] = startPredictors[c];
      index[c] = Math.Clamp(startIndices[c], 0, 88);
    }

    return DecodeNibbles(data, predictor, index, shift: 6);
  }

  // HIGH nibble → channel 0, LOW nibble → last channel; one byte per emitted frame.
  private static short[] DecodeNibbles(ReadOnlySpan<byte> data, Span<int> predictor, Span<int> index, int shift) {
    var channels = predictor.Length;
    var last = channels - 1;
    var output = new short[data.Length * 2];
    var produced = 0;
    foreach (var b in data) {
      output[produced++] = ImaCore.Expand(b >> 4, ref predictor[0], ref index[0], shift);
      output[produced++] = ImaCore.Expand(b & 0x0F, ref predictor[last], ref index[last], shift);
    }
    return output;
  }
}
