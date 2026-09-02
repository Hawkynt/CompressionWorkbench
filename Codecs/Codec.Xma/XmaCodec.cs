#pragma warning disable CS1591
using Codec.WmaPro;

namespace Codec.Xma;

/// <summary>
/// Microsoft XMA1/XMA2 decoder orchestrator. XMA is a thin multiplexing layer over WMA
/// Pro: the audio is carried as <see cref="XmaPacket.NumStreams"/>-many WMA Pro
/// elementary streams (1 or 2 channels each), interleaved as fixed-size packets
/// (<see cref="XmaPacket.PacketSize"/> bytes). This class parses the XMA framing
/// (<see cref="XmaPacket"/>), routes each packet to its owning per-stream
/// <see cref="WmaProCodec"/> following the packet-skip interleave, and re-interleaves the
/// decoded streams into one multi-channel signal. A faithful structural port of FFmpeg's
/// <c>xma_decode_packet</c> orchestration (LGPL 2.1).
/// <para>
/// The per-stream WMA Pro frame decode reuses the existing <see cref="WmaProCodec"/>
/// verbatim. Where a stream's packet framing cannot be driven through that codec's ASF
/// packet entry point (e.g. frames that span XMA packet boundaries), the decode degrades
/// gracefully: <see cref="TryDecode"/> returns <c>false</c> and the caller surfaces the
/// raw XMA blob instead of partial PCM.
/// </para>
/// </summary>
public sealed class XmaCodec {

  private readonly XmaPacket.StreamConfig _config;
  private readonly int _sampleRate;

  /// <summary>Decoded XMA stream layout (number of streams + per-stream channel counts).</summary>
  public XmaPacket.StreamConfig Config => this._config;

  /// <summary>Total output channels across all streams.</summary>
  public int Channels => this._config.TotalChannels;

  /// <summary>Output sample rate in Hz.</summary>
  public int SampleRate => this._sampleRate;

  /// <summary>
  /// Initializes a new instance of <see cref="XmaCodec"/>.
  /// </summary>
public XmaCodec(ReadOnlySpan<byte> extradata, bool isXma2, int sampleRate, int declaredChannels) {
    this._config = XmaPacket.ParseStreamConfig(extradata, isXma2, declaredChannels);
    this._sampleRate = sampleRate;
    if (this._config.NumStreams is < 1 or > 8)
      throw new InvalidDataException($"XMA: invalid stream count {this._config.NumStreams}.");
  }

  /// <summary>
  /// Attempts to decode the XMA bitstream <paramref name="data"/> into interleaved
  /// signed-16-bit PCM. Returns <c>true</c> with the PCM in <paramref name="pcm"/> on
  /// success; returns <c>false</c> (PCM empty) when the stream uses framing this
  /// orchestrator cannot drive through <see cref="WmaProCodec"/>, so callers fall back to
  /// the raw blob. State is not retained between calls — each call decodes a whole blob.
  /// </summary>
  public bool TryDecode(ReadOnlySpan<byte> data, out short[] pcm) {
    pcm = [];
    try {
      var numStreams = this._config.NumStreams;
      var totalChannels = this._config.TotalChannels;
      if (data.Length < XmaPacket.PacketSize)
        return false;

      // Build a per-stream WMA Pro decoder (1 or 2 channels each).
      var decoders = new WmaProCodec[numStreams];
      var startChannel = new int[numStreams];
      var acc = 0;
      for (var i = 0; i < numStreams; ++i) {
        var streamChannels = this._config.StreamChannels[i];
        startChannel[i] = acc;
        acc += streamChannels;
        decoders[i] = new WmaProCodec(streamChannels, this._sampleRate, 16,
          XmaPacket.PacketSize, 0, BuildStreamExtradata(streamChannels));
      }

      // Split the blob into fixed-size packets and route each to a stream following the
      // packet-skip interleave (each XMA packet header declares how many packets the OTHER
      // streams must skip before their next packet). At start, packets round-robin streams.
      var packetCount = data.Length / XmaPacket.PacketSize;
      var skipPackets = new int[numStreams];
      var perStreamPcm = new List<short>[numStreams];
      for (var i = 0; i < numStreams; ++i) perStreamPcm[i] = new List<short>();

      var current = 0;
      var anyDecoded = false;
      for (var p = 0; p < packetCount; ++p) {
        var packet = data.Slice(p * XmaPacket.PacketSize, XmaPacket.PacketSize);

        // Select the owning stream (the one with 0 skip_packets).
        if (skipPackets[current] != 0) {
          var best = 0;
          for (var i = 1; i < numStreams; ++i)
            if (skipPackets[i] < skipPackets[best]) best = i;
          current = best;
        }

        var hdr = XmaPacket.ParseHeader(packet, this._config.IsXma2, XmaPacket.PacketSize);

        // Hand the whole 2KB packet to the stream's WMA Pro decoder. (The decoder's ASF
        // packet entry consumes block_align bytes; XMA stores one packet per block.)
        var samples = decoders[current].DecodePacket(packet);
        if (samples.Length > 0) {
          anyDecoded = true;
          perStreamPcm[current].AddRange(samples);
        }

        // Update the interleave bookkeeping.
        skipPackets[current] = hdr.SkipPackets;
        for (var i = 0; i < numStreams; ++i)
          if (i != current)
            skipPackets[i] = Math.Max(0, skipPackets[i] - 1);
      }

      if (!anyDecoded)
        return false;

      // Re-interleave the per-stream PCM (each stream is 1 or 2 channels) into one signal.
      var frames = int.MaxValue;
      for (var i = 0; i < numStreams; ++i)
        frames = Math.Min(frames, perStreamPcm[i].Count / this._config.StreamChannels[i]);
      if (frames <= 0)
        return false;

      var outPcm = new short[frames * totalChannels];
      for (var i = 0; i < numStreams; ++i) {
        var sc = this._config.StreamChannels[i];
        var src = perStreamPcm[i];
        for (var f = 0; f < frames; ++f)
          for (var c = 0; c < sc; ++c)
            outPcm[f * totalChannels + startChannel[i] + c] = src[f * sc + c];
      }
      pcm = outPcm;
      return true;
    } catch {
      pcm = [];
      return false;
    }
  }

  // Synthesizes 18-byte WMA Pro extradata for an XMA elementary stream: bits_per_sample
  // 16, no channel mask (so the codec uses the channel-count argument), decode_flags 0x10d6.
  private static byte[] BuildStreamExtradata(int channels) {
    _ = channels;
    var e = new byte[18];
    e[0] = 16; e[1] = 0;                       // bits_per_sample = 16
    // bytes 2..5 channel mask = 0 → decoder takes nb_channels from the constructor arg
    e[14] = (byte)(XmaPacket.DecodeFlags & 0xFF);        // 0xd6
    e[15] = (byte)((XmaPacket.DecodeFlags >> 8) & 0xFF); // 0x10
    return e;
  }
}
