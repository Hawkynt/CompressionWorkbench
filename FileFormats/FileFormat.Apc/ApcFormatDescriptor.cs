#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Codec.WsAdpcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Apc;

/// <summary>
/// CRYO APC (<c>.apc</c>) audio. The little-endian header is
/// <c>"CRYO_APC" (8) | version (4, conventionally "1.20") | u32 sampleCount |
/// u32 sampleRate | i32 leftInitialSample | i32 rightInitialSample | u32 stereoFlag</c>,
/// followed by continuous IMA-ADPCM nibbles. The IMA step index starts at zero and the
/// signed initial-sample fields seed the predictor(s). APC packs the high nibble first:
/// mono consumes high then low with one state, while stereo stores left in the high nibble
/// and right in the low nibble with independent states.
/// <para>
/// The descriptor supports the pseudo-archive view, PCM16 mono/stereo decode and encode,
/// standalone creation from channel WAVs, and packet-preserving APC demux/mux. APC packet
/// streams use the format-specific <c>ima-adpcm-apc</c> codec id so they cannot be confused
/// with block-framed WAVE IMA-ADPCM during generic remuxing.
/// </para>
/// </summary>
public sealed class ApcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable,
  IAudioContainerFormat, IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource, IAudioMuxTarget {

  private static readonly string[] EncodeCodecs = ["ima-adpcm-apc", "adpcm-ima-apc", "adpcm_ima_apc", "ima-adpcm"];
  private static readonly string[] MuxCodecs = ["ima-adpcm-apc", "adpcm-ima-apc", "adpcm_ima_apc"];

  private const int HeaderSize = 32;
  private const string DefaultVersion = "1.20";

  public string Id => "Apc";
  public string DisplayName => "CRYO APC";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".apc";
  public IReadOnlyList<string> Extensions => [".apc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("CRYO_APC"u8.ToArray(), Confidence: 0.99)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ima-adpcm", "IMA-ADPCM (CRYO APC packing)")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "CRYO APC seeded continuous IMA-ADPCM; PCM encode/decode and byte-preserving demux/mux.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  public long? MaxTotalArchiveSize => null;

  public string AcceptedInputsDescription =>
    "APC accepts FULL.apc or one/two mono signed PCM16 WAV channel files with matching sample rate and frame count.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName);
    if (name.Equals("FULL.apc", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }

    reason = $"not an APC input (got {input.ArchiveName}); {this.AcceptedInputsDescription}";
    return false;
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var files = FormatHelpers.FilesOnly(inputs).ToList();
    var full = files.FirstOrDefault(static file =>
      Path.GetFileName(file.Name).Equals("FULL.apc", StringComparison.OrdinalIgnoreCase));
    if (full.Data is not null) {
      _ = ParseApc(full.Data);
      output.Write(full.Data);
      return;
    }

    var channels = files
      .Where(static file => file.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static file => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(file.Name)))
      .Select(static file => new WavReader().ReadCanonicalPcm(file.Data))
      .ToArray();
    if (channels.Length is < 1 or > 2)
      throw new InvalidOperationException("APC creation requires one or two mono WAV channels.");

    var first = channels[0];
    if (first.NumChannels != 1 || first.FormatCode != 1 || first.BitsPerSample != 16)
      throw new InvalidOperationException("APC creation requires signed PCM16 mono WAV inputs.");
    if (channels.Any(channel => channel.NumChannels != 1 || channel.FormatCode != 1
                                || channel.BitsPerSample != 16 || channel.SampleRate != first.SampleRate
                                || channel.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All APC channel WAVs must be signed PCM16 mono with matching sample rate and frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(static channel => channel.InterleavedPcm).ToList(), 16);
    var pcm = new AudioPcmBuffer(new AudioPcmFormat(first.SampleRate, channels.Length, 16), interleaved);
    this.EncodePcm(output, pcm, options.Method ?? options.GetString("codec") ?? "ima-adpcm", options);
  }

  public IReadOnlyList<string> SupportedEncodeCodecs => EncodeCodecs;

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);
    ArgumentNullException.ThrowIfNull(options);

    if (!IsEncodeCodec(codecId)) {
      reason = $"APC does not support codec '{codecId}'.";
      return false;
    }
    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = "APC encoding requires signed PCM16 input.";
      return false;
    }
    if (format.Channels is not (1 or 2)) {
      reason = "APC supports mono or stereo only.";
      return false;
    }
    if (format.SampleRate <= 0) {
      reason = "APC sample rate must be positive.";
      return false;
    }
    if (options.HasOption("sample-count")) {
      reason = "APC sample-count is derived from PCM frame count and cannot be overridden during encoding.";
      return false;
    }

    return TryValidateHeaderOptions(format.Channels, options, properties: null, out reason);
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(pcm);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);

    var frameBytes = checked(pcm.Format.Channels * 2);
    if (pcm.InterleavedData.Length % frameBytes != 0)
      throw new InvalidDataException("APC PCM payload is not an integral number of frames.");

    var frameCount = pcm.InterleavedData.Length / frameBytes;
    if (pcm.Format.Channels == 1 && (frameCount & 1) != 0)
      throw new NotSupportedException("APC mono stores two samples per byte and its sample-count-defined payload size requires an even frame count.");

    var samples = ReadPcm16(pcm.InterleavedData);
    var defaultLeft = samples.Length == 0 ? 0 : samples[0];
    var defaultRight = pcm.Format.Channels == 2 && samples.Length >= 2 ? samples[1] : 0;
    var leftInitial = ResolveInt(options, null, "left-initial-sample", defaultLeft);
    var rightInitial = ResolveInt(options, null, "right-initial-sample", defaultRight);
    var stereoFlag = ResolveStereoFlag(pcm.Format.Channels, options, null);
    var version = ResolveVersion(options, null);

    var payload = EncodePayload(samples, pcm.Format.Channels, leftInitial, rightInitial);
    WriteHeader(output, version, (uint)frameCount, pcm.Format.SampleRate, leftInitial, rightInitial, stereoFlag);
    output.Write(payload);
  }

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var parsed = ParseApc(ReadAll(input));
    return new AudioPcmBuffer(
      new AudioPcmFormat(parsed.SampleRate, parsed.Channels, 16, AudioPcmEncoding.SignedInteger),
      ShortsToLePcm(DecodeSamples(parsed)));
  }

  public IReadOnlyList<string> SupportedMuxCodecs => MuxCodecs;

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);

    if (!IsMuxCodec(stream.CodecId)) {
      reason = $"APC cannot mux codec '{stream.CodecId}'; packet remux requires APC-specific continuous IMA packing.";
      return false;
    }
    if (stream.Channels is not (1 or 2)) {
      reason = "APC carries mono or stereo streams only.";
      return false;
    }
    if (stream.SampleRate <= 0) {
      reason = "APC sample rate must be positive.";
      return false;
    }
    if (stream.BitsPerSample is not (0 or 4)) {
      reason = "APC carries 4-bit IMA-ADPCM nibbles.";
      return false;
    }
    if (!TryValidateHeaderOptions(stream.Channels, options, stream.Properties, out reason))
      return false;
    if (TryRawValue(options, stream.Properties, "sample-count") is { } sampleCountText) {
      if (!uint.TryParse(sampleCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleCount)) {
        reason = $"APC sample-count '{sampleCountText}' is not an unsigned 32-bit integer.";
        return false;
      }
      if (stream.Channels == 1 && (sampleCount & 1) != 0) {
        reason = "APC mono sample-count must be even.";
        return false;
      }
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

    using var payloadStream = new MemoryStream();
    foreach (var packet in stream.Packets) {
      if (packet.IsHeader)
        throw new InvalidDataException("APC has no in-band codec header packet; use stream properties/codec private data for header metadata.");
      payloadStream.Write(packet.Data);
    }
    var payload = payloadStream.ToArray();

    var sampleCount = ResolveSampleCount(stream, options, payload.Length);
    ValidatePayloadLength(sampleCount, stream.Format.Channels, payload.Length);

    var version = ResolveVersion(options, stream.Format.Properties);
    var leftDefault = ReadPrivatePredictor(stream.CodecPrivateData, 0);
    var rightDefault = ReadPrivatePredictor(stream.CodecPrivateData, 4);
    var leftInitial = ResolveInt(options, stream.Format.Properties, "left-initial-sample", leftDefault);
    var rightInitial = ResolveInt(options, stream.Format.Properties, "right-initial-sample", rightDefault);
    var stereoFlag = ResolveStereoFlag(stream.Format.Channels, options, stream.Format.Properties);

    WriteHeader(output, version, sampleCount, stream.Format.SampleRate, leftInitial, rightInitial, stereoFlag);
    output.Write(payload);
  }

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    stream = null;
    try {
      var parsed = ParseApc(ReadAll(input));
      var privateData = new byte[8];
      BinaryPrimitives.WriteInt32LittleEndian(privateData, parsed.LeftInitialSample);
      BinaryPrimitives.WriteInt32LittleEndian(privateData.AsSpan(4), parsed.RightInitialSample);
      var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        ["version"] = parsed.Version,
        ["sample-count"] = parsed.SampleCount.ToString(CultureInfo.InvariantCulture),
        ["left-initial-sample"] = parsed.LeftInitialSample.ToString(CultureInfo.InvariantCulture),
        ["right-initial-sample"] = parsed.RightInitialSample.ToString(CultureInfo.InvariantCulture),
        ["stereo-flag"] = parsed.StereoFlag.ToString(CultureInfo.InvariantCulture),
        ["nibble-order"] = "high-first",
      };
      stream = new AudioEncodedStream(
        new AudioStreamFormat("ima-adpcm-apc", parsed.SampleRate, parsed.Channels, 4, properties),
        [new AudioPacket(parsed.Payload, parsed.SampleCount)],
        privateData);
      return true;
    } catch (InvalidDataException) {
      return false;
    } catch (OverflowException) {
      return false;
    }
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    var blob = ReadAll(stream);
    var parsed = ParseApc(blob);
    var samples = DecodeSamples(parsed);

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.apc", "Container", blob),
    };

    var pcm = ShortsToLePcm(samples);
    if (parsed.Channels == 1) {
      entries.Add(new("MONO.wav", "Channel",
        PcmCodec.ToWavBlob(pcm, channels: 1, parsed.SampleRate, bitsPerSample: 16, formatCode: 1), "ima-adpcm"));
    } else {
      foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, parsed.Channels, parsed.SampleRate, 16))
        entries.Add(new($"{name}.wav", "Channel", wav, "ima-adpcm"));
    }

    var info = new StringBuilder();
    info.AppendLine($"version={parsed.Version}");
    info.AppendLine($"sample_rate={parsed.SampleRate}");
    info.AppendLine($"channels={parsed.Channels}");
    info.AppendLine($"sample_count={parsed.SampleCount}");
    info.AppendLine($"left_initial_sample={parsed.LeftInitialSample}");
    info.AppendLine($"right_initial_sample={parsed.RightInitialSample}");
    info.AppendLine($"stereo_flag={parsed.StereoFlag}");
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private readonly record struct ParsedApc(
    string Version,
    int SampleRate,
    int Channels,
    uint SampleCount,
    int LeftInitialSample,
    int RightInitialSample,
    uint StereoFlag,
    byte[] Payload
  );

  private static ParsedApc ParseApc(ReadOnlySpan<byte> blob) {
    if (blob.Length < HeaderSize)
      throw new InvalidDataException("APC too short for 32-byte header.");
    if (!blob[..8].SequenceEqual("CRYO_APC"u8))
      throw new InvalidDataException("Missing CRYO_APC magic.");

    var version = Encoding.Latin1.GetString(blob.Slice(8, 4));
    var sampleCount = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(12, 4));
    var rawSampleRate = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(16, 4));
    if (rawSampleRate is 0 or > int.MaxValue)
      throw new InvalidDataException($"APC sample rate {rawSampleRate} cannot be represented as a positive .NET audio sample rate.");

    var leftInitial = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(20, 4));
    var rightInitial = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(24, 4));
    var stereoFlag = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(28, 4));
    var channels = stereoFlag == 0 ? 1 : 2;
    var payloadLength = blob.Length - HeaderSize;
    ValidatePayloadLength(sampleCount, channels, payloadLength);

    return new ParsedApc(
      version,
      (int)rawSampleRate,
      channels,
      sampleCount,
      leftInitial,
      rightInitial,
      stereoFlag,
      blob[HeaderSize..].ToArray());
  }

  private static short[] DecodeSamples(ParsedApc parsed) {
    var totalSamples = checked((int)((long)parsed.SampleCount * parsed.Channels));
    var samples = new short[totalSamples];
    var left = new StandardImaCodec.State(parsed.LeftInitialSample, 0);

    if (parsed.Channels == 1) {
      var o = 0;
      foreach (var b in parsed.Payload) {
        samples[o++] = StandardImaCodec.DecodeOneNibble((byte)(b >> 4), ref left);
        samples[o++] = StandardImaCodec.DecodeOneNibble((byte)(b & 0x0F), ref left);
      }
      return samples;
    }

    var right = new StandardImaCodec.State(parsed.RightInitialSample, 0);
    var offset = 0;
    foreach (var b in parsed.Payload) {
      samples[offset++] = StandardImaCodec.DecodeOneNibble((byte)(b >> 4), ref left);
      samples[offset++] = StandardImaCodec.DecodeOneNibble((byte)(b & 0x0F), ref right);
    }
    return samples;
  }

  private static byte[] EncodePayload(ReadOnlySpan<short> samples, int channels, int leftInitial, int rightInitial) {
    var frames = samples.Length / channels;
    if (channels == 1) {
      var payload = new byte[frames / 2];
      var state = new StandardImaCodec.State(leftInitial, 0);
      for (var frame = 0; frame < frames; frame += 2) {
        var high = StandardImaCodec.EncodeOneNibble(samples[frame], ref state);
        var low = StandardImaCodec.EncodeOneNibble(samples[frame + 1], ref state);
        payload[frame / 2] = (byte)((high << 4) | low);
      }
      return payload;
    }

    var stereo = new byte[frames];
    var left = new StandardImaCodec.State(leftInitial, 0);
    var right = new StandardImaCodec.State(rightInitial, 0);
    for (var frame = 0; frame < frames; ++frame) {
      var high = StandardImaCodec.EncodeOneNibble(samples[frame * 2], ref left);
      var low = StandardImaCodec.EncodeOneNibble(samples[frame * 2 + 1], ref right);
      stereo[frame] = (byte)((high << 4) | low);
    }
    return stereo;
  }

  private static void WriteHeader(
    Stream output,
    byte[] version,
    uint sampleCount,
    int sampleRate,
    int leftInitial,
    int rightInitial,
    uint stereoFlag
  ) {
    Span<byte> header = stackalloc byte[HeaderSize];
    "CRYO_APC"u8.CopyTo(header);
    version.CopyTo(header[8..]);
    BinaryPrimitives.WriteUInt32LittleEndian(header[12..], sampleCount);
    BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)sampleRate);
    BinaryPrimitives.WriteInt32LittleEndian(header[20..], leftInitial);
    BinaryPrimitives.WriteInt32LittleEndian(header[24..], rightInitial);
    BinaryPrimitives.WriteUInt32LittleEndian(header[28..], stereoFlag);
    output.Write(header);
  }

  private static void ValidatePayloadLength(uint sampleCount, int channels, int payloadLength) {
    if (channels == 1 && (sampleCount & 1) != 0)
      throw new InvalidDataException("APC mono sample count must be even: the format defines its compressed size as sampleCount / 2 bytes.");

    var expected = channels == 2 ? (long)sampleCount : sampleCount / 2L;
    if (payloadLength != expected)
      throw new InvalidDataException($"APC header declares {sampleCount} samples/{channels} channel(s), requiring {expected} payload bytes, but the file contains {payloadLength}.");
  }

  private static bool TryValidateHeaderOptions(
    int channels,
    FormatCreateOptions options,
    IReadOnlyDictionary<string, string>? properties,
    out string? reason
  ) {
    if (!TryResolveVersion(options, properties, out _, out reason))
      return false;
    if (!TryResolveInt(options, properties, "left-initial-sample", 0, out _, out reason))
      return false;
    if (!TryResolveInt(options, properties, "right-initial-sample", 0, out _, out reason))
      return false;
    if (!TryResolveUInt(options, properties, "stereo-flag", channels == 2 ? 1u : 0u, out var stereoFlag, out reason))
      return false;
    if ((stereoFlag == 0) != (channels == 1)) {
      reason = channels == 1
        ? "APC mono requires stereo-flag=0."
        : "APC stereo requires a non-zero stereo-flag.";
      return false;
    }
    reason = null;
    return true;
  }

  private static uint ResolveSampleCount(AudioEncodedStream stream, FormatCreateOptions options, int payloadLength) {
    if (TryRawValue(options, stream.Format.Properties, "sample-count") is { } raw) {
      if (!uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var explicitCount))
        throw new ArgumentException($"APC sample-count '{raw}' is not an unsigned 32-bit integer.");
      return explicitCount;
    }

    if (stream.Packets.Count > 0 && stream.Packets.All(static packet => !packet.IsHeader && packet.DurationSamples > 0)) {
      long duration = 0;
      foreach (var packet in stream.Packets)
        duration = checked(duration + packet.DurationSamples);
      if (duration > uint.MaxValue)
        throw new NotSupportedException("APC's 32-bit sample-count field cannot represent the packet durations.");
      return (uint)duration;
    }

    var inferred = stream.Format.Channels == 2 ? (long)payloadLength : checked((long)payloadLength * 2);
    if (inferred > uint.MaxValue)
      throw new NotSupportedException("APC's 32-bit sample-count field cannot represent the encoded payload.");
    return (uint)inferred;
  }

  private static byte[] ResolveVersion(FormatCreateOptions options, IReadOnlyDictionary<string, string>? properties) {
    if (!TryResolveVersion(options, properties, out var result, out var reason))
      throw new ArgumentException(reason);
    return result;
  }

  private static bool TryResolveVersion(
    FormatCreateOptions options,
    IReadOnlyDictionary<string, string>? properties,
    out byte[] version,
    out string? reason
  ) {
    var text = TryRawValue(options, properties, "version") ?? DefaultVersion;
    if (text.Length != 4 || text.Any(static c => c > byte.MaxValue)) {
      version = [];
      reason = "APC version is exactly four single-byte characters (for example '1.20').";
      return false;
    }

    version = Encoding.Latin1.GetBytes(text);
    reason = null;
    return true;
  }

  private static int ResolveInt(
    FormatCreateOptions options,
    IReadOnlyDictionary<string, string>? properties,
    string key,
    int fallback
  ) {
    if (!TryResolveInt(options, properties, key, fallback, out var result, out var reason))
      throw new ArgumentException(reason);
    return result;
  }

  private static bool TryResolveInt(
    FormatCreateOptions options,
    IReadOnlyDictionary<string, string>? properties,
    string key,
    int fallback,
    out int value,
    out string? reason
  ) {
    var raw = TryRawValue(options, properties, key);
    if (raw is null) {
      value = fallback;
      reason = null;
      return true;
    }
    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) {
      reason = $"APC {key} '{raw}' is not a signed 32-bit integer.";
      return false;
    }
    reason = null;
    return true;
  }

  private static uint ResolveStereoFlag(
    int channels,
    FormatCreateOptions options,
    IReadOnlyDictionary<string, string>? properties
  ) {
    if (!TryResolveUInt(options, properties, "stereo-flag", channels == 2 ? 1u : 0u, out var value, out var reason))
      throw new ArgumentException(reason);
    if ((value == 0) != (channels == 1))
      throw new ArgumentException(channels == 1 ? "APC mono requires stereo-flag=0." : "APC stereo requires a non-zero stereo-flag.");
    return value;
  }

  private static bool TryResolveUInt(
    FormatCreateOptions options,
    IReadOnlyDictionary<string, string>? properties,
    string key,
    uint fallback,
    out uint value,
    out string? reason
  ) {
    var raw = TryRawValue(options, properties, key);
    if (raw is null) {
      value = fallback;
      reason = null;
      return true;
    }
    if (!uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) {
      reason = $"APC {key} '{raw}' is not an unsigned 32-bit integer.";
      return false;
    }
    reason = null;
    return true;
  }

  private static string? TryRawValue(
    FormatCreateOptions options,
    IReadOnlyDictionary<string, string>? properties,
    string key
  ) {
    if (options.GetString(key) is { } option)
      return option;
    return properties is not null && properties.TryGetValue(key, out var property) ? property : null;
  }

  private static int ReadPrivatePredictor(byte[]? privateData, int offset)
    => privateData is { Length: >= 8 }
      ? BinaryPrimitives.ReadInt32LittleEndian(privateData.AsSpan(offset, 4))
      : 0;

  private static bool IsEncodeCodec(string codecId)
    => EncodeCodecs.Contains(codecId, StringComparer.OrdinalIgnoreCase);

  private static bool IsMuxCodec(string codecId)
    => MuxCodecs.Contains(codecId, StringComparer.OrdinalIgnoreCase);

  private static short[] ReadPcm16(ReadOnlySpan<byte> data) {
    if ((data.Length & 1) != 0)
      throw new InvalidDataException("PCM16 payload has odd byte length.");
    var samples = new short[data.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
    return samples;
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), samples[i]);
    return pcm;
  }

  private static byte[] ReadAll(Stream input) {
    if (input.CanSeek)
      input.Position = 0;
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    return memory.ToArray();
  }
}
