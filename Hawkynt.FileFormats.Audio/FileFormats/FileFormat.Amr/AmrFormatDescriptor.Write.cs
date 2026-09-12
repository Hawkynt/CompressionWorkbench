#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.AmrNb;
using Codec.AmrWb;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Amr;

/// <summary>Write/encode/demux/mux support for RFC 4867 AMR/AMR-WB storage files.</summary>
public sealed partial class AmrFormatDescriptor :
  IArchiveWriteConstraints, IArchiveCreatable, IAudioContainerFormat,
  IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource, IAudioMuxTarget {

  private static readonly string[] AudioCodecs = ["amr", "amr-nb", "amrnb", "amr-wb", "amrwb"];

  // Number of AMR class A/B/C speech bits carried after the one-octet storage-frame header.
  // Negative entries are reserved frame types. These values are normative framing data from
  // RFC 4867 / 3GPP TS 26.101 and TS 26.201, not implementation-specific tables.
  private static readonly short[] NbSpeechBits = [
    95, 103, 118, 134, 148, 159, 204, 244, 39,
    -1, -1, -1, -1, -1, -1, 0
  ];

  private static readonly short[] WbSpeechBits = [
    132, 177, 253, 285, 317, 365, 397, 461, 477, 40,
    -1, -1, -1, -1, 0, 0
  ];

  public long? MaxTotalArchiveSize => null;

  public string AcceptedInputsDescription =>
    "AMR accepts one byte-exact FULL.amr/FULL.awb or 1-6 mono PCM16 WAV channels at 8000 Hz (AMR-NB) or 16000 Hz (AMR-WB).";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    if (!input.IsDirectory) {
      var name = Path.GetFileName(input.ArchiveName);
      if (name.Equals("FULL.amr", StringComparison.OrdinalIgnoreCase)
          || name.Equals("FULL.awb", StringComparison.OrdinalIgnoreCase)
          || name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
        reason = null;
        return true;
      }
    }

    reason = $"not an AMR creation input (got {input.ArchiveName}); {this.AcceptedInputsDescription}";
    return false;
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var files = inputs.Where(static input => !input.IsDirectory).ToArray();
    var fullFiles = files.Where(static input => {
      var name = Path.GetFileName(input.ArchiveName);
      return name.Equals("FULL.amr", StringComparison.OrdinalIgnoreCase)
             || name.Equals("FULL.awb", StringComparison.OrdinalIgnoreCase);
    }).ToArray();

    if (fullFiles.Length > 0) {
      if (fullFiles.Length != 1 || files.Length != 1)
        throw new InvalidOperationException("AMR byte-exact creation accepts exactly one FULL.amr/FULL.awb and no other inputs.");

      var data = fullFiles[0].ReadContent();
      if (!this.TryDemux(new MemoryStream(data, writable: false), out _))
        throw new InvalidDataException("FULL.amr/FULL.awb is not a valid RFC 4867 AMR storage file.");
      output.Write(data);
      return;
    }

    var wavs = files
      .Where(static input => input.ArchiveName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static input => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(input.ArchiveName)))
      .Select(static input => new WavReader().ReadCanonicalPcm(input.ReadContent()))
      .ToArray();

    if (wavs.Length is < 1 or > 6 || wavs.Length != files.Length)
      throw new InvalidOperationException("AMR creation requires 1-6 mono WAV channel inputs, or one FULL.amr/FULL.awb.");

    var first = wavs[0];
    if (first.NumChannels != 1 || first.FormatCode != 1 || first.BitsPerSample != 16)
      throw new InvalidOperationException("AMR creation requires mono PCM16 WAV channel inputs.");
    if (first.SampleRate is not (AmrNbCodec.SampleRate or AmrWbCodec.SampleRate))
      throw new InvalidOperationException("AMR WAV inputs must use 8000 Hz (AMR-NB) or 16000 Hz (AMR-WB).");
    if (wavs.Any(wav => wav.NumChannels != 1 || wav.FormatCode != 1 || wav.BitsPerSample != 16
                        || wav.SampleRate != first.SampleRate
                        || wav.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All AMR channel WAVs must be mono PCM16 with matching sample rate and frame count.");

    var pcm = new AudioPcmBuffer(
      new AudioPcmFormat(first.SampleRate, wavs.Length, 16, AudioPcmEncoding.SignedInteger),
      PcmCodec.Interleave(wavs.Select(static wav => wav.InterleavedPcm).ToList(), 16));

    var codecId = options.Method ?? options.GetString("codec")
      ?? (first.SampleRate == AmrNbCodec.SampleRate ? "amr-nb" : "amr-wb");
    this.EncodePcm(output, pcm, codecId, options);
  }

  public IReadOnlyList<string> SupportedEncodeCodecs => AudioCodecs;

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);
    ArgumentNullException.ThrowIfNull(codecId);
    ArgumentNullException.ThrowIfNull(options);

    if (!TryResolveVariant(codecId, format.SampleRate, out var isWb, out reason))
      return false;

    var expectedRate = isWb ? AmrWbCodec.SampleRate : AmrNbCodec.SampleRate;
    if (format.SampleRate != expectedRate) {
      reason = $"{CanonicalCodecId(isWb)} requires {expectedRate} Hz PCM.";
      return false;
    }
    if (format.Channels is < 1 or > 6) {
      reason = "RFC 4867 AMR storage supports 1-6 channels.";
      return false;
    }
    if (format.BitsPerSample != 16 || format.Encoding != AudioPcmEncoding.SignedInteger) {
      reason = "AMR encoding requires signed PCM16 input.";
      return false;
    }

    try {
      if (isWb)
        _ = ParseWbMode(options.GetOption("mode", "12.65"));
      else
        _ = ParseNbMode(options.GetOption("mode", "12.2"));
    } catch (ArgumentException ex) {
      reason = ex.Message;
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
    if (pcm.InterleavedData.Length % pcm.Format.BytesPerFrame != 0)
      throw new InvalidDataException("PCM payload does not contain an integral number of interleaved sample frames.");

    var isWb = ResolveVariant(codecId, pcm.Format.SampleRate);
    var samples = ReadPcm16(pcm.InterleavedData);
    var channels = Deinterleave(samples, pcm.Format.Channels);
    var dtx = options.GetOptionBool("dtx", false);
    var pad = options.GetOptionBool("pad-final-frame", true);

    var encodedChannels = new byte[channels.Length][];
    if (isWb) {
      var encoderOptions = new AmrWbEncoderOptions(ParseWbMode(options.GetOption("mode", "12.65")), dtx, pad);
      for (var channel = 0; channel < channels.Length; ++channel)
        encodedChannels[channel] = AmrWbCodec.Encode(channels[channel], encoderOptions);
    } else {
      var encoderOptions = new AmrNbEncoderOptions(ParseNbMode(options.GetOption("mode", "12.2")), dtx, pad);
      for (var channel = 0; channel < channels.Length; ++channel)
        encodedChannels[channel] = AmrNbCodec.Encode(channels[channel], encoderOptions);
    }

    var frames = encodedChannels.Select(data => ParseFrameSpans(data, isWb)).ToArray();
    var frameBlocks = frames.Length == 0 ? 0 : frames[0].Count;
    if (frames.Any(channelFrames => channelFrames.Count != frameBlocks))
      throw new InvalidDataException("Per-channel AMR encoders produced inconsistent frame counts.");

    WriteHeader(output, isWb, pcm.Format.Channels);
    for (var block = 0; block < frameBlocks; ++block)
      for (var channel = 0; channel < frames.Length; ++channel)
        WriteCanonicalFrame(output, frames[channel][block], isWb);
  }

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var parsed = ReadFile(input);
    var channelStreams = SplitChannelsStrict(parsed.Body, parsed.Channels, parsed.IsWb);
    var decoded = new short[parsed.Channels][];
    for (var channel = 0; channel < decoded.Length; ++channel)
      decoded[channel] = parsed.IsWb
        ? AmrWbCodec.Decode(channelStreams[channel])
        : AmrNbCodec.Decode(channelStreams[channel]);

    var sampleCount = decoded.Length == 0 ? 0 : decoded[0].Length;
    if (decoded.Any(samples => samples.Length != sampleCount))
      throw new InvalidDataException("AMR channels decode to different sample counts.");

    var interleaved = new byte[checked(sampleCount * parsed.Channels * sizeof(short))];
    var destination = interleaved.AsSpan();
    var position = 0;
    for (var sample = 0; sample < sampleCount; ++sample)
      for (var channel = 0; channel < parsed.Channels; ++channel) {
        BinaryPrimitives.WriteInt16LittleEndian(destination[position..], decoded[channel][sample]);
        position += sizeof(short);
      }

    return new AudioPcmBuffer(
      new AudioPcmFormat(parsed.IsWb ? AmrWbCodec.SampleRate : AmrNbCodec.SampleRate,
        parsed.Channels, 16, AudioPcmEncoding.SignedInteger),
      interleaved);
  }

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    stream = null;
    try {
      var parsed = ReadFile(input);
      var packets = ParseFrameBlocks(parsed.Body, parsed.Channels, parsed.IsWb);
      stream = new AudioEncodedStream(
        new AudioStreamFormat(
          CanonicalCodecId(parsed.IsWb),
          parsed.IsWb ? AmrWbCodec.SampleRate : AmrNbCodec.SampleRate,
          parsed.Channels,
          Properties: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["storage"] = "rfc4867",
            ["header"] = parsed.Channels == 1 ? "single-channel" : "MC1.0",
          }),
        packets);
      return true;
    } catch (InvalidDataException) {
      return false;
    }
  }

  public IReadOnlyList<string> SupportedMuxCodecs => AudioCodecs;

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);

    if (!TryResolveVariant(stream.CodecId, stream.SampleRate, out var isWb, out reason))
      return false;
    var expectedRate = isWb ? AmrWbCodec.SampleRate : AmrNbCodec.SampleRate;
    if (stream.SampleRate != expectedRate) {
      reason = $"{CanonicalCodecId(isWb)} storage requires {expectedRate} Hz access units.";
      return false;
    }
    if (stream.Channels is < 1 or > 6) {
      reason = "RFC 4867 AMR storage supports 1-6 channels.";
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

    var isWb = ResolveVariant(stream.Format.CodecId, stream.Format.SampleRate);
    WriteHeader(output, isWb, stream.Format.Channels);
    foreach (var packet in stream.Packets) {
      if (packet.IsHeader)
        throw new InvalidDataException("AMR file magic is container metadata, not an encoded packet.");
      WriteFrameBlock(output, packet.Data, stream.Format.Channels, isWb);
    }
  }

  private static (bool IsWb, int Channels, byte[] Body) ReadFile(Stream input) {
    if (input.CanSeek)
      input.Position = 0;
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    var file = memory.ToArray();
    var (variant, headerLength, channels) = ParseHeader(file);
    if (variant == Variant.Unknown)
      throw new InvalidDataException("Missing RFC 4867 AMR/AMR-WB storage-file magic.");

    var isWb = variant is Variant.Wb or Variant.WbMc;
    var isMc = variant is Variant.NbMc or Variant.WbMc;
    if (isMc) {
      if (file.Length < headerLength)
        throw new InvalidDataException("Truncated AMR MC1.0 channel description.");
      if (channels is < 1 or > 6)
        throw new InvalidDataException($"AMR MC1.0 channel count {channels} is outside the RFC 4867 range 1-6.");
    } else {
      channels = 1;
    }

    var body = file.AsSpan(headerLength).ToArray();
    _ = ParseFrameBlocks(body, channels, isWb);
    return (isWb, channels, body);
  }

  private static IReadOnlyList<AudioPacket> ParseFrameBlocks(ReadOnlySpan<byte> body, int channels, bool isWb) {
    var packets = new List<AudioPacket>();
    var position = 0;
    var duration = isWb ? AmrWbCodec.SamplesPerFrame : AmrNbCodec.SamplesPerFrame;
    while (position < body.Length) {
      var blockStart = position;
      for (var channel = 0; channel < channels; ++channel) {
        if (position >= body.Length)
          throw new InvalidDataException("AMR file ends inside a multi-channel frame-block.");
        var size = StorageFrameSize(body[position], isWb);
        if (position + size > body.Length)
          throw new InvalidDataException("Truncated AMR storage frame.");
        position += size;
      }

      packets.Add(new AudioPacket(body[blockStart..position].ToArray(), duration));
    }

    return packets;
  }

  private static byte[][] SplitChannelsStrict(ReadOnlySpan<byte> body, int channels, bool isWb) {
    var streams = Enumerable.Range(0, channels).Select(static _ => new MemoryStream()).ToArray();
    var position = 0;
    try {
      while (position < body.Length) {
        for (var channel = 0; channel < channels; ++channel) {
          if (position >= body.Length)
            throw new InvalidDataException("AMR file ends inside a multi-channel frame-block.");
          var size = StorageFrameSize(body[position], isWb);
          if (position + size > body.Length)
            throw new InvalidDataException("Truncated AMR storage frame.");
          streams[channel].Write(body.Slice(position, size));
          position += size;
        }
      }

      return streams.Select(static stream => stream.ToArray()).ToArray();
    } finally {
      foreach (var stream in streams)
        stream.Dispose();
    }
  }

  private static List<byte[]> ParseFrameSpans(ReadOnlySpan<byte> storage, bool isWb) {
    var result = new List<byte[]>();
    var position = 0;
    while (position < storage.Length) {
      var size = StorageFrameSize(storage[position], isWb);
      if (position + size > storage.Length)
        throw new InvalidDataException("Encoder returned a truncated AMR storage frame.");
      result.Add(storage.Slice(position, size).ToArray());
      position += size;
    }
    return result;
  }

  private static int StorageFrameSize(byte header, bool isWb) {
    var frameType = (header >> 3) & 0x0F;
    var bits = (isWb ? WbSpeechBits : NbSpeechBits)[frameType];
    if (bits < 0)
      throw new InvalidDataException($"Reserved AMR-{(isWb ? "WB" : "NB")} frame type {frameType}.");
    return 1 + ((bits + 7) >> 3);
  }

  private static void WriteFrameBlock(Stream output, ReadOnlySpan<byte> block, int channels, bool isWb) {
    var position = 0;
    for (var channel = 0; channel < channels; ++channel) {
      if (position >= block.Length)
        throw new InvalidDataException("AMR packet contains fewer frames than its channel count.");
      var size = StorageFrameSize(block[position], isWb);
      if (position + size > block.Length)
        throw new InvalidDataException("AMR packet ends inside a storage frame.");
      WriteCanonicalFrame(output, block.Slice(position, size), isWb);
      position += size;
    }
    if (position != block.Length)
      throw new InvalidDataException("AMR packet contains more than one frame-block.");
  }

  private static void WriteCanonicalFrame(Stream output, ReadOnlySpan<byte> frame, bool isWb) {
    if (frame.IsEmpty || frame.Length != StorageFrameSize(frame[0], isWb))
      throw new InvalidDataException("AMR packet is not exactly one storage frame.");

    output.WriteByte((byte)(frame[0] & 0x7C)); // P bits are zero on write; FT and Q are preserved.
    if (frame.Length == 1)
      return;

    var frameType = (frame[0] >> 3) & 0x0F;
    var speechBits = (isWb ? WbSpeechBits : NbSpeechBits)[frameType];
    var trailingPaddingBits = (8 - (speechBits & 7)) & 7;
    if (trailingPaddingBits == 0) {
      output.Write(frame[1..]);
      return;
    }

    if (frame.Length > 2)
      output.Write(frame.Slice(1, frame.Length - 2));
    output.WriteByte((byte)(frame[^1] & (0xFF << trailingPaddingBits)));
  }

  private static void WriteHeader(Stream output, bool isWb, int channels) {
    if (channels is < 1 or > 6)
      throw new ArgumentOutOfRangeException(nameof(channels), "RFC 4867 AMR storage supports 1-6 channels.");

    if (channels == 1) {
      output.Write(isWb ? MagicWb : MagicNb);
      return;
    }

    output.Write(isWb ? MagicWbMc : MagicNbMc);
    Span<byte> channelDescription = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(channelDescription, (uint)channels);
    output.Write(channelDescription);
  }

  private static short[] ReadPcm16(ReadOnlySpan<byte> data) {
    if ((data.Length & 1) != 0)
      throw new InvalidDataException("PCM16 payload has odd length.");
    var samples = new short[data.Length / 2];
    for (var index = 0; index < samples.Length; ++index)
      samples[index] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(index * 2, 2));
    return samples;
  }

  private static short[][] Deinterleave(ReadOnlySpan<short> samples, int channels) {
    if (channels < 1 || samples.Length % channels != 0)
      throw new InvalidDataException("PCM sample count is not divisible by the channel count.");

    var samplesPerChannel = samples.Length / channels;
    var result = new short[channels][];
    for (var channel = 0; channel < channels; ++channel) {
      var destination = result[channel] = new short[samplesPerChannel];
      for (var sample = 0; sample < samplesPerChannel; ++sample)
        destination[sample] = samples[sample * channels + channel];
    }
    return result;
  }

  private static bool TryResolveVariant(string codecId, int sampleRate, out bool isWb, out string? reason) {
    switch (codecId.Trim().ToLowerInvariant()) {
      case "amr-nb":
      case "amrnb":
        isWb = false;
        reason = null;
        return true;
      case "amr-wb":
      case "amrwb":
        isWb = true;
        reason = null;
        return true;
      case "amr":
        if (sampleRate == AmrNbCodec.SampleRate) {
          isWb = false;
          reason = null;
          return true;
        }
        if (sampleRate == AmrWbCodec.SampleRate) {
          isWb = true;
          reason = null;
          return true;
        }
        isWb = false;
        reason = "generic 'amr' is selected by sample rate and requires 8000 Hz (NB) or 16000 Hz (WB).";
        return false;
      default:
        isWb = false;
        reason = $"AMR storage cannot carry codec '{codecId}'.";
        return false;
    }
  }

  private static bool ResolveVariant(string codecId, int sampleRate)
    => TryResolveVariant(codecId, sampleRate, out var isWb, out var reason)
      ? isWb
      : throw new NotSupportedException(reason);

  private static string CanonicalCodecId(bool isWb) => isWb ? "amr-wb" : "amr-nb";

  private static AmrNbMode ParseNbMode(string text) => text.Trim().ToLowerInvariant() switch {
    "4.75" or "475" or "mr475" => AmrNbMode.Mr475,
    "5.15" or "515" or "mr515" => AmrNbMode.Mr515,
    "5.90" or "5.9" or "590" or "59" or "mr59" => AmrNbMode.Mr59,
    "6.70" or "6.7" or "670" or "67" or "mr67" => AmrNbMode.Mr67,
    "7.40" or "7.4" or "740" or "74" or "mr74" => AmrNbMode.Mr74,
    "7.95" or "795" or "mr795" => AmrNbMode.Mr795,
    "10.2" or "102" or "mr102" => AmrNbMode.Mr102,
    "12.2" or "122" or "mr122" => AmrNbMode.Mr122,
    _ => throw new ArgumentException(
      $"Unknown AMR-NB mode '{text}'. Expected 4.75, 5.15, 5.90, 6.70, 7.40, 7.95, 10.2, or 12.2 kbit/s.")
  };

  private static AmrWbMode ParseWbMode(string text) => text.Trim().ToLowerInvariant() switch {
    "6.60" or "6.6" or "660" or "mr660" => AmrWbMode.Mr660,
    "8.85" or "885" or "mr885" => AmrWbMode.Mr885,
    "12.65" or "1265" or "mr1265" => AmrWbMode.Mr1265,
    "14.25" or "1425" or "mr1425" => AmrWbMode.Mr1425,
    "15.85" or "1585" or "mr1585" => AmrWbMode.Mr1585,
    "18.25" or "1825" or "mr1825" => AmrWbMode.Mr1825,
    "19.85" or "1985" or "mr1985" => AmrWbMode.Mr1985,
    "23.05" or "2305" or "mr2305" => AmrWbMode.Mr2305,
    "23.85" or "2385" or "mr2385" => AmrWbMode.Mr2385,
    _ => throw new ArgumentException(
      $"Unknown AMR-WB mode '{text}'. Expected 6.60, 8.85, 12.65, 14.25, 15.85, 18.25, 19.85, 23.05, or 23.85 kbit/s.")
  };
}
