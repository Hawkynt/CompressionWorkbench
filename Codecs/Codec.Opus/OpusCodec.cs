#pragma warning disable CS1591
#pragma warning disable CS0618

using System.Buffers.Binary;
using Concentus;
using Concentus.Structs;

namespace Codec.Opus;

/// <summary>
/// RFC 6716 / RFC 7845 Opus codec. Ogg framing and metadata are handled locally; SILK,
/// hybrid, CELT and multistream signal coding are handled by pure-managed Concentus.
/// </summary>
public static partial class OpusCodec {

  /// <summary>
  /// Decodes Ogg Opus mapping family 0 (mono/stereo) and family 1 (Vorbis-order surround)
  /// to interleaved little-endian PCM16 at 48 kHz. Pre-skip and output gain are applied.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var reader = new OggOpusReader(input);
    var head = reader.ReadHead();
    _ = reader.TryReadTags();

    if (head.ChannelMappingFamily == 0) {
      using IOpusDecoder decoder = new OpusDecoder(48000, head.ChannelCount);
      decoder.Gain = head.OutputGainQ8;
      DecodeFamily0(reader, output, decoder, head.ChannelCount, head.PreSkip);
      return;
    }

    if (head.ChannelMappingFamily == 1) {
      using IOpusMultiStreamDecoder decoder = new OpusMSDecoder(
        48000,
        head.ChannelCount,
        head.StreamCount,
        head.CoupledStreamCount,
        head.ChannelMapping);
      decoder.Gain = head.OutputGainQ8;
      DecodeFamily1(reader, output, decoder, head.ChannelCount, head.PreSkip);
      return;
    }

    throw new NotSupportedException($"Opus channel mapping family {head.ChannelMappingFamily} is not supported by this stream surface.");
  }

  private static void DecodeFamily0(OggOpusReader reader, Stream output, IOpusDecoder decoder,
    int channels, int preSkip) {
    var pcm = new short[5760 * channels];
    var bytes = new byte[pcm.Length * 2];
    while (reader.TryReadPacket(out var packet)) {
      if (packet.Length == 0) continue;
      var decodedFrames = decoder.Decode(packet, pcm, 5760, decode_fec: false);
      if (decodedFrames < 0)
        throw new InvalidDataException($"Opus decoder returned invalid frame count {decodedFrames}.");
      WriteDecodedPcm(output, pcm, channels, decodedFrames, ref preSkip, bytes);
    }
  }

  private static void DecodeFamily1(OggOpusReader reader, Stream output, IOpusMultiStreamDecoder decoder,
    int channels, int preSkip) {
    var pcm = new short[5760 * channels];
    var bytes = new byte[pcm.Length * 2];
    while (reader.TryReadPacket(out var packet)) {
      if (packet.Length == 0) continue;
      var decodedFrames = decoder.DecodeMultistream(packet, pcm, 5760, decode_fec: false);
      if (decodedFrames < 0)
        throw new InvalidDataException($"Opus multistream decoder returned invalid frame count {decodedFrames}.");
      WriteDecodedPcm(output, pcm, channels, decodedFrames, ref preSkip, bytes);
    }
  }

  private static void WriteDecodedPcm(Stream output, short[] pcm, int channels, int decodedFrames,
    ref int preSkip, byte[] bytes) {
    var skip = Math.Min(preSkip, decodedFrames);
    preSkip -= skip;
    var takeFrames = decodedFrames - skip;
    if (takeFrames <= 0) return;

    var source = skip * channels;
    var sampleCount = takeFrames * channels;
    var byteCount = sampleCount * 2;
    for (var i = 0; i < sampleCount; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), pcm[source + i]);
    output.Write(bytes, 0, byteCount);
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
      Vendor: tags?.Vendor,
      ChannelMappingFamily: head.ChannelMappingFamily,
      StreamCount: head.StreamCount,
      CoupledStreamCount: head.CoupledStreamCount,
      ChannelMapping: head.ChannelMapping);
  }
}

public sealed record OpusStreamInfo(
  int SampleRate,
  int Channels,
  int PreSkip,
  int InputSampleRate,
  string? Vendor,
  int ChannelMappingFamily,
  int StreamCount,
  int CoupledStreamCount,
  IReadOnlyList<byte> ChannelMapping);
