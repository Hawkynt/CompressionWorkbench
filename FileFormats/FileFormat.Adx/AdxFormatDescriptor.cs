#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.CriAdx;
using Codec.Mp3;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Adx;

/// <summary>
/// CRI ADX/AHX audio container. Standard ADX, fixed-coefficient ADX and exponential-scale ADX
/// can be decoded and encoded; v3/v4/v5 headers, loop metadata, header alignment and v4 type-8/
/// type-9 scale encryption are selectable. The canonical audio interfaces expose PCM conversion
/// plus packet-preserving ADX/AHX demux/mux for lossless remuxing.
/// </summary>
public sealed class AdxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable,
  IAudioContainerFormat, IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource, IAudioMuxTarget,
  IFormatOptionsSchema {

  private static readonly string[] EncodeCodecs = ["adx"];
  private static readonly string[] MuxCodecs = ["adx", "ahx"];

  public string Id => "Adx";
  public string DisplayName => "CRI ADX";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".adx";
  public IReadOnlyList<string> Extensions => [".adx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x80, 0x00], Confidence: 0.4)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("adx", "CRI ADX ADPCM"),
    new("adx-fixed", "CRI ADX fixed predictors"),
    new("adx-exp", "CRI ADX exponential scale"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "CRI ADX/AHX; PCM encode/decode plus packet-preserving demux/mux.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Encoding", "ADX encoding", FormatOptionKind.Enum, "Standard", ["Standard", "Fixed", "Exponential"],
      "Type 3 standard ADX, type 2 fixed predictors, or type 4 exponential scale."),
    new("Version", "ADX header version", FormatOptionKind.Enum, "3", ["3", "4", "5"],
      "Version 3 is the historical default; version 4 stores decoder histories and supports encryption; version 5 has no loop layout."),
    new("HighpassFrequency", "High-pass frequency", FormatOptionKind.Integer, "500", null,
      "Predictor cutoff in Hz. Must be below Nyquist; fixed-predictor ADX stores but does not use it."),
    new("LoopStartSample", "Loop start sample", FormatOptionKind.Integer, "-1", null,
      "-1 disables looping. Supply both loop start and loop end for header versions 3 or 4."),
    new("LoopEndSample", "Loop end sample", FormatOptionKind.Integer, "-1", null,
      "Exclusive loop end sample; -1 disables looping."),
    new("EncryptionRevision", "ADX encryption", FormatOptionKind.Enum, "None", ["None", "8", "9"],
      "Version-4 type-3 ADX scale encryption. Supply the derived 15-bit LCG start/multiplier/addend values."),
    new("EncryptionStart", "Encryption start", FormatOptionKind.Integer, "0", null,
      "Derived 15-bit initial XOR value.", "EncryptionRevision=8|9"),
    new("EncryptionMultiplier", "Encryption multiplier", FormatOptionKind.Integer, "0", null,
      "Derived 15-bit LCG multiplier.", "EncryptionRevision=8|9"),
    new("EncryptionAddend", "Encryption addend", FormatOptionKind.Integer, "0", null,
      "Derived 15-bit LCG addend.", "EncryptionRevision=8|9"),
    new("HeaderAlignment", "Audio-data alignment", FormatOptionKind.Integer, "1", ["1", "32", "256", "2048", "4096"],
      "Power-of-two alignment for the encoded data start."),
    new("WriteEndMarker", "Write EOF marker", FormatOptionKind.Boolean, "false", null,
      "Append the conventional 0x8001 ADX terminator frame after the declared samples."),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => AudioPseudoArchive.List(BuildEntries(stream));
  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "ADX accepts FULL.adx or mono PCM16 WAV channel files with matching sample rate and frame count.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.adx" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not an ADX input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var fileList = FormatHelpers.FilesOnly(inputs).ToList();
    var full = fileList.FirstOrDefault(static f =>
      Path.GetFileName(f.Name).Equals("FULL.adx", StringComparison.OrdinalIgnoreCase));
    if (full.Data is not null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(static f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();
    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("ADX creation needs either FULL.adx or one or more per-channel mono WAVs.");

    var channels = channelBlobs.Select(static item => new WavReader().ReadCanonicalPcm(item.Data)).ToArray();
    var first = channels[0];
    if (channels.Any(static c => c.NumChannels != 1 || c.FormatCode != 1 || c.BitsPerSample != 16))
      throw new InvalidOperationException("ADX creation expects mono signed PCM16 WAV inputs.");
    if (channels.Any(c => c.SampleRate != first.SampleRate || c.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All ADX channel WAVs must share sample rate and frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(static c => c.InterleavedPcm).ToList(), 16);
    var pcm = new AudioPcmBuffer(new AudioPcmFormat(first.SampleRate, channels.Length, 16), interleaved);
    this.EncodePcm(output, pcm, "adx", options);
  }

  public IReadOnlyList<string> SupportedEncodeCodecs => EncodeCodecs;

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);
    ArgumentNullException.ThrowIfNull(options);
    if (!EncodeCodecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"codec '{codecId}' is not CRI ADX";
      return false;
    }
    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = "ADX encoder input must be signed PCM16";
      return false;
    }
    if (format.Channels is < 1 or > AdxCodec.MaxChannels || format.SampleRate <= 1) {
      reason = $"ADX requires 1 to {AdxCodec.MaxChannels} channels and a positive sample rate";
      return false;
    }
    try {
      var encodeOptions = ResolveEncodeOptions(options, totalSamples: null, format.SampleRate);
      if (encodeOptions.HighpassFrequency >= format.SampleRate / 2) {
        reason = "ADX high-pass frequency must be below Nyquist";
        return false;
      }
    } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or FormatException or OverflowException) {
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
    if (pcm.InterleavedData.Length % 2 != 0)
      throw new InvalidDataException("PCM16 input has an odd byte count.");

    var samples = LePcmToShorts(pcm.InterleavedData);
    if (samples.Length % pcm.Format.Channels != 0)
      throw new InvalidDataException("PCM input is not an integral number of channel frames.");
    var encodeOptions = ResolveEncodeOptions(options, samples.Length / pcm.Format.Channels, pcm.Format.SampleRate);
    output.Write(AdxCodec.Encode(samples, pcm.Format.Channels, pcm.Format.SampleRate, encodeOptions));
  }

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var blob = ReadAll(input);
    var info = AdxCodec.ReadInfo(blob);
    if (info.IsAhx) {
      if (info.Revision != 0)
        throw new NotSupportedException("Encrypted AHX requires a key and is not available through the keyless PCM source interface.");
      var payload = blob.AsSpan(info.DataOffset).ToArray();
      var streamInfo = Mp3Codec.ReadStreamInfo(new MemoryStream(payload, writable: false));
      using var decoded = new MemoryStream();
      Mp3Codec.Decompress(new MemoryStream(payload, writable: false), decoded);
      return new AudioPcmBuffer(
        new AudioPcmFormat(streamInfo.SampleRate > 0 ? streamInfo.SampleRate : info.SampleRate,
          streamInfo.Channels > 0 ? streamInfo.Channels : info.Channels, 16),
        decoded.ToArray());
    }
    var (samples, channels, sampleRate) = AdxCodec.Decode(blob);
    return new AudioPcmBuffer(new AudioPcmFormat(sampleRate, channels, 16), ShortsToLePcm(samples));
  }

  public IReadOnlyList<string> SupportedMuxCodecs => MuxCodecs;

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!MuxCodecs.Contains(stream.CodecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"ADX can mux ADX or AHX payloads, not codec '{stream.CodecId}'";
      return false;
    }
    if (stream.SampleRate <= 0) {
      reason = "ADX/AHX requires a positive sample rate";
      return false;
    }
    if (stream.CodecId.Equals("ahx", StringComparison.OrdinalIgnoreCase) && stream.Channels != 1) {
      reason = "CRI AHX is mono";
      return false;
    }
    if (stream.CodecId.Equals("adx", StringComparison.OrdinalIgnoreCase) && stream.Channels is < 1 or > AdxCodec.MaxChannels) {
      reason = $"ADX supports 1 to {AdxCodec.MaxChannels} channels";
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
      throw new ArgumentException("ADX/AHX muxing requires at least one encoded packet.", nameof(stream));
    if (stream.Packets.Any(static packet => packet.IsHeader))
      throw new InvalidDataException("ADX/AHX does not use out-of-band header packets.");

    if (TryUsePrivateHeader(stream, out var privateHeader)) {
      output.Write(privateHeader);
    } else if (stream.Format.CodecId.Equals("ahx", StringComparison.OrdinalIgnoreCase)) {
      var total = PropertyInt(stream.Format, "total-samples") ?? checked((int)stream.Packets.Sum(static p => p.DurationSamples));
      var type = checked((byte)(PropertyInt(stream.Format, "encoding-type") ?? AdxCodec.EncodingTypeAhx11));
      var revision = checked((byte)(PropertyInt(stream.Format, "revision") ?? 0));
      var alignment = options.GetOptionInt("HeaderAlignment", 1);
      output.Write(AdxCodec.BuildAhxHeader(stream.Format.SampleRate, total, type, revision, alignment));
    } else {
      var total = PropertyInt(stream.Format, "total-samples") ?? checked((int)stream.Packets.Sum(static p => p.DurationSamples));
      var encoding = checked((byte)(PropertyInt(stream.Format, "encoding-type") ?? AdxCodec.EncodingTypeStandard));
      var version = PropertyInt(stream.Format, "version") ?? 3;
      var revision = checked((byte)(PropertyInt(stream.Format, "revision") ?? 0));
      var highpass = PropertyInt(stream.Format, "highpass-frequency") ?? 500;
      var dataOffset = version == 4 ? 0x18 + Math.Max(8, stream.Format.Channels * 4) + 6 : 0x1A;
      var info = new AdxCodec.AdxInfo(encoding, AdxCodec.FrameSize, AdxCodec.BitDepth,
        stream.Format.Channels, stream.Format.SampleRate, total, highpass, version, revision, dataOffset);
      output.Write(AdxCodec.RebuildHeader(info, dataOffset));
    }

    foreach (var packet in stream.Packets) {
      if (packet.Data.Length == 0)
        throw new InvalidDataException("ADX/AHX cannot mux an empty encoded packet.");
      output.Write(packet.Data);
    }
  }

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    var blob = ReadAll(input);
    stream = null;
    AdxCodec.AdxInfo info;
    try {
      info = AdxCodec.ReadInfo(blob);
    } catch (InvalidDataException) {
      return false;
    }

    var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      ["encoding-type"] = info.EncodingType.ToString(CultureInfo.InvariantCulture),
      ["version"] = info.Version.ToString(CultureInfo.InvariantCulture),
      ["revision"] = info.Revision.ToString(CultureInfo.InvariantCulture),
      ["highpass-frequency"] = info.HighpassFrequency.ToString(CultureInfo.InvariantCulture),
      ["total-samples"] = info.TotalSamples.ToString(CultureInfo.InvariantCulture),
      ["data-offset"] = info.DataOffset.ToString(CultureInfo.InvariantCulture),
    };
    if (info.LoopStartSample is { } loopStart)
      properties["loop-start-sample"] = loopStart.ToString(CultureInfo.InvariantCulture);
    if (info.LoopEndSample is { } loopEnd)
      properties["loop-end-sample"] = loopEnd.ToString(CultureInfo.InvariantCulture);

    var packets = new List<AudioPacket>();
    if (info.IsAhx) {
      if (blob.Length <= info.DataOffset)
        return false;
      packets.Add(new AudioPacket(blob.AsSpan(info.DataOffset).ToArray(), info.TotalSamples));
      stream = new AudioEncodedStream(
        new AudioStreamFormat("ahx", info.SampleRate, info.Channels, Properties: properties),
        packets,
        blob.AsSpan(0, info.DataOffset).ToArray());
      return true;
    }

    if (!info.IsAdxAdpcm || info.BlockSize != AdxCodec.FrameSize || info.BitDepth != AdxCodec.BitDepth)
      return false;
    var groups = info.TotalSamples == 0 ? 0 : (info.TotalSamples + AdxCodec.SamplesPerFrame - 1) / AdxCodec.SamplesPerFrame;
    var groupSize = checked(AdxCodec.FrameSize * info.Channels);
    var required = checked(info.DataOffset + groups * groupSize);
    if (required > blob.Length)
      throw new InvalidDataException("ADX payload is truncated relative to its declared sample count.");
    for (var group = 0; group < groups; ++group) {
      var duration = Math.Min(AdxCodec.SamplesPerFrame, info.TotalSamples - group * AdxCodec.SamplesPerFrame);
      packets.Add(new AudioPacket(blob.AsSpan(info.DataOffset + group * groupSize, groupSize).ToArray(), duration));
    }
    if (required < blob.Length)
      packets.Add(new AudioPacket(blob.AsSpan(required).ToArray()));

    stream = new AudioEncodedStream(
      new AudioStreamFormat("adx", info.SampleRate, info.Channels, AdxCodec.BitDepth, properties),
      packets,
      blob.AsSpan(0, info.DataOffset).ToArray());
    return true;
  }

  private static bool TryUsePrivateHeader(AudioEncodedStream stream, out byte[] header) {
    header = [];
    if (stream.CodecPrivateData is not { Length: >= 20 } privateData)
      return false;
    try {
      var info = AdxCodec.ReadInfo(privateData);
      var expectedAhx = stream.Format.CodecId.Equals("ahx", StringComparison.OrdinalIgnoreCase);
      if (info.IsAhx != expectedAhx || info.SampleRate != stream.Format.SampleRate || info.Channels != stream.Format.Channels)
        throw new InvalidDataException("ADX codec-private header does not match the advertised encoded stream geometry.");
      header = privateData;
      return true;
    } catch (InvalidDataException) {
      return false;
    }
  }

  private static AdxEncodeOptions ResolveEncodeOptions(FormatCreateOptions options, int? totalSamples, int sampleRate) {
    var encoding = options.GetOption("Encoding", "Standard").ToLowerInvariant() switch {
      "standard" or "3" => AdxEncodingMode.Standard,
      "fixed" or "2" => AdxEncodingMode.Fixed,
      "exponential" or "exp" or "4" => AdxEncodingMode.Exponential,
      var value => throw new FormatException($"Unknown ADX encoding '{value}'."),
    };
    var version = options.GetOption("Version", "3") switch {
      "3" => AdxHeaderVersion.Version3,
      "4" => AdxHeaderVersion.Version4,
      "5" => AdxHeaderVersion.Version5,
      var value => throw new FormatException($"Unknown ADX header version '{value}'."),
    };
    var highpass = options.GetOptionInt("HighpassFrequency", 500);
    if (highpass < 0 || highpass >= sampleRate / 2)
      throw new ArgumentOutOfRangeException("HighpassFrequency", "ADX high-pass frequency must be non-negative and below Nyquist.");

    var start = options.GetOptionInt("LoopStartSample", -1);
    var end = options.GetOptionInt("LoopEndSample", -1);
    int? loopStart = null;
    int? loopEnd = null;
    if (start >= 0 || end >= 0) {
      if (start < 0 || end < 0)
        throw new ArgumentException("ADX loop start and end must be supplied together.");
      if (totalSamples is { } samples && (end <= start || end > samples))
        throw new ArgumentOutOfRangeException("LoopEndSample", "ADX loop range must satisfy start < end <= total samples.");
      loopStart = start;
      loopEnd = end;
    }
    if (version == AdxHeaderVersion.Version5 && loopStart.HasValue)
      throw new NotSupportedException("ADX version 5 does not define loop metadata.");

    AdxEncryptionKey? encryption = null;
    var encryptionText = options.GetOption("EncryptionRevision", "None");
    if (!encryptionText.Equals("None", StringComparison.OrdinalIgnoreCase) && encryptionText != "0") {
      if (!byte.TryParse(encryptionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision) || revision is not (8 or 9))
        throw new FormatException("ADX encryption revision must be None, 8, or 9.");
      if (version != AdxHeaderVersion.Version4 || encoding != AdxEncodingMode.Standard)
        throw new NotSupportedException("ADX encryption revisions 8/9 require version 4 with standard type-3 ADX.");
      var keyStart = CheckedKeyComponent(options, "EncryptionStart");
      var multiplier = CheckedKeyComponent(options, "EncryptionMultiplier");
      var addend = CheckedKeyComponent(options, "EncryptionAddend");
      encryption = new AdxEncryptionKey(keyStart, multiplier, addend, revision);
    }

    return new AdxEncodeOptions {
      Encoding = encoding,
      Version = version,
      HighpassFrequency = highpass,
      LoopStartSample = loopStart,
      LoopEndSample = loopEnd,
      Encryption = encryption,
      HeaderAlignment = options.GetOptionInt("HeaderAlignment", 1),
      WriteEndMarker = options.GetOptionBool("WriteEndMarker", false),
    };
  }

  private static ushort CheckedKeyComponent(FormatCreateOptions options, string name) {
    if (!options.TryGetInt(name, out var value) || value is < 0 or > 0x7FFF)
      throw new ArgumentOutOfRangeException(name, $"ADX {name} must be a 15-bit integer (0..32767).");
    return (ushort)value;
  }

  private static int? PropertyInt(AudioStreamFormat format, string key)
    => format.Properties is { } properties && properties.TryGetValue(key, out var text) &&
       int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
      ? value : null;

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    var blob = ReadAll(stream);
    var entries = new List<AudioPseudoArchive.Entry> { new("FULL.adx", "Container", blob, "adx") };
    try {
      var info = AdxCodec.ReadInfo(blob);
      if (info.IsAhx) {
        AddAhxEntries(blob, info, entries);
        return entries;
      }

      var (samples, channels, sampleRate) = AdxCodec.Decode(blob);
      var pcm = ShortsToLePcm(samples);
      if (channels == 1) {
        entries.Add(new("MONO.wav", "Channel", PcmCodec.ToWavBlob(pcm, 1, sampleRate, 16, 1), "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, channels, sampleRate, 16))
          entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
      }
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(BuildMetadata(info))));
    } catch (Exception) {
      // Opaque/unsupported/encrypted input still exposes the byte-exact FULL.adx entry.
    }
    return entries;
  }

  private static string BuildMetadata(AdxCodec.AdxInfo info) {
    var meta = new StringBuilder();
    meta.AppendLine($"sample_rate={info.SampleRate}");
    meta.AppendLine($"channels={info.Channels}");
    meta.AppendLine($"total_samples={info.TotalSamples}");
    meta.AppendLine($"version={info.Version}");
    meta.AppendLine($"revision={info.Revision}");
    meta.AppendLine($"highpass_frequency={info.HighpassFrequency}");
    meta.AppendLine($"encoding_type={info.EncodingType}");
    if (info.LoopStartSample is { } start) meta.AppendLine($"loop_start_sample={start}");
    if (info.LoopEndSample is { } end) meta.AppendLine($"loop_end_sample={end}");
    return meta.ToString();
  }

  private static void AddAhxEntries(byte[] blob, AdxCodec.AdxInfo info, List<AudioPseudoArchive.Entry> entries) {
    var meta = new StringBuilder(BuildMetadata(info));
    meta.AppendLine("codec=ahx");
    meta.AppendLine("payload=mpeg-layer2");
    var payload = info.DataOffset < blob.Length ? blob[info.DataOffset..] : [];
    try {
      if (info.Revision != 0)
        throw new NotSupportedException("Encrypted AHX requires an external key.");
      var streamInfo = Mp3Codec.ReadStreamInfo(new MemoryStream(payload, writable: false));
      using var pcmStream = new MemoryStream();
      Mp3Codec.Decompress(new MemoryStream(payload, writable: false), pcmStream);
      var pcm = pcmStream.ToArray();
      if (pcm.Length == 0)
        throw new InvalidDataException("AHX MPEG Layer II payload decoded to no samples.");
      var rate = streamInfo.SampleRate > 0 ? streamInfo.SampleRate : info.SampleRate;
      var channels = streamInfo.Channels > 0 ? streamInfo.Channels : 1;
      if (channels == 1)
        entries.Add(new("MONO.wav", "Channel", PcmCodec.ToWavBlob(pcm, 1, rate, 16, 1), "mp2"));
      else
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, channels, rate, 16))
          entries.Add(new($"{name}.wav", "Channel", wav, "mp2"));
    } catch (Exception) {
      meta.AppendLine("note=AHX payload could not be decoded; FULL.adx remains available.");
    }
    entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));
  }

  private static byte[] ReadAll(Stream input) {
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    return ms.ToArray();
  }

  private static byte[] ShortsToLePcm(ReadOnlySpan<short> samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), samples[i]);
    return pcm;
  }

  private static short[] LePcmToShorts(ReadOnlySpan<byte> pcm) {
    var samples = new short[pcm.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2));
    return samples;
  }
}
