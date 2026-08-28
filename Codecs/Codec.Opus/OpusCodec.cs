#pragma warning disable CS1591
#pragma warning disable CS0618

using System.Buffers.Binary;
using Concentus;
using Concentus.Structs;

namespace Codec.Opus;

/// <summary>
/// RFC 6716 / RFC 7845 Opus codec. Ogg framing and metadata are handled locally; SILK,
/// hybrid and CELT signal coding are decoded by the pure-managed Concentus implementation.
/// Mapping family 0 (mono/stereo) is supported by this stream-level surface.
/// </summary>
public static partial class OpusCodec {

  /// <summary>
  /// Decodes an Ogg Opus mapping-family-0 stream to interleaved little-endian PCM16 at 48 kHz.
  /// Pre-skip and the OpusHead output gain are applied. The final packet may contain codec padding;
  /// callers requiring sample-exact trimming should use the Ogg granule position from their
  /// container layer.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var reader = new OggOpusReader(input);
    var head = reader.ReadHead();
    _ = reader.TryReadTags();
    if (head.ChannelMappingFamily != 0 || head.ChannelCount is < 1 or > 2)
      throw new NotSupportedException("This Opus stream decoder currently supports mapping family 0 (mono/stereo). ");

    using IOpusDecoder decoder = new OpusDecoder(48000, head.ChannelCount);
    decoder.Gain = head.OutputGainQ8;
    var preSkip = (int)head.PreSkip;
    var pcm = new short[5760 * head.ChannelCount];
    var bytes = new byte[pcm.Length * 2];

    while (reader.TryReadPacket(out var packet)) {
      if (packet.Length == 0) continue;
      var decodedFrames = decoder.Decode(packet, pcm, 5760, decode_fec: false);
      if (decodedFrames < 0)
        throw new InvalidDataException($"Opus decoder returned invalid frame count {decodedFrames}.");

      var skip = Math.Min(preSkip, decodedFrames);
      preSkip -= skip;
      var takeFrames = decodedFrames - skip;
      if (takeFrames <= 0) continue;

      var source = skip * head.ChannelCount;
      var sampleCount = takeFrames * head.ChannelCount;
      var byteCount = sampleCount * 2;
      for (var i = 0; i < sampleCount; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), pcm[source + i]);
      output.Write(bytes, 0, byteCount);
    }
  }

  /// <summary>Reads OpusHead / OpusTags metadata without decoding audio.</summary>
  public static OpusStreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var reader = new OggOpusReader(input);
    var head = reader.ReadHead();
    var tags = reader.TryReadTags();
    return new OpusStreamInfo(
      SampleRate: 48000,
      Channels: head.ChannelCount,
      PreSkip: head.PreSkip,
      InputSampleRate: (int)head.InputSampleRate,
      Vendor: tags?.Vendor);
  }
}

public sealed record OpusStreamInfo(int SampleRate, int Channels, int PreSkip, int InputSampleRate, string? Vendor);
