#pragma warning disable CS1591

namespace Codec.Speex;

/// <summary>
/// Speex decoder facade. Input is an Ogg Speex stream (the "Speex   " identification
/// header packet, a Vorbis-comment packet, then audio packets each carrying
/// <see cref="SpeexHeader.FramesPerPacket"/> frames). Output is interleaved
/// little-endian signed 16-bit PCM at the header's declared sample rate.
/// <para>
/// <b>Ported from:</b> FFmpeg <c>libavcodec/speexdec.c</c> + <c>speexdata.h</c>
/// (Xiph.org / Jean-Marc Valin et al., BSD-3) — the self-contained native Speex
/// decoder. Narrowband (mode 0), wideband (mode 1) and ultra-wideband (mode 2) layers
/// plus in-band intensity stereo are implemented; see <see cref="SpeexDecoder"/>.
/// </para>
/// </summary>
public static class SpeexCodec {

  /// <summary>
  /// Decompresses an Ogg Speex stream from <paramref name="input"/> into interleaved
  /// little-endian signed 16-bit PCM on <paramref name="output"/>.
  /// </summary>
  /// <exception cref="InvalidDataException">Input is not a valid Ogg Speex stream.</exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var reader = new OggSpeexReader(input);
    var header = reader.ReadHeader();
    _ = reader.TryReadComments(); // consume the comment packet so it isn't decoded as audio

    var decoder = new SpeexDecoder(header);
    var buf = new byte[2];

    while (reader.TryReadPacket(out var packet)) {
      if (packet.Length == 0)
        continue;
      short[] pcm;
      try {
        pcm = decoder.DecodePacket(packet);
      } catch (InvalidDataException) {
        break; // bitstream corrupt — stop cleanly
      }
      foreach (var s in pcm) {
        buf[0] = (byte)s;
        buf[1] = (byte)(s >> 8);
        output.Write(buf, 0, 2);
      }
    }
  }

  /// <summary>
  /// Reads the Ogg Speex identification header without decoding audio.
  /// </summary>
  public static SpeexStreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var reader = new OggSpeexReader(input);
    var header = reader.ReadHeader();
    return new SpeexStreamInfo(
      SampleRate: header.Rate,
      Channels: header.NbChannels,
      Mode: header.Mode,
      FrameSize: 160 << header.Mode,
      FramesPerPacket: header.FramesPerPacket);
  }

  /// <summary>
  /// Decodes a sequence of Speex audio packets (header already parsed) into one
  /// contiguous interleaved 16-bit PCM array. Helper for callers that have already
  /// demuxed the Ogg container.
  /// </summary>
  public static short[] DecodeStream(SpeexHeader header, IEnumerable<byte[]> audioPackets) {
    ArgumentNullException.ThrowIfNull(header);
    ArgumentNullException.ThrowIfNull(audioPackets);
    var decoder = new SpeexDecoder(header);
    var all = new List<short>();
    foreach (var p in audioPackets) {
      if (p.Length == 0) continue;
      all.AddRange(decoder.DecodePacket(p));
    }
    return all.ToArray();
  }
}

/// <summary>Speex stream identification info extracted from the header packet.</summary>
public sealed record SpeexStreamInfo(int SampleRate, int Channels, int Mode, int FrameSize, int FramesPerPacket);
