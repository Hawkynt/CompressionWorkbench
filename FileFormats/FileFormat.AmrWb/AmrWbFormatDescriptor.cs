#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.AmrWb;
using Compression.Registry;

namespace FileFormat.AmrWb;

/// <summary>3GPP AMR-WB single-channel storage format (<c>#!AMR-WB\n</c> + storage frames).</summary>
public sealed class AmrWbFormatDescriptor : IFormatDescriptor, IAudioContainerFormat,
  IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource, IAudioMuxTarget {

  private static readonly byte[] FileMagic = "#!AMR-WB\n"u8.ToArray();
  private static readonly string[] Codecs = ["amr-wb", "amrwb"];

  public string Id => "AmrWb";
  public string DisplayName => "AMR-WB";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities => FormatCapabilities.None;
  public string DefaultExtension => ".amr";
  public IReadOnlyList<string> Extensions => [".amr"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new(FileMagic, Confidence: 0.99)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("amr-wb", "AMR-WB")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "3GPP AMR-WB storage file; #!AMR-WB magic followed by storage frames.";

  public IReadOnlyList<string> SupportedEncodeCodecs => Codecs;
  public IReadOnlyList<string> SupportedMuxCodecs => Codecs;

  public AudioPcmBuffer DecodePcm(Stream input) {
    var storage = ReadStorage(input);
    ValidateStorage(storage);
    var samples = AmrWbCodec.Decode(storage);
    return new AudioPcmBuffer(
      new AudioPcmFormat(AmrWbCodec.SampleRate, 1, 16, AudioPcmEncoding.SignedInteger),
      ToLittleEndian(samples));
  }

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    if (!IsCodec(codecId)) {
      reason = $"AMR-WB does not support codec '{codecId}'.";
      return false;
    }
    if (format.SampleRate != AmrWbCodec.SampleRate || format.Channels != 1 ||
        format.BitsPerSample != 16 || format.Encoding != AudioPcmEncoding.SignedInteger) {
      reason = "AMR-WB encoding requires mono signed PCM16 at 16000 Hz.";
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

    var encoderOptions = new AmrWbEncoderOptions(
      ParseMode(options.GetOption("mode", "12.65")),
      options.GetOptionBool("dtx", false),
      options.GetOptionBool("pad-final-frame", true));
    var encoded = AmrWbCodec.Encode(ReadPcm16(pcm.InterleavedData), encoderOptions);
    output.Write(FileMagic);
    output.Write(encoded);
  }

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    stream = null;
    try {
      var storage = ReadStorage(input);
      var packets = ParsePackets(storage);
      stream = new AudioEncodedStream(
        new AudioStreamFormat("amr-wb", AmrWbCodec.SampleRate, 1, 16),
        packets);
      return true;
    } catch (InvalidDataException) {
      return false;
    }
  }

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    if (!IsCodec(stream.CodecId)) {
      reason = $"AMR-WB cannot mux codec '{stream.CodecId}'.";
      return false;
    }
    if (stream.SampleRate != AmrWbCodec.SampleRate || stream.Channels != 1) {
      reason = "AMR-WB storage requires mono 16000 Hz access units.";
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
        throw new InvalidDataException("AMR-WB file magic is container metadata, not an encoded packet.");
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
      throw new InvalidDataException("Missing AMR-WB '#!AMR-WB\\n' file magic.");
    return file[FileMagic.Length..];
  }

  private static IReadOnlyList<AudioPacket> ParsePackets(ReadOnlySpan<byte> storage) {
    var packets = new List<AudioPacket>();
    var pos = 0;
    while (pos < storage.Length) {
      var size = FrameSize(storage[pos]);
      if (pos + size > storage.Length)
        throw new InvalidDataException("Truncated AMR-WB storage frame.");
      packets.Add(new AudioPacket(storage.Slice(pos, size).ToArray(), AmrWbCodec.SamplesPerFrame));
      pos += size;
    }
    return packets;
  }

  private static void ValidateStorage(ReadOnlySpan<byte> storage) => _ = ParsePackets(storage);

  private static void ValidateSingleFrame(ReadOnlySpan<byte> frame) {
    if (frame.IsEmpty)
      throw new InvalidDataException("Empty AMR-WB packet.");
    var size = FrameSize(frame[0]);
    if (frame.Length != size)
      throw new InvalidDataException($"AMR-WB packet has {frame.Length} bytes; frame type requires {size}.");
  }

  private static int FrameSize(byte header) {
    if ((header & 0x83) != 0)
      throw new InvalidDataException("AMR-WB storage frame has non-zero padding bits.");
    var frameType = (header >> 3) & 0x0F;
    if (frameType is >= 10 and <= 13)
      throw new InvalidDataException($"Reserved AMR-WB frame type {frameType} is not valid in an AMR-WB storage file.");
    return Math.Max(1, AmrWbCodec.FrameBytes(frameType));
  }

  private static bool IsCodec(string codecId)
    => Codecs.Contains(codecId, StringComparer.OrdinalIgnoreCase);

  private static AmrWbMode ParseMode(string text) => text.Trim().ToLowerInvariant() switch {
    "6.60" or "6.6" or "660" or "mr660" => AmrWbMode.Mr660,
    "8.85" or "885" or "mr885" => AmrWbMode.Mr885,
    "12.65" or "1265" or "mr1265" => AmrWbMode.Mr1265,
    "14.25" or "1425" or "mr1425" => AmrWbMode.Mr1425,
    "15.85" or "1585" or "mr1585" => AmrWbMode.Mr1585,
    "18.25" or "1825" or "mr1825" => AmrWbMode.Mr1825,
    "19.85" or "1985" or "mr1985" => AmrWbMode.Mr1985,
    "23.05" or "2305" or "mr2305" => AmrWbMode.Mr2305,
    "23.85" or "2385" or "mr2385" => AmrWbMode.Mr2385,
    _ => throw new ArgumentException($"Unknown AMR-WB mode '{text}'. Expected 6.60, 8.85, 12.65, 14.25, 15.85, 18.25, 19.85, 23.05, or 23.85 kbit/s."),
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
