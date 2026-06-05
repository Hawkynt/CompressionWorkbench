#pragma warning disable CS1591

namespace Codec.Musepack;

/// <summary>
/// Parser + frame walker for the Musepack SV7 (<c>MP+</c>) container, ported from
/// FFmpeg's <c>libavformat/mpc.c</c> (<c>mpc_read_header</c> / <c>mpc_read_packet</c>,
/// LGPL 2.1, © Konstantin Shishkov). The header is the 3-byte <c>MP+</c> magic, a
/// version byte (<c>0x07</c>), a little-endian 32-bit frame count, then 16 bytes of
/// extradata. The audio that follows is a single bit-packed stream of variable-length
/// frames whose 20-bit size prefixes are themselves bit-aligned; this walker reproduces
/// the demuxer's exact <c>curbits</c> accounting to split the stream into per-frame
/// byte ranges plus the per-frame <c>skip</c> (leading bits to discard).
/// </summary>
internal static class Mpc7Container {

  public static readonly byte[] Magic = "MP+"u8.ToArray();
  public const byte Sv7Version = 0x07;

  /// <summary>SV7 stream-header fields decoded from the magic + extradata.</summary>
  public sealed record StreamHeader(
    int Version, uint FrameCount, int SampleRate, int MaxBands,
    bool IntensityStereo, bool MidSideUsed, bool Gapless, int LastFrameLen);

  /// <summary>One SV7 frame: its byte range in the file and the leading bits to skip.</summary>
  public readonly record struct Frame(int Offset, int Size, int Skip, bool LastFrame);

  /// <summary>Reads the SV7 header. <paramref name="audioStart"/> is the byte offset of the first frame.</summary>
  public static StreamHeader ReadHeader(byte[] data, out int audioStart) {
    if (data.Length < 3 + 1 + 4 + 16)
      throw new InvalidDataException("Musepack SV7: stream too short for header.");
    if (!data.AsSpan(0, 3).SequenceEqual(Magic))
      throw new InvalidDataException("Musepack SV7: missing MP+ magic.");

    var version = data[3];
    if (version != Sv7Version)
      throw new NotSupportedException($"Musepack: stream version {version} is not SV7.");

    var frameCount = (uint)(data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24));

    // 16 bytes of extradata follow the frame count.
    var extra = data.AsSpan(8, 16).ToArray();
    audioStart = 24;

    var sampleRate = Mpc7Tables.SampleRates[extra[2] & 3];

    // The decoder (mpc7_decode_init) byte-swaps the first 4 32-bit words and reads bits.
    var swapped = ByteSwapWords(extra, 16);
    var gb = new MpcBitReader(swapped, 0, 16);
    var intensity = gb.GetBit() != 0;
    var mss = gb.GetBit() != 0;
    var maxbands = gb.GetBits(6);
    if (maxbands >= MpcTables.Bands)
      throw new InvalidDataException($"Musepack SV7: too many bands ({maxbands}).");
    gb.SkipBits(88);
    var gapless = gb.GetBit() != 0;
    var lastFrameLen = gb.GetBits(11);

    return new StreamHeader(version, frameCount, sampleRate, maxbands,
      intensity, mss, gapless, lastFrameLen);
  }

  /// <summary>
  /// Walks the bit-packed frame stream beginning at <paramref name="audioStart"/>,
  /// yielding one <see cref="Frame"/> per coded frame. Stops at the declared frame count
  /// (or at the buffer end / on inconsistency for truncated input).
  /// </summary>
  public static IEnumerable<Frame> EnumerateFrames(byte[] data, int audioStart, uint frameCount) {
    var pos = audioStart;
    var curbits = 8; // mpc_read_header sets curbits = 8 before the first packet.
    uint frame = 0;

    while ((frame < frameCount || frameCount == 0) && pos < data.Length) {
      var tmp = ReadLe32(data, pos);
      int size2;
      if (curbits <= 12) {
        size2 = (int)((tmp >> (12 - curbits)) & 0xFFFFF);
      } else {
        var tmp2 = ReadLe32(data, pos + 4);
        size2 = (int)(((tmp << (curbits - 12)) | (tmp2 >> (44 - curbits))) & 0xFFFFF);
      }
      var skip = curbits + 20;          // == pkt->data[0]; bits to skip in the decoder
      var size = ((size2 + skip + 31) & ~31) >> 3; // bytes of this frame's buffer
      if (size <= 0 || pos + size > data.Length + 4)
        yield break;

      var newCurbits = (skip + size2) & 0x1F;
      var lastFlag = frameCount != 0 && frame + 1 >= frameCount;

      var available = Math.Min(size, data.Length - pos);
      if (available <= 0)
        yield break;
      yield return new Frame(pos, available, skip, lastFlag);

      // Advance: read `size` bytes from pos; if curbits!=0 seek back 4.
      pos += size - (newCurbits != 0 ? 4 : 0);
      curbits = newCurbits;
      ++frame;
    }
  }

  /// <summary>Byte-swaps each aligned 32-bit word (FFmpeg's <c>bswap_buf</c>).</summary>
  public static byte[] ByteSwapWords(ReadOnlySpan<byte> src, int length) {
    var n = length & ~3;
    var result = new byte[length];
    for (var i = 0; i < n; i += 4) {
      result[i + 0] = src[i + 3];
      result[i + 1] = src[i + 2];
      result[i + 2] = src[i + 1];
      result[i + 3] = src[i + 0];
    }
    for (var i = n; i < length; ++i)
      result[i] = src[i];
    return result;
  }

  private static uint ReadLe32(byte[] data, int pos) {
    uint v = 0;
    for (var i = 0; i < 4; ++i)
      if (pos + i < data.Length)
        v |= (uint)data[pos + i] << (8 * i);
    return v;
  }
}
