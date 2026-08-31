using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Packet-preserving MPEG audio adapter. Frame parsing follows ISO/IEC 11172-3 and ISO/IEC 13818-3;
/// ID3 metadata is intentionally outside the encoded packet stream.
/// </summary>
internal sealed class Mp3AudioPacketAdapter : IAudioDemuxSource, IAudioMuxTarget {
  internal static readonly Mp3AudioPacketAdapter Instance = new();

  private static readonly string[] MuxCodecs = ["mp3", "mp2"];
  private static readonly int[] Mpeg1Layer1Bitrates = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
  private static readonly int[] Mpeg1Layer2Bitrates = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
  private static readonly int[] Mpeg1Layer3Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
  private static readonly int[] Mpeg2Layer1Bitrates = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
  private static readonly int[] Mpeg2Layer23Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
  private static readonly int[] Mpeg1SampleRates = [44_100, 48_000, 32_000];

  public IReadOnlyList<string> SupportedMuxCodecs => MuxCodecs;

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    var bytes = Materialize(input);
    stream = null;
    if (bytes.Length < 4) return false;

    var offset = SkipId3v2(bytes);
    if (offset == bytes.Length)
      throw new InvalidDataException("MPEG audio stream contains metadata but no audio frames.");
    if (offset + 4 > bytes.Length || !TryParseHeader(bytes.AsSpan(offset), out var first))
      return false;
    EnsureSupportedLayer(first.Layer);

    var packets = new List<AudioPacket>();
    while (offset < bytes.Length) {
      if (IsId3v1(bytes, offset)) {
        offset += 128;
        break;
      }
      if (offset + 4 > bytes.Length)
        throw new InvalidDataException($"Truncated MPEG audio frame header at byte offset {offset}.");
      if (!TryParseHeader(bytes.AsSpan(offset), out var header))
        throw new InvalidDataException($"Invalid MPEG audio frame header at byte offset {offset}.");
      EnsureSupportedLayer(header.Layer);
      if (header.Version != first.Version || header.Layer != first.Layer ||
          header.SampleRate != first.SampleRate || header.Channels != first.Channels)
        throw new InvalidDataException("MPEG audio stream changes version, layer, sample rate, or channel count between frames.");
      if (offset + header.FrameSize > bytes.Length)
        throw new InvalidDataException($"Truncated MPEG audio frame at byte offset {offset}: expected {header.FrameSize} bytes.");

      packets.Add(new AudioPacket(bytes.AsSpan(offset, header.FrameSize).ToArray(), header.SamplesPerFrame));
      offset += header.FrameSize;
    }

    if (offset != bytes.Length)
      throw new InvalidDataException($"Unexpected trailing data after MPEG audio frames at byte offset {offset}.");
    if (packets.Count == 0)
      throw new InvalidDataException("MPEG audio stream contains no complete frames.");

    var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      ["mpeg-version"] = VersionName(first.Version),
      ["mpeg-layer"] = first.Layer.ToString(System.Globalization.CultureInfo.InvariantCulture),
      ["channel-mode"] = ChannelModeName(first.ChannelMode),
      ["samples-per-frame"] = first.SamplesPerFrame.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
    stream = new AudioEncodedStream(
      new AudioStreamFormat(first.Layer == 3 ? "mp3" : "mp2", first.SampleRate, first.Channels, Properties: properties),
      packets);
    return true;
  }

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!MuxCodecs.Contains(stream.CodecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"raw MPEG audio accepts Layer II/III packets, not codec '{stream.CodecId}'";
      return false;
    }
    if (stream.SampleRate <= 0 || stream.Channels is < 1 or > 2) {
      reason = "raw MPEG audio requires a positive sample rate and mono/stereo packets";
      return false;
    }
    reason = null;
    return true;
  }

  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanMux(stream.Format, options, out var reason))
      throw new NotSupportedException(reason);
    if (stream.Packets.Count == 0)
      throw new ArgumentException("MPEG audio muxing requires at least one frame.", nameof(stream));

    var expectedLayer = stream.Format.CodecId.Equals("mp3", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
    foreach (var packet in stream.Packets) {
      if (packet.IsHeader)
        throw new InvalidDataException("Raw MPEG audio does not use out-of-band header packets.");
      if (packet.Data.Length < 4 || !TryParseHeader(packet.Data, out var header))
        throw new InvalidDataException("MPEG audio packet does not begin with a valid frame header.");
      EnsureSupportedLayer(header.Layer);
      if (header.Layer != expectedLayer)
        throw new InvalidDataException($"MPEG audio packet is Layer {header.Layer}, but stream codec is '{stream.Format.CodecId}'.");
      if (header.FrameSize != packet.Data.Length)
        throw new InvalidDataException($"MPEG audio packet length {packet.Data.Length} does not match header frame size {header.FrameSize}.");
      if (header.SampleRate != stream.Format.SampleRate || header.Channels != stream.Format.Channels)
        throw new InvalidDataException("MPEG audio packet geometry does not match the advertised stream format.");
      if (packet.DurationSamples > 0 && packet.DurationSamples != header.SamplesPerFrame)
        throw new InvalidDataException("MPEG audio packet duration does not match its frame header.");
      output.Write(packet.Data);
    }
  }

  private static int SkipId3v2(ReadOnlySpan<byte> bytes) {
    if (bytes.Length < 10 || !bytes[..3].SequenceEqual("ID3"u8)) return 0;
    if ((bytes[6] | bytes[7] | bytes[8] | bytes[9]) >= 0x80)
      throw new InvalidDataException("ID3v2 tag uses an invalid synchsafe size.");
    var payloadSize = bytes[6] << 21 | bytes[7] << 14 | bytes[8] << 7 | bytes[9];
    var footerSize = (bytes[5] & 0x10) != 0 ? 10 : 0;
    var totalSize = checked(10 + payloadSize + footerSize);
    if (totalSize > bytes.Length)
      throw new InvalidDataException("Truncated ID3v2 tag precedes MPEG audio frames.");
    return totalSize;
  }

  private static bool IsId3v1(ReadOnlySpan<byte> bytes, int offset)
    => bytes.Length - offset == 128 && bytes.Slice(offset, 3).SequenceEqual("TAG"u8);

  private static bool TryParseHeader(ReadOnlySpan<byte> bytes, out MpegFrameHeader header) {
    header = default;
    if (bytes.Length < 4) return false;
    var word = BinaryPrimitives.ReadUInt32BigEndian(bytes);
    if ((word & 0xFFE0_0000u) != 0xFFE0_0000u) return false;

    var versionBits = (int)((word >> 19) & 0x3);
    var layerBits = (int)((word >> 17) & 0x3);
    var bitrateIndex = (int)((word >> 12) & 0xF);
    var rateIndex = (int)((word >> 10) & 0x3);
    var padding = (int)((word >> 9) & 0x1);
    var channelMode = (int)((word >> 6) & 0x3);
    if (versionBits == 1 || layerBits == 0 || bitrateIndex == 15 || rateIndex == 3) return false;
    if (bitrateIndex == 0)
      throw new NotSupportedException("Free-format MPEG audio frames are not packetized because their frame size is not self-describing.");

    var version = versionBits switch { 3 => 1, 2 => 2, 0 => 25, _ => 0 };
    var layer = 4 - layerBits;
    var bitrateKbps = GetBitrateKbps(version, layer, bitrateIndex);
    var sampleRate = Mpeg1SampleRates[rateIndex] / (version == 1 ? 1 : version == 2 ? 2 : 4);
    var samplesPerFrame = layer switch {
      1 => 384,
      2 => 1152,
      3 when version == 1 => 1152,
      3 => 576,
      _ => 0,
    };
    var frameSize = layer switch {
      1 => (12 * bitrateKbps * 1000 / sampleRate + padding) * 4,
      2 => 144 * bitrateKbps * 1000 / sampleRate + padding,
      3 when version == 1 => 144 * bitrateKbps * 1000 / sampleRate + padding,
      3 => 72 * bitrateKbps * 1000 / sampleRate + padding,
      _ => 0,
    };
    if (frameSize < 4) return false;
    header = new MpegFrameHeader(version, layer, sampleRate, channelMode == 3 ? 1 : 2, channelMode, samplesPerFrame, frameSize);
    return true;
  }

  private static int GetBitrateKbps(int version, int layer, int index)
    => (version, layer) switch {
      (1, 1) => Mpeg1Layer1Bitrates[index],
      (1, 2) => Mpeg1Layer2Bitrates[index],
      (1, 3) => Mpeg1Layer3Bitrates[index],
      (_, 1) => Mpeg2Layer1Bitrates[index],
      _ => Mpeg2Layer23Bitrates[index],
    };

  private static void EnsureSupportedLayer(int layer) {
    if (layer is not (2 or 3))
      throw new NotSupportedException($"MPEG Layer {layer} packet routing is not exposed by the MP3/MP2 descriptor.");
  }

  private static string VersionName(int version)
    => version == 25 ? "2.5" : version.ToString(System.Globalization.CultureInfo.InvariantCulture);

  private static string ChannelModeName(int channelMode) => channelMode switch {
    0 => "stereo",
    1 => "joint-stereo",
    2 => "dual-channel",
    3 => "mono",
    _ => "unknown",
  };

  private static byte[] Materialize(Stream input) {
    using var copy = new MemoryStream();
    input.CopyTo(copy);
    return copy.ToArray();
  }

  private readonly record struct MpegFrameHeader(
    int Version,
    int Layer,
    int SampleRate,
    int Channels,
    int ChannelMode,
    int SamplesPerFrame,
    int FrameSize);
}
