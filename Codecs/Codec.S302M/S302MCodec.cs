#pragma warning disable CS1591
namespace Codec.S302M;

/// <summary>
/// SMPTE 302M decoder — linear PCM carried as AES3 (AES/EBU) subframes inside an MPEG-2 transport
/// stream PES payload. A faithful port of FFmpeg's <c>libavcodec/s302m.c</c>.
/// <para>
/// Each S302M packet starts with a 4-byte AES3 header (big-endian): a 16-bit audio packet size,
/// a 2-bit channel-count field (<c>n × 2 + 2</c> channels, so 2/4/6/8), an 8-bit channel id,
/// a 2-bit bits-per-sample field (<c>n × 4 + 16</c>, so 16/20/24) and 4 alignment bits. The
/// payload is a packed stream of AES3 subframe words whose audio bits are stored in
/// <b>bit-reversed</b> order (LSB-first within each byte), two samples at a time. Decoding
/// reverses each byte (<see cref="Reverse"/>, FFmpeg's <c>ff_reverse</c>) and re-assembles the
/// left-justified PCM words exactly as the reference does for the 16-, 20- and 24-bit cases.
/// </para>
/// <para>
/// S302M has no encoder in FFmpeg; <see cref="Encode"/> here is the exact inverse of the decoder's
/// bit packing (used to build round-trip test vectors), not a general-purpose muxer.
/// </para>
/// </summary>
public static class S302MCodec {

  /// <summary>Length of the AES3 header that precedes the packed PCM payload.</summary>
  public const int Aes3HeaderLength = 4;

  /// <summary>The fixed S302M sample rate (48 kHz).</summary>
  public const int SampleRate = 48000;

  /// <summary>Parsed AES3 header fields.</summary>
  public readonly record struct Aes3Header(int FrameSizeBytes, int Channels, int BitsPerSample);

  /// <summary>FFmpeg's <c>ff_reverse</c>: the bit-reversal of a byte (LSB↔MSB).</summary>
  internal static readonly byte[] Reverse = BuildReverse();

  private static byte[] BuildReverse() {
    var table = new byte[256];
    for (var i = 0; i < 256; ++i) {
      var v = 0;
      for (var b = 0; b < 8; ++b)
        v |= ((i >> b) & 1) << (7 - b);
      table[i] = (byte)v;
    }
    return table;
  }

  /// <summary>
  /// Parses the 4-byte AES3 header at the start of <paramref name="data"/>. Returns <c>null</c>
  /// when the buffer is too short, the declared frame size does not match the payload length, or
  /// the bits-per-sample exceed 24 — matching the reference's validation.
  /// </summary>
  public static Aes3Header? ReadHeader(ReadOnlySpan<byte> data) {
    if (data.Length <= Aes3HeaderLength)
      return null;

    var h = ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
    var frameSize = (int)((h >> 16) & 0xFFFF);
    var channels = (int)((h >> 14) & 0x0003) * 2 + 2;
    var bits = (int)((h >> 4) & 0x0003) * 4 + 16;

    if (Aes3HeaderLength + frameSize != data.Length || bits > 24)
      return null;

    return new Aes3Header(frameSize, channels, bits);
  }

  /// <summary>
  /// Decodes an S302M packet to per-channel signed PCM samples, normalised to a signed 32-bit
  /// value left-justified to <paramref name="bitsPerSample"/> (i.e. the raw left-justified word the
  /// reference produces, interpreted as a two's-complement sample). The interleaving is the AES3
  /// channel-pair order. Returns an empty array on an invalid header.
  /// </summary>
  public static int[][] DecodeToChannels(ReadOnlySpan<byte> data, out int sampleRate,
                                         out int channels, out int bitsPerSample) {
    sampleRate = SampleRate;
    channels = 0;
    bitsPerSample = 0;

    var header = ReadHeader(data);
    if (header is not { } h)
      return [];

    channels = h.Channels;
    bitsPerSample = h.BitsPerSample;

    var interleaved = DecodeInterleaved(data);
    var frames = interleaved.Length / channels;

    var result = new int[channels][];
    for (var ch = 0; ch < channels; ++ch) {
      result[ch] = new int[frames];
      for (var f = 0; f < frames; ++f)
        result[ch][f] = interleaved[f * channels + ch];
    }
    return result;
  }

  /// <summary>
  /// Decodes an S302M packet to interleaved signed PCM samples (left-justified into a signed 32-bit
  /// container at the stream's bit depth, AES3 channel order). The reference packs two samples per
  /// iteration; the final fractional group beyond the per-depth stride is left undecoded exactly as
  /// in FFmpeg (the loop condition stops with a few payload bytes remaining).
  /// </summary>
  public static int[] DecodeInterleaved(ReadOnlySpan<byte> data) {
    var header = ReadHeader(data);
    if (header is not { } h)
      return [];

    var channels = h.Channels;
    var payload = data[Aes3HeaderLength..];
    var blockSize = (h.BitsPerSample + 4) / 4;       // bytes per sample-pair element / 2
    var sampleCount = 2 * (payload.Length / blockSize) / channels * channels;
    var usable = sampleCount / 2 * blockSize;        // bytes the reference actually consumes

    var samples = new int[sampleCount];
    var oi = 0;
    var buf = 0;

    switch (h.BitsPerSample) {
      case 24:
        for (var bufSize = usable; bufSize > 6; bufSize -= 7) {
          samples[oi++] = ToInt24((int)(((uint)Reverse[payload[buf + 2]] << 24) |
                                         ((uint)Reverse[payload[buf + 1]] << 16) |
                                         ((uint)Reverse[payload[buf + 0]] << 8)));
          samples[oi++] = ToInt24((int)(((uint)Reverse[payload[buf + 6] & 0xF0] << 28) |
                                         ((uint)Reverse[payload[buf + 5]] << 20) |
                                         ((uint)Reverse[payload[buf + 4]] << 12) |
                                         ((uint)Reverse[payload[buf + 3] & 0x0F] << 4)));
          buf += 7;
        }
        break;

      case 20:
        for (var bufSize = usable; bufSize > 5; bufSize -= 6) {
          samples[oi++] = ToInt20((int)(((uint)Reverse[payload[buf + 2] & 0xF0] << 28) |
                                         ((uint)Reverse[payload[buf + 1]] << 20) |
                                         ((uint)Reverse[payload[buf + 0]] << 12)));
          samples[oi++] = ToInt20((int)(((uint)Reverse[payload[buf + 5] & 0xF0] << 28) |
                                         ((uint)Reverse[payload[buf + 4]] << 20) |
                                         ((uint)Reverse[payload[buf + 3]] << 12)));
          buf += 6;
        }
        break;

      default: // 16-bit
        for (var bufSize = usable; bufSize > 4; bufSize -= 5) {
          samples[oi++] = ToInt16((Reverse[payload[buf + 1]] << 8) | Reverse[payload[buf + 0]]);
          samples[oi++] = ToInt16((Reverse[payload[buf + 4] & 0xF0] << 12) |
                                  (Reverse[payload[buf + 3]] << 4) |
                                  (Reverse[payload[buf + 2]] >> 4));
          buf += 5;
        }
        break;
    }

    return oi == samples.Length ? samples : samples[..oi];
  }

  // Sign-extend the left-justified words the reference emits into native two's-complement ints.
  private static int ToInt16(int word) => (short)(word & 0xFFFF);
  private static int ToInt20(int word) => (word << 0) >> 12; // word is in the high 20 bits
  private static int ToInt24(int word) => word >> 8;          // word is in the high 24 bits

  /// <summary>
  /// Builds an S302M packet from interleaved signed PCM samples — the exact inverse of
  /// <see cref="DecodeInterleaved"/>. <paramref name="channels"/> must be 2/4/6/8 and
  /// <paramref name="bitsPerSample"/> 16/20/24; the sample count must be a multiple of
  /// <c>channels × 2</c> (the reference packs sample pairs). Used to construct byte-exact
  /// round-trip test vectors.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<int> interleaved, int channels, int bitsPerSample) {
    if (channels is not (2 or 4 or 6 or 8))
      throw new ArgumentException("S302M supports 2, 4, 6 or 8 channels.", nameof(channels));
    if (bitsPerSample is not (16 or 20 or 24))
      throw new ArgumentException("S302M supports 16, 20 or 24 bits per sample.", nameof(bitsPerSample));
    if (interleaved.Length % 2 != 0)
      throw new ArgumentException("Sample count must be even (AES3 packs sample pairs).", nameof(interleaved));
    if (interleaved.Length % channels != 0)
      throw new ArgumentException("Sample count must be a multiple of the channel count.", nameof(interleaved));

    var blockSize = (bitsPerSample + 4) / 4;
    var pairs = interleaved.Length / 2;
    var payloadLength = pairs * blockSize;

    var packet = new byte[Aes3HeaderLength + payloadLength];
    var channelsField = (channels - 2) / 2;
    var bitsField = (bitsPerSample - 16) / 4;
    var header = ((uint)payloadLength << 16) | ((uint)channelsField << 14) | ((uint)bitsField << 4);
    packet[0] = (byte)(header >> 24);
    packet[1] = (byte)(header >> 16);
    packet[2] = (byte)(header >> 8);
    packet[3] = (byte)header;

    // For each payload byte the decoder forms a sample by OR-ing Reverse[payload[k] (& mask)]
    // shifted into place. Reverse is an involution, so to make the decoder reconstruct a chosen
    // byte value v we set payload[k] = Reverse[v]; a low/high-nibble mask in the decoder means only
    // that nibble of v is meaningful, and the discarded nibble is set to zero here.
    var payload = packet.AsSpan(Aes3HeaderLength);
    var buf = 0;
    for (var p = 0; p < pairs; ++p) {
      var a = (uint)interleaved[p * 2 + 0];
      var b = (uint)interleaved[p * 2 + 1];
      switch (bitsPerSample) {
        case 24: {
          var wa = a & 0xFFFFFF; // sample a occupies bits [23:0]; decoder left-justifies by <<8
          var wb = b & 0xFFFFFF;
          payload[buf + 2] = Reverse[(byte)(wa >> 16)];
          payload[buf + 1] = Reverse[(byte)(wa >> 8)];
          payload[buf + 0] = Reverse[(byte)wa];
          payload[buf + 6] = Reverse[(byte)((wb >> 20) & 0x0F)];      // high nibble of wb (& 0xf0)
          payload[buf + 5] = Reverse[(byte)(wb >> 12)];
          payload[buf + 4] = Reverse[(byte)(wb >> 4)];
          payload[buf + 3] = Reverse[(byte)((wb & 0x0F) << 4)];        // low nibble of wb (& 0x0f)
          buf += 7;
          break;
        }
        case 20: {
          var wa = a & 0xFFFFF; // sample occupies bits [19:0]; decoder left-justifies by <<12
          var wb = b & 0xFFFFF;
          payload[buf + 2] = Reverse[(byte)((wa >> 16) & 0x0F)];       // top nibble (& 0xf0)
          payload[buf + 1] = Reverse[(byte)(wa >> 8)];
          payload[buf + 0] = Reverse[(byte)wa];
          payload[buf + 5] = Reverse[(byte)((wb >> 16) & 0x0F)];
          payload[buf + 4] = Reverse[(byte)(wb >> 8)];
          payload[buf + 3] = Reverse[(byte)wb];
          buf += 6;
          break;
        }
        default: {
          var wa = a & 0xFFFF;
          var wb = b & 0xFFFF;
          payload[buf + 1] = Reverse[(byte)(wa >> 8)];
          payload[buf + 0] = Reverse[(byte)wa];
          payload[buf + 4] = Reverse[(byte)((wb >> 12) & 0x0F)];       // bits [15:12] via & 0xf0
          payload[buf + 3] = Reverse[(byte)(wb >> 4)];                 // bits [11:4]
          payload[buf + 2] = Reverse[(byte)((wb & 0x0F) << 4)];        // bits [3:0] via >> 4
          buf += 5;
          break;
        }
      }
    }

    return packet;
  }
}
