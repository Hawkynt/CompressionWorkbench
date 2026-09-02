#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.AmrNb;
using Compression.Registry;

namespace FileFormat.AmrNb;

/// <summary>3GPP AMR-NB single-channel storage format (<c>#!AMR\n</c> + IF1 storage frames).</summary>
public sealed class AmrNbFormatDescriptor : IFormatDescriptor, IAudioContainerFormat,
  IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource, IAudioMuxTarget {

  private static readonly byte[] FileMagic = "#!AMR\n"u8.ToArray();
  private static readonly string[] Codecs = ["amr-nb", "amrnb"];

  public string Id => "AmrNb";
  public string DisplayName => "AMR-NB";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities => FormatCapabilities.None;
  public string DefaultExtension => ".amr";
  public IReadOnlyList<string> Extensions => [".amr"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new(FileMagic, Confidence: 0.99)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("amr-nb", "AMR-NB")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "3GPP AMR-NB storage file; #!AMR magic followed by IF1 storage frames.";

  public IReadOnlyList<string> SupportedEncodeCodecs => Codecs;
  public IReadOnlyList<string> SupportedMuxCodecs => Codecs;

  public AudioPcmBuffer DecodePcm(Stream input) {
    var storage = ReadStorage(input);
    ValidateStorage(storage);
    var samples = AmrNbCodec.Decode(storage);
    return new AudioPcmBuffer(
      new AudioPcmFormat(AmrNbCodec.SampleRate, 1, 16, AudioPcmEncoding.SignedInteger),
      ToLittleEndian(samples));
  }

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    if (!IsCodec(codecId)) {
      reason = $"AMR-NB does not support codec '{codecId}'.";
      return false;
    }
    if (format.SampleRate != AmrNbCodec.SampleRate || format.Channels != 1 ||
        format.BitsPerSample != 16 || format.Encoding != AudioPcmEncoding.SignedInteger) {
      reason = "AMR-NB encoding requires mono signed PCM16 at 8000 Hz.";
      return false;
    }
    reason = null;
    return true;
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(pcm);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);

    var encoderOptions = new AmrNbEncoderOptions(
      ParseMode(options.GetOption("mode", "12.2")),
      options.GetOptionBool("dtx", false),
      options.GetOptionBool("pad-final-frame", true));
    var encoded = AmrNbCodec.Encode(ReadPcm16(pcm.InterleavedData), encoderOptions);
    output.Write(FileMagic);
    output.Write(encoded);
  }

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    stream = null;
    try {
      var storage = ReadStorage(input);
      var packets = ParsePackets(storage);
      stream = new AudioEncodedStream(
        new AudioStreamFormat("amr-nb", AmrNbCodec.SampleRate, 1, 16),
        packets);
      return true;
    } catch (InvalidDataException) {
      return false;
    }
  }

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    if (!IsCodec(stream.CodecId)) {
      reason = $"AMR-NB cannot mux codec '{stream.CodecId}'.";
      return false;
    }
    if (stream.SampleRate != AmrNbCodec.SampleRate || stream.Channels != 1) {
      reason = "AMR-NB storage requires mono 8000 Hz access units.";
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

    output.Write(FileMagic);
    foreach (var packet in stream.Packets) {
      if (packet.IsHeader)
        throw new InvalidDataException("AMR-NB file magic is container metadata, not an encoded packet.");
      ValidateSingleFrame(packet.Data);
      output.Write(packet.Data);
    }
  }

  private static byte[] ReadStorage(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    if (input.CanSeek) input.Position = 0;
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    var file = memory.ToArray();
    if (file.Length < FileMagic.Length || !file.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic))
      throw new InvalidDataException("Missing AMR-NB '#!AMR\\n' file magic.");
    return file[FileMagic.Length..];
  }

  private static IReadOnlyList<AudioPacket> ParsePackets(ReadOnlySpan<byte> storage) {
    var packets = new List<AudioPacket>();
    var pos = 0;
    while (pos < storage.Length) {
      var size = FrameSize(storage[pos]);
      if (pos + size > storage.Length)
        throw new InvalidDataException("Truncated AMR-NB storage frame.");
      packets.Add(new AudioPacket(storage.Slice(pos, size).ToArray(), AmrNbCodec.SamplesPerFrame));
      pos += size;
    }
    return packets;
  }

  private static void ValidateStorage(ReadOnlySpan<byte> storage) => _ = ParsePackets(storage);

  private static void ValidateSingleFrame(ReadOnlySpan<byte> frame) {
    if (frame.IsEmpty)
      throw new InvalidDataException("Empty AMR-NB packet.");
    var size = FrameSize(frame[0]);
    if (frame.Length != size)
      throw new InvalidDataException($"AMR-NB packet has {frame.Length} bytes; frame type requires {size}.");
  }

  private static int FrameSize(byte header) {
    if ((header & 0x83) != 0)
      throw new InvalidDataException("AMR-NB storage frame has non-zero padding bits.");
    var frameType = (header >> 3) & 0x0F;
    if (frameType is > 8 and < 15)
      throw new InvalidDataException($"Reserved AMR-NB frame type {frameType} is not valid in an AMR-NB storage file.");
    return 1 + AmrNbCodec.PayloadBytes(frameType);
  }

  private static bool IsCodec(string codecId)
    => Codecs.Contains(codecId, StringComparer.OrdinalIgnoreCase);

  private static AmrNbMode ParseMode(string text) => text.Trim().ToLowerInvariant() switch {
    "4.75" or "475" or "mr475" => AmrNbMode.Mr475,
    "5.15" or "515" or "mr515" => AmrNbMode.Mr515,
    "5.90" or "5.9" or "590" or "59" or "mr59" => AmrNbMode.Mr59,
    "6.70" or "6.7" or "670" or "67" or "mr67" => AmrNbMode.Mr67,
    "7.40" or "7.4" or "740" or "74" or "mr74" => AmrNbMode.Mr74,
    "7.95" or "795" or "mr795" => AmrNbMode.Mr795,
    "10.2" or "102" or "mr102" => AmrNbMode.Mr102,
    "12.2" or "122" or "mr122" => AmrNbMode.Mr122,
    _ => throw new ArgumentException($"Unknown AMR-NB mode '{text}'. Expected 4.75, 5.15, 5.90, 6.70, 7.40, 7.95, 10.2, or 12.2 kbit/s."),
  };

  private static short[] ReadPcm16(ReadOnlySpan<byte> data) {
    if ((data.Length & 1) != 0)
      throw new InvalidDataException("PCM16 payload has odd length.");
    var samples = new short[data.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
    return samples;
  }

  private static byte[] ToLittleEndian(ReadOnlySpan<short> samples) {
    var bytes = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
    return bytes;
  }
}
