#pragma warning disable CS1591

namespace Codec.Xma;

/// <summary>
/// Parsers for the Microsoft XMA1/XMA2 container framing, ported from FFmpeg's
/// <c>libavcodec/wmaprodec.c</c> (the XMA paths of <c>decode_packet</c> and
/// <c>decode_init</c>, LGPL 2.1). XMA carries one or more WMA Pro elementary streams
/// (1 or 2 channels each) split into fixed-size packets — 2048 bytes by convention —
/// whose WMA Pro frames are bit-packed and may span packet boundaries. Each packet
/// opens with a small header; this type exposes the header fields and the XMA1/XMA2
/// extradata layout (number of streams + per-stream channel count) needed to drive a
/// per-stream WMA Pro decode and interleave the streams back into one output.
/// </summary>
public static class XmaPacket {

  /// <summary>Conventional XMA packet size (also the per-stream WMA Pro block_align).</summary>
  public const int PacketSize = 2048;

  /// <summary>The fixed WMA Pro decode flags every XMA stream uses (XMA2WAVEFORMAT/XMA1).</summary>
  public const int DecodeFlags = 0x10d6;

  /// <summary>WMA Pro frame length for XMA streams is always 512 samples.</summary>
  public const int SamplesPerFrame = 512;

  /// <summary>One parsed XMA packet header.</summary>
  /// <param name="NumFrames">XMA2: number of frames opening in this packet (6 bits). XMA1: not present (0).</param>
  /// <param name="NumBitsPrevFrame">Bits belonging to the previous packet's trailing frame.</param>
  /// <param name="SkipPackets">Packets to skip before this stream's next packet (interleave control).</param>
  /// <param name="HeaderBits">Total header size in bits (where the frame payload begins).</param>
  public readonly record struct Header(int NumFrames, int NumBitsPrevFrame, int SkipPackets, int HeaderBits);

  /// <summary>
  /// Parses the leading header of one XMA packet. The 2-byte <c>block_align</c>
  /// determines <c>log2_frame_size = log2(block_align) + 4</c>, the width of the
  /// <c>num_bits_prev_frame</c> field. XMA2 packets open with a 6-bit frame count; XMA1
  /// packets open with a 4-bit sequence number + 2 reserved bits. Both then carry the
  /// previous-frame bit count, 3 reserved bits and an 8-bit packet-skip count.
  /// </summary>
  public static Header ParseHeader(ReadOnlySpan<byte> packet, bool isXma2, int blockAlign) {
    var log2FrameSize = Log2(blockAlign) + 4;
    var br = new BitCursor(packet);

    int numFrames;
    if (isXma2) {
      numFrames = (int)br.GetBits(6);
    } else {
      br.GetBits(4); // packet sequence number
      br.GetBits(2); // reserved
      numFrames = 0;
    }

    var numBitsPrevFrame = (int)br.GetBits(log2FrameSize);
    br.GetBits(3); // reserved
    var skipPackets = (int)br.GetBits(8);

    return new Header(numFrames, numBitsPrevFrame, skipPackets, br.BitPos);
  }

  /// <summary>XMA1/XMA2 stream configuration decoded from the codec extradata.</summary>
  /// <param name="IsXma2">True for XMA2, false for XMA1.</param>
  /// <param name="NumStreams">Number of WMA Pro elementary streams.</param>
  /// <param name="StreamChannels">Channel count (1 or 2) per stream.</param>
  /// <param name="TotalChannels">Sum of all per-stream channel counts.</param>
  public sealed record StreamConfig(bool IsXma2, int NumStreams, int[] StreamChannels, int TotalChannels);

  /// <summary>
  /// Decodes the stream layout from XMA extradata, mirroring FFmpeg's
  /// <c>decode_init</c> XMA branches:
  /// <list type="bullet">
  ///   <item><b>XMA2WAVEFORMATEX</b> (34-byte extradata): channels split 2ch+2ch+…+1/2ch
  ///     across <paramref name="declaredChannels"/>.</item>
  ///   <item><b>XMA2WAVEFORMAT</b>: <c>num_streams</c> at <c>extradata[1]</c> (when
  ///     <c>extradata[0]==3</c>) or <c>extradata[9]</c>; per-stream channels at
  ///     <c>extradata[32 + (extradata[0]==3?0:8) + 4*n]</c>.</item>
  ///   <item><b>XMA1WAVEFORMAT</b>: per-stream channels at <c>extradata[8 + 20*n + 17]</c>.</item>
  /// </list>
  /// </summary>
  public static StreamConfig ParseStreamConfig(ReadOnlySpan<byte> extradata, bool isXma2, int declaredChannels) {
    if (isXma2 && extradata.Length == 34) {
      // XMA2WAVEFORMATEX: 2ch per stream until the remainder, last may be 1ch.
      var numStreams = (declaredChannels + 1) / 2;
      var ch = new int[numStreams];
      for (var n = 0; n < numStreams; ++n)
        ch[n] = (n + 1) * 2 > declaredChannels ? 1 : 2;
      return new StreamConfig(true, numStreams, ch, Sum(ch));
    }

    if (isXma2) {
      // XMA2WAVEFORMAT: num_streams is at offset 1 (legacy v3) or 9; per-stream config block.
      var legacy = extradata.Length > 0 && extradata[0] == 3;
      var numStreams = legacy
        ? (extradata.Length > 1 ? extradata[1] : 0)
        : (extradata.Length > 9 ? extradata[9] : 0);
      var basePos = 32 + (legacy ? 0 : 8);
      var ch = ReadPerStreamChannels(extradata, numStreams, basePos, stride: 4, channelOffset: 0);
      return new StreamConfig(true, numStreams, ch, Sum(ch));
    }

    // XMA1WAVEFORMAT: num_streams at extradata[4]; per-stream channels at +17 with stride 20.
    var n1 = extradata.Length > 4 ? extradata[4] : 0;
    var ch1 = ReadPerStreamChannels(extradata, n1, basePos: 8, stride: 20, channelOffset: 17);
    return new StreamConfig(false, n1, ch1, Sum(ch1));
  }

  private static int[] ReadPerStreamChannels(ReadOnlySpan<byte> extradata, int numStreams, int basePos, int stride, int channelOffset) {
    if (numStreams is < 0 or > 8) numStreams = 0;
    var ch = new int[numStreams];
    for (var n = 0; n < numStreams; ++n) {
      var pos = basePos + stride * n + channelOffset;
      var c = pos < extradata.Length ? extradata[pos] : 0;
      ch[n] = c is 1 or 2 ? c : (c <= 0 ? 1 : 2);
    }
    return ch;
  }

  private static int Sum(int[] a) {
    var s = 0;
    foreach (var v in a) s += v;
    return s;
  }

  private static int Log2(int v) => v <= 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);

  /// <summary>Minimal MSB-first bit cursor for header parsing.</summary>
  private ref struct BitCursor {
    private readonly ReadOnlySpan<byte> _data;
    private int _bit;
    public BitCursor(ReadOnlySpan<byte> data) { this._data = data; this._bit = 0; }
    public int BitPos => this._bit;
    public uint GetBits(int n) {
      uint r = 0;
      for (var i = 0; i < n; ++i) {
        uint b = 0;
        var bytePos = this._bit >> 3;
        if (bytePos < this._data.Length)
          b = (uint)((this._data[bytePos] >> (7 - (this._bit & 7))) & 1);
        r = (r << 1) | b;
        ++this._bit;
      }
      return r;
    }
  }
}
