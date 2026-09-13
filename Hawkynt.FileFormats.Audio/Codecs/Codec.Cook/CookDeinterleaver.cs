#pragma warning disable CS1591
namespace Codec.Cook;

/// <summary>
/// Reorders carried RealAudio audio packets into the codec's coded-frame byte order, a port
/// of the audio descrambling in FFmpeg's <c>libavformat/rmdec.c</c>. RealMedia stores cook
/// (and sipr/atrac) subpackets interleaved across a group of <c>sub_packet_h</c> container
/// packets; the decoder must see them re-stitched. Three interleavers are supported here:
/// <list type="bullet">
///   <item><c>Int0</c> — no reorder; each packet is already a coded frame.</item>
///   <item><c>Int4</c> — <c>dst[x*2*w + y*cfs] = src[x*cfs]</c> for x in [0, h/2).</item>
///   <item><c>genr</c> — <c>dst[sps*(h*x + ((h+1)/2)*(y&amp;1) + (y&gt;&gt;1))] = src[x*sps]</c>
///     for x in [0, w/sps).</item>
/// </list>
/// where w = audio_framesize, h = sub_packet_h, sps = sub_packet_size, cfs = coded_framesize,
/// and y is the packet index within the group. Any other id or inconsistent framing yields an
/// empty result so the caller can fall back to a blob-only view.
/// </summary>
public static class CookDeinterleaver {

  /// <summary>'Int0' deinterleaver id (little-endian FOURCC).</summary>
  public const uint Int0 = 0x30746E49;

  /// <summary>'Int4' deinterleaver id (little-endian FOURCC).</summary>
  public const uint Int4 = 0x34746E49;

  /// <summary>'genr' deinterleaver id (little-endian FOURCC).</summary>
  public const uint Genr = 0x726E6567;

  /// <summary>
  /// Reorders <paramref name="packets"/> per the given interleaver and framing. Packets are
  /// processed in groups of <paramref name="subPacketH"/>; a trailing partial group is
  /// dropped. Returns the concatenated coded frames, or an empty array on an unsupported
  /// interleaver / inconsistent framing.
  /// </summary>
  public static byte[] Reorder(IReadOnlyList<byte[]> packets, uint deintId,
      int subPacketH, int audioFrameSize, int subPacketSize, int codedFrameSize) {
    var h = subPacketH;
    var w = audioFrameSize;
    var sps = subPacketSize;
    var cfs = codedFrameSize;

    if (deintId == Int0 || deintId == 0 || h <= 1 || w <= 0) {
      using var flat = new MemoryStream();
      foreach (var pk in packets) flat.Write(pk);
      return flat.ToArray();
    }

    if (deintId != Int4 && deintId != Genr)
      return [];
    if (deintId == Int4 && (cfs <= 0 || (long)cfs * h != 2L * w))
      return [];
    if (deintId == Genr && (sps <= 0 || sps > w || w % sps != 0))
      return [];
    if ((long)w * h > int.MaxValue)
      return [];

    using var outStream = new MemoryStream();
    for (var groupStart = 0; groupStart + h <= packets.Count; groupStart += h) {
      var buffer = new byte[w * h];
      for (var y = 0; y < h; ++y) {
        var src = packets[groupStart + y];
        if (deintId == Int4) {
          for (var x = 0; x < h / 2; ++x)
            CopySegment(src, x * cfs, buffer, x * 2 * w + y * cfs, cfs);
        } else {
          for (var x = 0; x < w / sps; ++x)
            CopySegment(src, x * sps, buffer, sps * (h * x + ((h + 1) / 2) * (y & 1) + (y >> 1)), sps);
        }
      }
      outStream.Write(buffer);
    }
    return outStream.ToArray();
  }

  private static void CopySegment(byte[] src, int srcOff, byte[] dst, int dstOff, int len) {
    if (srcOff < 0 || dstOff < 0 || len <= 0) return;
    var n = Math.Min(len, Math.Min(src.Length - srcOff, dst.Length - dstOff));
    if (n > 0)
      Array.Copy(src, srcOff, dst, dstOff, n);
  }
}
