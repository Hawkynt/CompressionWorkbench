#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Codec.AdpcmX;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Ast;

/// <summary>
/// GameCube/Wii AST audio container with PCM16BE and AFC encode/decode, pseudo-archive extraction,
/// encoded BLCK demux/mux, and packet-preserving reblocking.
/// </summary>
public sealed class AstFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable, IFormatOptionsSchema,
  IAudioContainerFormat, IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource, IAudioMuxTarget {

  private static readonly string[] EncodeCodecs = ["pcm16be", "afc"];
  private const int PrivateHeaderBytes = 36;

  public string Id => "Ast";
  public string DisplayName => "AST (GameCube/Wii stream)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ast";
  public IReadOnlyList<string> Extensions => [".ast"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("STRM"u8.ToArray(), Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("pcm16be", "PCM16 big-endian planar"),
    new("afc", "Nintendo AFC ADPCM"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "AST GameCube/Wii audio; PCM16BE/AFC encode+decode with packet-preserving BLCK demux/mux/remux.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("Codec", "Codec", FormatOptionKind.Enum, "Pcm16Be", ["Pcm16Be", "Afc"],
      "AST codec: lossless planar PCM16 big-endian or Nintendo AFC ADPCM."),
    new("BlockSize", "Bytes per channel per BLCK", FormatOptionKind.Integer,
      AstWriter.BlockSize.ToString(CultureInfo.InvariantCulture),
      Description: "Positive byte count; PCM must be divisible by 2, AFC by 9. 0x2760 (10080) is conventional."),
    new("Loop", "Loop", FormatOptionKind.Boolean, "false", Description: "Set the AST loop flag."),
    new("LoopStart", "Loop start (samples)", FormatOptionKind.Integer, "0",
      Description: "AFC loop starts must be 16-sample aligned.", DependsOn: "Loop=true"),
    new("LoopEnd", "Loop end (samples)", FormatOptionKind.Integer, "0",
      Description: "Exclusive loop end; 0 means end of stream.", DependsOn: "Loop=true"),
    new("Volume", "Volume byte", FormatOptionKind.Integer, "127",
      Description: "Value stored at STRM offset 0x28; Nintendo files conventionally use 127."),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var files = FormatHelpers.FilesOnly(inputs).ToList();
    var full = files.FirstOrDefault(static file =>
      Path.GetFileName(file.Name).Equals("FULL.ast", StringComparison.OrdinalIgnoreCase));
    if (full.Data is not null) {
      output.Write(full.Data);
      return;
    }

    var metadataFile = files.FirstOrDefault(static file =>
      Path.GetFileName(file.Name).Equals("metadata.ini", StringComparison.OrdinalIgnoreCase));
    var metadata = metadataFile.Data is null ? EmptyMetadata() : ParseMetadata(metadataFile.Data);

    var channelWavs = files
      .Where(static file => Path.GetFileName(file.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static file => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(file.Name)))
      .Select(static file => new WavReader().ReadCanonicalPcm(file.Data))
      .ToArray();
    if (channelWavs.Length == 0)
      throw new InvalidOperationException("AST creation needs FULL.ast or one or more per-channel WAV files.");

    var first = channelWavs[0];
    if (channelWavs.Any(channel => channel.NumChannels != 1 || channel.FormatCode != 1 ||
                                   channel.BitsPerSample != 16 || channel.SampleRate != first.SampleRate ||
                                   channel.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All AST channel WAVs must be PCM16 mono with matching sample rate and frame count.");

    var pcmChannels = channelWavs.Select(ToShorts).ToArray();
    var writerOptions = ResolveWriterOptions(options, metadata, pcmChannels[0].Length);
    output.Write(new AstWriter().Write(pcmChannels, first.SampleRate, writerOptions));
  }

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "AST accepts FULL.ast, metadata.ini, or LEFT/RIGHT/CENTER/... mono PCM16 WAV channel files.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName);
    var directory = Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/') ?? "";
    if (directory.Length == 0 && (name.Equals("FULL.ast", StringComparison.OrdinalIgnoreCase) ||
                                  name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase) ||
                                  name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))) {
      reason = null;
      return true;
    }

    reason = $"not an AST input (got {input.ArchiveName}); {this.AcceptedInputsDescription}";
    return false;
  }

  public IReadOnlyList<string> SupportedEncodeCodecs => EncodeCodecs;

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);
    ArgumentNullException.ThrowIfNull(options);
    if (!TryParseCodec(codecId, out var codec)) {
      reason = $"AST does not support codec '{codecId}'";
      return false;
    }

    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = "AST encoding requires signed PCM16 input";
      return false;
    }

    if (format.SampleRate <= 0 || format.Channels is < 1 or > ushort.MaxValue) {
      reason = "AST requires a positive sample rate and 1..65535 channels";
      return false;
    }

    if (codec == AstCodec.Afc && format.Channels > 6) {
      reason = "AST AFC BLCK histories are defined for at most six channels";
      return false;
    }

    var blockSize = options.GetOptionInt("BlockSize", AstWriter.BlockSize);
    if (blockSize <= 0 || codec == AstCodec.Pcm16BigEndian && (blockSize & 1) != 0 ||
        codec == AstCodec.Afc && blockSize % Thp.AfcBytesPerFrame != 0) {
      reason = codec == AstCodec.Afc
        ? $"AFC block size must be a positive multiple of {Thp.AfcBytesPerFrame}"
        : "PCM16 block size must be a positive multiple of two";
      return false;
    }

    var volume = options.GetOptionInt("Volume", 127);
    if (volume is < byte.MinValue or > byte.MaxValue) {
      reason = "AST volume must fit in one byte";
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
      throw new InvalidDataException("PCM buffer ends in a partial sample frame.");

    var frameCount = checked((int)pcm.FrameCount);
    var channels = new short[pcm.Format.Channels][];
    for (var channel = 0; channel < channels.Length; ++channel)
      channels[channel] = new short[frameCount];

    for (var frame = 0; frame < frameCount; ++frame)
      for (var channel = 0; channel < channels.Length; ++channel) {
        var offset = checked((frame * channels.Length + channel) * sizeof(short));
        channels[channel][frame] = BinaryPrimitives.ReadInt16LittleEndian(pcm.InterleavedData.AsSpan(offset));
      }

    _ = TryParseCodec(codecId, out var codec);
    var writerOptions = ResolveWriterOptions(options, EmptyMetadata(), frameCount, codec);
    output.Write(new AstWriter().Write(channels, pcm.Format.SampleRate, writerOptions));
  }

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var parsed = new AstReader().Read(ReadAll(input));
    if (parsed.Pcm.Length == 0)
      throw new NotSupportedException($"AST codec {parsed.Info.Codec} cannot be decoded to PCM.");

    var channelBytes = parsed.Pcm.Select(ShortsToLe).ToList();
    return new AudioPcmBuffer(
      new AudioPcmFormat(parsed.Info.SampleRate, parsed.Info.NumChannels, 16),
      PcmCodec.Interleave(channelBytes, 16));
  }

  public IReadOnlyList<string> SupportedMuxCodecs => EncodeCodecs;

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!TryParseCodec(stream.CodecId, out var codec)) {
      reason = $"AST cannot mux codec '{stream.CodecId}'";
      return false;
    }

    if (stream.SampleRate <= 0 || stream.Channels is < 1 or > ushort.MaxValue) {
      reason = "AST requires a positive sample rate and 1..65535 channels";
      return false;
    }

    if (codec == AstCodec.Afc && stream.Channels > 6) {
      reason = "AST AFC BLCK histories are defined for at most six channels";
      return false;
    }

    if (stream.BitsPerSample is not (0 or 16)) {
      reason = "AST carries 16-bit PCM or AFC reconstructed to 16-bit samples";
      return false;
    }

    reason = null;
    return true;
  }

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    stream = null;
    AstReader.ParsedAst parsed;
    try {
      parsed = new AstReader().Read(ReadAll(input));
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException) {
      return false;
    }

    if (parsed.Info.Codec is not ((int)AstCodec.Afc or (int)AstCodec.Pcm16BigEndian))
      return false;

    var codec = parsed.Info.Codec == (int)AstCodec.Afc ? "afc" : "pcm16be";
    var packets = new List<AudioPacket>(parsed.Blocks.Count);
    long remaining = parsed.Info.SampleCount;
    foreach (var block in parsed.Blocks) {
      var payload = new byte[checked(block.SizePerChannel * parsed.Info.NumChannels)];
      for (var channel = 0; channel < parsed.Info.NumChannels; ++channel)
        block.Channels[channel].CopyTo(payload, channel * block.SizePerChannel);

      var capacity = parsed.Info.Codec == (int)AstCodec.Afc
        ? block.SizePerChannel / Thp.AfcBytesPerFrame * Thp.AfcSamplesPerFrame
        : block.SizePerChannel / sizeof(short);
      var duration = Math.Min(remaining, capacity);
      packets.Add(new AudioPacket(payload, duration));
      remaining -= duration;
    }

    var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      ["ast-packet-layout"] = "planar-block",
      ["ast-sample-count"] = parsed.Info.SampleCount.ToString(CultureInfo.InvariantCulture),
      ["ast-loop"] = parsed.Info.Loop ? "1" : "0",
      ["ast-loop-flag"] = parsed.Info.LoopFlag.ToString(CultureInfo.InvariantCulture),
      ["ast-loop-start"] = parsed.Info.LoopStart.ToString(CultureInfo.InvariantCulture),
      ["ast-loop-end"] = parsed.Info.LoopEnd.ToString(CultureInfo.InvariantCulture),
      ["ast-block-size"] = parsed.Info.FirstBlockSize.ToString(CultureInfo.InvariantCulture),
      ["ast-volume"] = parsed.Info.Volume.ToString(CultureInfo.InvariantCulture),
    };
    stream = new AudioEncodedStream(
      new AudioStreamFormat(codec, parsed.Info.SampleRate, parsed.Info.NumChannels, 16, properties),
      packets,
      BuildPrivateData(parsed));
    return true;
  }

  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanMux(stream.Format, options, out var reason))
      throw new NotSupportedException(reason);
    _ = TryParseCodec(stream.Format.CodecId, out var codec);

    var packets = stream.Packets.Where(static packet => !packet.IsHeader).ToArray();
    var sourceBlockSizes = new List<int>(packets.Length);
    var channelData = new List<byte>[stream.Format.Channels];
    for (var channel = 0; channel < channelData.Length; ++channel)
      channelData[channel] = [];

    foreach (var packet in packets) {
      if (packet.Data.Length % stream.Format.Channels != 0)
        throw new InvalidDataException("AST packet payload is not evenly planar across the declared channels.");
      var perChannelSize = packet.Data.Length / stream.Format.Channels;
      ValidateEncodedBlockSize(codec, perChannelSize);
      sourceBlockSizes.Add(perChannelSize);
      for (var channel = 0; channel < stream.Format.Channels; ++channel)
        channelData[channel].AddRange(packet.Data.AsSpan(channel * perChannelSize, perChannelSize).ToArray());
    }

    var encodedLength = channelData[0].Count;
    if (channelData.Any(data => data.Count != encodedLength))
      throw new InvalidDataException("AST mux channels do not have equal encoded lengths.");

    var capacity = codec == AstCodec.Afc
      ? (long)encodedLength / Thp.AfcBytesPerFrame * Thp.AfcSamplesPerFrame
      : (long)encodedLength / sizeof(short);
    var sampleCountProperty = PropertyInt(stream.Format, "ast-sample-count");
    var sampleCount = sampleCountProperty
      ?? (packets.Length > 0 && packets.All(static packet => packet.DurationSamples > 0)
        ? checked((int)packets.Sum(static packet => packet.DurationSamples))
        : checked((int)capacity));
    if (sampleCount < 0 || sampleCount > capacity)
      throw new InvalidDataException("AST sample count exceeds the encoded packet capacity.");

    var inheritedBlockSize = PropertyInt(stream.Format, "ast-block-size");
    var defaultBlockSize = inheritedBlockSize is > 0 ? inheritedBlockSize.Value : AstWriter.BlockSize;
    var targetBlockSize = options.GetOptionInt("BlockSize", defaultBlockSize);
    ValidateEncodedBlockSize(codec, targetBlockSize);

    var loop = options.HasOption("Loop")
      ? options.GetOptionBool("Loop", false)
      : PropertyBool(stream.Format, "ast-loop");
    var loopStart = options.TryGetInt("LoopStart", out var configuredLoopStart)
      ? configuredLoopStart
      : PropertyInt(stream.Format, "ast-loop-start") ?? 0;
    var loopEnd = options.TryGetInt("LoopEnd", out var configuredLoopEnd)
      ? configuredLoopEnd
      : PropertyInt(stream.Format, "ast-loop-end") ?? sampleCount;
    if (loopEnd == 0)
      loopEnd = sampleCount;
    ValidateLoop(codec, loop, loopStart, loopEnd, sampleCount);

    var volume = options.TryGetInt("Volume", out var configuredVolume)
      ? configuredVolume
      : PropertyInt(stream.Format, "ast-volume") ?? 127;
    if (volume is < byte.MinValue or > byte.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(options), "AST volume must fit in one byte.");

    var blockSizes = BuildTargetBlockSizes(encodedLength, targetBlockSize);
    var preserved = ParsePrivateData(stream.CodecPrivateData);
    var canReuseBlockHeaders = preserved.BlockHeaders.Length == blockSizes.Count &&
                               sourceBlockSizes.SequenceEqual(blockSizes);
    var decodedAfc = codec == AstCodec.Afc && !canReuseBlockHeaders
      ? channelData.Select(data => Thp.DecodeAfc(data.ToArray(), sampleCount)).ToArray()
      : null;

    using var memory = new MemoryStream();
    memory.Write(new byte[0x40]);
    var encodedOffset = 0;
    for (var blockIndex = 0; blockIndex < blockSizes.Count; ++blockIndex) {
      var blockSize = blockSizes[blockIndex];
      Span<byte> header = stackalloc byte[32];
      header.Clear();
      "BLCK"u8.CopyTo(header);
      BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)blockSize));
      if (canReuseBlockHeaders)
        preserved.BlockHeaders[blockIndex].CopyTo(header[8..]);
      else if (decodedAfc is not null)
        WriteAfcHistories(header[8..], decodedAfc, encodedOffset);
      memory.Write(header);

      for (var channel = 0; channel < stream.Format.Channels; ++channel)
        memory.Write(CollectionsMarshal.AsSpan(channelData[channel]).Slice(encodedOffset, blockSize));
      encodedOffset += blockSize;
    }

    var file = memory.ToArray();
    var fileHeader = file.AsSpan(0, 0x40);
    "STRM"u8.CopyTo(fileHeader);
    BinaryPrimitives.WriteUInt32BigEndian(fileHeader[4..], checked((uint)(file.Length - 0x40)));
    BinaryPrimitives.WriteUInt16BigEndian(fileHeader[8..], (ushort)codec);
    BinaryPrimitives.WriteUInt16BigEndian(fileHeader[10..], 16);
    BinaryPrimitives.WriteUInt16BigEndian(fileHeader[12..], checked((ushort)stream.Format.Channels));
    var inheritedLoopFlag = PropertyInt(stream.Format, "ast-loop-flag") ?? ushort.MaxValue;
    BinaryPrimitives.WriteUInt16BigEndian(fileHeader[14..], loop
      ? checked((ushort)Math.Clamp(inheritedLoopFlag, 1, ushort.MaxValue))
      : (ushort)0);
    BinaryPrimitives.WriteUInt32BigEndian(fileHeader[16..], checked((uint)stream.Format.SampleRate));
    BinaryPrimitives.WriteUInt32BigEndian(fileHeader[20..], checked((uint)sampleCount));
    BinaryPrimitives.WriteUInt32BigEndian(fileHeader[24..], checked((uint)(loop ? loopStart : 0)));
    BinaryPrimitives.WriteUInt32BigEndian(fileHeader[28..], checked((uint)(loop ? loopEnd : sampleCount)));
    BinaryPrimitives.WriteUInt32BigEndian(fileHeader[32..], checked((uint)(blockSizes.Count == 0 ? 0 : blockSizes[0])));
    preserved.ReservedHeader.CopyTo(fileHeader[0x24..]);
    fileHeader[0x28] = checked((byte)volume);
    output.Write(file);
  }

  private static AstWriterOptions ResolveWriterOptions(FormatCreateOptions options,
      IReadOnlyDictionary<string, string> metadata, int sampleCount, AstCodec? forcedCodec = null) {
    var codecText = options.Method ?? options.GetString("Codec") ?? Metadata(metadata, "codec") ?? "pcm16be";
    if (forcedCodec is not null)
      codecText = forcedCodec == AstCodec.Afc ? "afc" : "pcm16be";
    if (!TryParseCodec(codecText, out var codec))
      throw new NotSupportedException($"AST codec '{codecText}' is not supported.");

    var blockSize = options.TryGetInt("BlockSize", out var configuredBlockSize)
      ? configuredBlockSize
      : MetadataInt(metadata, "blockSize") ?? AstWriter.BlockSize;
    var loop = options.HasOption("Loop")
      ? options.GetOptionBool("Loop", false)
      : MetadataBool(metadata, "loop");
    var loopStart = options.TryGetInt("LoopStart", out var configuredLoopStart)
      ? configuredLoopStart
      : MetadataInt(metadata, "loopStart") ?? 0;
    var loopEnd = options.TryGetInt("LoopEnd", out var configuredLoopEnd)
      ? configuredLoopEnd
      : MetadataInt(metadata, "loopEnd") ?? sampleCount;
    var volume = options.TryGetInt("Volume", out var configuredVolume)
      ? configuredVolume
      : MetadataInt(metadata, "volume") ?? 127;
    if (volume is < byte.MinValue or > byte.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(options), "AST volume must fit in one byte.");

    return new AstWriterOptions(codec, blockSize, loop, loopStart, loopEnd == 0 ? null : loopEnd, checked((byte)volume));
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    var blob = ReadAll(stream);
    var entries = new List<AudioPseudoArchive.Entry> { new("FULL.ast", "Container", blob) };
    try {
      var parsed = new AstReader().Read(blob);
      if (parsed.Pcm.Length > 0) {
        var names = ChannelLayout.DefaultNames(parsed.Info.NumChannels);
        for (var channel = 0; channel < parsed.Info.NumChannels; ++channel) {
          var wav = PcmCodec.ToWavBlob(ShortsToLe(parsed.Pcm[channel]), channels: 1,
            parsed.Info.SampleRate, bitsPerSample: 16);
          entries.Add(new($"{names[channel]}.wav", "Channel", wav, "pcm"));
        }
      }
      entries.Add(new("metadata.ini", "Tag", BuildMetadata(parsed)));
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException) {
      // Invalid input still has a useful FULL.ast pseudo-entry.
    }
    return entries;
  }

  private static byte[] BuildMetadata(AstReader.ParsedAst parsed) {
    var info = parsed.Info;
    var codecName = info.Codec switch {
      (int)AstCodec.Afc => "AFC",
      (int)AstCodec.Pcm16BigEndian => "PCM16BE",
      _ => $"unknown({info.Codec})",
    };
    var blockSize = parsed.Blocks.Count > 0 ? parsed.Blocks[0].SizePerChannel : info.FirstBlockSize;
    var builder = new StringBuilder();
    builder.Append("[ast]\n");
    builder.Append(CultureInfo.InvariantCulture, $"sampleRate={info.SampleRate}\n");
    builder.Append(CultureInfo.InvariantCulture, $"channels={info.NumChannels}\n");
    builder.Append(CultureInfo.InvariantCulture, $"codec={codecName}\n");
    builder.Append(CultureInfo.InvariantCulture, $"sampleCount={info.SampleCount}\n");
    builder.Append(CultureInfo.InvariantCulture, $"blockSize={blockSize}\n");
    builder.Append(CultureInfo.InvariantCulture, $"volume={info.Volume}\n");
    builder.Append(CultureInfo.InvariantCulture, $"loop={(info.Loop ? 1 : 0)}\n");
    builder.Append(CultureInfo.InvariantCulture, $"loopStart={info.LoopStart}\n");
    builder.Append(CultureInfo.InvariantCulture, $"loopEnd={info.LoopEnd}\n");
    return Encoding.UTF8.GetBytes(builder.ToString());
  }

  private static Dictionary<string, string> ParseMetadata(ReadOnlySpan<byte> data) {
    var result = EmptyMetadata();
    var section = "";
    foreach (var rawLine in Encoding.UTF8.GetString(data).Split('\n')) {
      var line = rawLine.Trim();
      if (line.Length == 0 || line[0] is ';' or '#')
        continue;
      if (line.StartsWith('[', StringComparison.Ordinal) && line.EndsWith(']', StringComparison.Ordinal)) {
        section = line[1..^1].Trim();
        continue;
      }
      if (!section.Equals("ast", StringComparison.OrdinalIgnoreCase))
        continue;
      var equals = line.IndexOf('=');
      if (equals <= 0)
        continue;
      result[line[..equals].Trim()] = line[(equals + 1)..].Trim();
    }
    return result;
  }

  private static Dictionary<string, string> EmptyMetadata()
    => new(StringComparer.OrdinalIgnoreCase);

  private static string? Metadata(IReadOnlyDictionary<string, string> metadata, string key)
    => metadata.TryGetValue(key, out var value) ? value : null;

  private static int? MetadataInt(IReadOnlyDictionary<string, string> metadata, string key)
    => metadata.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer,
         CultureInfo.InvariantCulture, out var parsed)
      ? parsed
      : null;

  private static bool MetadataBool(IReadOnlyDictionary<string, string> metadata, string key)
    => metadata.TryGetValue(key, out var value) &&
       (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

  private static bool TryParseCodec(string codecId, out AstCodec codec) {
    switch (codecId.Trim().ToLowerInvariant()) {
      case "afc" or "adpcm-afc" or "afc-adpcm":
        codec = AstCodec.Afc;
        return true;
      case "pcm" or "pcm16" or "pcm16be" or "pcm-s16be-planar" or "pcms16be":
        codec = AstCodec.Pcm16BigEndian;
        return true;
      default:
        codec = default;
        return false;
    }
  }

  private static void ValidateEncodedBlockSize(AstCodec codec, int blockSize) {
    if (blockSize <= 0)
      throw new InvalidDataException("AST encoded block size must be positive.");
    if (codec == AstCodec.Afc && blockSize % Thp.AfcBytesPerFrame != 0)
      throw new InvalidDataException($"AFC AST block size must be divisible by {Thp.AfcBytesPerFrame}.");
    if (codec == AstCodec.Pcm16BigEndian && (blockSize & 1) != 0)
      throw new InvalidDataException("PCM16 AST block size must be divisible by two.");
  }

  private static void ValidateLoop(AstCodec codec, bool loop, int start, int end, int sampleCount) {
    if (!loop)
      return;
    if (sampleCount == 0 || start < 0 || start >= end || end > sampleCount)
      throw new InvalidDataException("AST loop points must satisfy 0 <= start < end <= sample count.");
    if (codec == AstCodec.Afc && start % Thp.AfcSamplesPerFrame != 0)
      throw new InvalidDataException($"AFC loop start must be aligned to {Thp.AfcSamplesPerFrame} samples.");
  }

  private static List<int> BuildTargetBlockSizes(int encodedLength, int blockSize) {
    var result = new List<int>();
    for (var offset = 0; offset < encodedLength; offset += blockSize)
      result.Add(Math.Min(blockSize, encodedLength - offset));
    return result;
  }

  private sealed record PreservedContainerData(byte[] ReservedHeader, byte[][] BlockHeaders);

  private static byte[] BuildPrivateData(AstReader.ParsedAst parsed) {
    var result = new byte[checked(PrivateHeaderBytes + parsed.Blocks.Count * 24)];
    "ASTP"u8.CopyTo(result);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), checked((uint)parsed.Blocks.Count));
    parsed.Info.ReservedHeader.AsSpan(0, Math.Min(28, parsed.Info.ReservedHeader.Length))
      .CopyTo(result.AsSpan(8, 28));
    for (var i = 0; i < parsed.Blocks.Count; ++i)
      parsed.Blocks[i].HeaderData.AsSpan(0, Math.Min(24, parsed.Blocks[i].HeaderData.Length))
        .CopyTo(result.AsSpan(PrivateHeaderBytes + i * 24, 24));
    return result;
  }

  private static PreservedContainerData ParsePrivateData(byte[]? data) {
    var reserved = new byte[28];
    reserved[4] = 0x7F;
    if (data is null || data.Length < PrivateHeaderBytes || !data.AsSpan(0, 4).SequenceEqual("ASTP"u8))
      return new PreservedContainerData(reserved, []);

    var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
    if (count > int.MaxValue || PrivateHeaderBytes + (long)count * 24 != data.Length)
      return new PreservedContainerData(reserved, []);

    data.AsSpan(8, 28).CopyTo(reserved);
    var headers = new byte[(int)count][];
    for (var i = 0; i < headers.Length; ++i)
      headers[i] = data.AsSpan(PrivateHeaderBytes + i * 24, 24).ToArray();
    return new PreservedContainerData(reserved, headers);
  }

  private static void WriteAfcHistories(Span<byte> destination, short[][] decoded, int encodedOffset) {
    var sampleOffset = checked(encodedOffset / Thp.AfcBytesPerFrame * Thp.AfcSamplesPerFrame);
    for (var channel = 0; channel < decoded.Length; ++channel) {
      var history1 = sampleOffset > 0 && sampleOffset <= decoded[channel].Length
        ? decoded[channel][sampleOffset - 1]
        : (short)0;
      var history2 = sampleOffset > 1 && sampleOffset <= decoded[channel].Length
        ? decoded[channel][sampleOffset - 2]
        : (short)0;
      BinaryPrimitives.WriteInt16BigEndian(destination[(channel * 4)..], history1);
      BinaryPrimitives.WriteInt16BigEndian(destination[(channel * 4 + 2)..], history2);
    }
  }

  private static int? PropertyInt(AudioStreamFormat format, string key)
    => format.Properties is { } properties && properties.TryGetValue(key, out var text) &&
       int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
      ? value
      : null;

  private static bool PropertyBool(AudioStreamFormat format, string key)
    => format.Properties is { } properties && properties.TryGetValue(key, out var value) &&
       (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

  private static short[] ToShorts(WavReader.ParsedWav wav) {
    var samples = new short[wav.InterleavedPcm.Length / sizeof(short)];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.InterleavedPcm.AsSpan(i * sizeof(short)));
    return samples;
  }

  private static byte[] ShortsToLe(short[] samples) {
    var result = new byte[checked(samples.Length * sizeof(short))];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * sizeof(short)), samples[i]);
    return result;
  }

  private static byte[] ReadAll(Stream input) {
    if (input.CanSeek)
      input.Position = 0;
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    return memory.ToArray();
  }
}
