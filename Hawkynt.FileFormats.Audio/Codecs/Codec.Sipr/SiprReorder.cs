#pragma warning disable CS1591
namespace Codec.Sipr;

/// <summary>
/// In-place RealMedia SIPR superblock descrambler — a port of
/// <c>ff_rm_reorder_sipr_data</c> (FFmpeg's <c>libavformat/rmsipr.c</c>). RealMedia carries
/// SIPR with the <c>DEINT_ID_SIPR</c> interleaver: a whole superblock of
/// <c>sub_packet_h * frame_size</c> bytes has 38 pairs of equal-length nibble runs swapped
/// (the <c>sipr_swaps</c> table). The run length is
/// <c>bs = sub_packet_h * frame_size * 2 / 96</c> nibbles; each swap exchanges nibble run
/// <c>bs * sipr_swaps[n][0]</c> with run <c>bs * sipr_swaps[n][1]</c>. After descrambling the
/// superblock is a back-to-back sequence of coded frames for the decoder.
/// </summary>
public static class SiprReorder {

  /// <summary>'sipr' deinterleaver id (little-endian FOURCC), for descriptor wiring.</summary>
  public const uint Sipr = 0x72706973;

  /// <summary>
  /// Returns a descrambled copy of one <paramref name="superblock"/> of
  /// <c>subPacketH * frameSize</c> bytes. The input is not modified. If the framing is
  /// degenerate (non-positive sizes, <c>bs == 0</c>, or a size that does not cover the swap
  /// targets) the input is returned unchanged so callers can fall back gracefully.
  /// </summary>
  public static byte[] Reorder(ReadOnlySpan<byte> superblock, int subPacketH, int frameSize) {
    var buf = superblock.ToArray();
    ReorderInPlace(buf, subPacketH, frameSize);
    return buf;
  }

  /// <summary>In-place variant of <see cref="Reorder(ReadOnlySpan{byte},int,int)"/>.</summary>
  public static void ReorderInPlace(byte[] buf, int subPacketH, int frameSize) {
    if (subPacketH <= 0 || frameSize <= 0)
      return;
    var bs = subPacketH * frameSize * 2 / 96; // nibbles per swap run
    if (bs <= 0)
      return;

    foreach (var swap in SiprTables.SiprSwaps) {
      var i = bs * swap[0];
      var o = bs * swap[1];
      for (var j = 0; j < bs; ++j, ++i, ++o) {
        if ((i >> 1) >= buf.Length || (o >> 1) >= buf.Length)
          break; // truncated superblock — stop this run
        var x = (buf[i >> 1] >> (4 * (i & 1))) & 0xF;
        var y = (buf[o >> 1] >> (4 * (o & 1))) & 0xF;

        buf[o >> 1] = (byte)((x << (4 * (o & 1))) | (buf[o >> 1] & (0xF << (4 * (1 - (o & 1))))));
        buf[i >> 1] = (byte)((y << (4 * (i & 1))) | (buf[i >> 1] & (0xF << (4 * (1 - (i & 1))))));
      }
    }
  }
}
