#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Codec.MonkeysAudio;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ape;

/// <summary>
/// Monkey's Audio (.ape) container with pseudo-archive inspection, canonical PCM
/// encode/decode, packet-preserving demux/remux, and WORM creation from per-channel WAVs.
/// Modern 3.98+ streams use the descriptor/header/seek-table model; legacy files remain
/// readable as structural pseudo-archives but are not rewritten because their bitstream/header
/// generations are not interchangeable with the checked-in v3.99 encoder.
/// </summary>
public sealed class ApeFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable, IAudioContainerFormat,
  IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource, IAudioMuxTarget, IFormatOptionsSchema {

  private static readonly string[] ApeCodecs = ["ape", "monkeys-audio"];
  private const int DefaultCompressionLevel = MonkeysAudioCodec.CompressionNormal;

  public string Id => "Ape";
  public string DisplayName => "Monkey's Audio (.ape)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ape";
  public IReadOnlyList<string> Extensions => [".ape", ".mac"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("MAC "u8.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored"), new("ape", "APE"), new("pcm", "PCM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Classic;
  public string Description =>
    "Monkey's Audio; modern APE encode/decode + frame demux/remux, WAV envelope and APEv2 tags.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new(
      Key: "CompressionLevel",
      DisplayName: "Compression level",
      Kind: FormatOptionKind.Enum,
      Default: "2000",
      AllowedValues: ["1000", "2000", "3000", "4000", "5000"],
      Description: "Monkey's Audio Fast, Normal, High, Extra High, or Insane predictor/filter cascade."),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((entry, index) => new ArchiveEntryInfo(
      Index: index,
      Name: entry.Name,
      OriginalSize: entry.Data.Length,
      CompressedSize: entry.Data.Length,
      Method: entry.Kind switch {
        "Channel" => "pcm",
        "Frames" or "Frame" => "ape",
        _ => "stored",
      },
      IsDirectory: false,
      IsEncrypted: false,
      LastModified: null,
      Kind: entry.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var entry in BuildEntries(stream)) {
      if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files))
        continue;
      WriteFile(outputDir, entry.Name, entry.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var entry in BuildEntries(input)) {
      if (!entry.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
        continue;
      output.Write(entry.Data);
      return;
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  // ── WORM creation ───────────────────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "APE accepts FULL.ape or one/two mono integer-PCM WAV channels (8/16/24-bit) with matching rate and length.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName);
    if (name.Equals("FULL.ape", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }
    reason = $"not a Monkey's Audio input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var files = FilesOnly(inputs).ToList();
    var full = files.FirstOrDefault(static file =>
      Path.GetFileName(file.Name).Equals("FULL.ape", StringComparison.OrdinalIgnoreCase));
    if (full.Data is not null) {
      output.Write(full.Data);
      return;
    }

    var channels = files
      .Where(static file => file.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static file => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(file.Name)))
      .Select(static file => new WavReader().ReadCanonicalPcm(file.Data))
      .ToArray();
    if (channels.Length is < 1 or > 2)
      throw new InvalidOperationException("Monkey's Audio creation requires one or two mono WAV channel inputs.");

    var first = channels[0];
    if (first.NumChannels != 1 || first.FormatCode != 1 || first.BitsPerSample is not (8 or 16 or 24))
      throw new InvalidOperationException("Monkey's Audio creation requires 8/16/24-bit integer PCM WAV channels.");
    if (channels.Any(channel => channel.NumChannels != 1 || channel.FormatCode != 1 ||
                                channel.BitsPerSample != first.BitsPerSample || channel.SampleRate != first.SampleRate ||
                                channel.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All Monkey's Audio channel WAVs must have matching PCM geometry and frame count.");

    var interleaved = PcmCodec.Interleave(
      channels.Select(static channel => channel.InterleavedPcm).ToList(), first.BitsPerSample);
    var encoding = first.BitsPerSample == 8 ? AudioPcmEncoding.UnsignedInteger : AudioPcmEncoding.SignedInteger;
    var codec = options.Method ?? options.GetString("codec") ?? "ape";
    this.EncodePcm(output,
      new AudioPcmBuffer(new AudioPcmFormat(first.SampleRate, channels.Length, first.BitsPerSample, encoding), interleaved),
      codec,
      options);
  }

  // ── Canonical PCM ───────────────────────────────────────────────────────────

  public IReadOnlyList<string> SupportedEncodeCodecs => ApeCodecs;

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(format);
    ArgumentNullException.ThrowIfNull(options);
    if (!ApeCodecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"codec '{codecId}' is not Monkey's Audio";
      return false;
    }
    if (format.Channels is < 1 or > 2 || format.SampleRate < 1) {
      reason = "Monkey's Audio encoder supports one or two channels and a positive sample rate";
      return false;
    }
    if (format.BitsPerSample is not (8 or 16 or 24) || format.Encoding == AudioPcmEncoding.IeeeFloat) {
      reason = "Monkey's Audio encoder supports 8/16/24-bit integer PCM";
      return false;
    }
    if (format.BitsPerSample != 8 && format.Encoding != AudioPcmEncoding.SignedInteger) {
      reason = "16/24-bit Monkey's Audio PCM must be signed little-endian integer";
      return false;
    }
    if (!TryGetCompressionLevel(options, out _, out reason))
      return false;
    reason = null;
    return true;
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(pcm);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);
    _ = TryGetCompressionLevel(options, out var compressionLevel, out _);

    var data = pcm.InterleavedData;
    if (pcm.Format.BitsPerSample == 8 && pcm.Format.Encoding == AudioPcmEncoding.SignedInteger) {
      data = (byte[])data.Clone();
      for (var index = 0; index < data.Length; ++index)
        data[index] ^= 0x80;
    }

    using var source = new MemoryStream(data, writable: false);
    MonkeysAudioCodec.Compress(
      source, output, pcm.Format.Channels, pcm.Format.SampleRate, pcm.Format.BitsPerSample, compressionLevel);
  }

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var file = ReadAll(input);
    var codecBytes = ApeContainer.TryParseModern(file, out var parsed) && parsed is not null
      ? parsed.CodecPayload
      : file;

    using var probe = new MemoryStream(codecBytes, writable: false);
    var info = MonkeysAudioCodec.ReadStreamInfo(probe);
    using var source = new MemoryStream(codecBytes, writable: false);
    using var pcm = new MemoryStream();
    MonkeysAudioCodec.Decompress(source, pcm);
    return new AudioPcmBuffer(
      new AudioPcmFormat(
        info.SampleRate,
        info.Channels,
        info.BitsPerSample,
        info.BitsPerSample == 8 ? AudioPcmEncoding.UnsignedInteger : AudioPcmEncoding.SignedInteger),
      pcm.ToArray());
  }

  // ── Encoded frame demux / mux ───────────────────────────────────────────────

  public IReadOnlyList<string> SupportedMuxCodecs => ApeCodecs;

  public bool CanMux(AudioStreamFormat stream, FormatCreateOptions options, out string? reason) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!ApeCodecs.Contains(stream.CodecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"Monkey's Audio cannot mux codec '{stream.CodecId}'";
      return false;
    }
    if (stream.Channels is < 1 or > 2 || stream.SampleRate < 1 || stream.BitsPerSample is not (8 or 16 or 24)) {
      reason = "Monkey's Audio muxing requires 1-2 channels and 8/16/24-bit PCM geometry";
      return false;
    }

    var version = PropertyInt(stream, "version") ?? 3990;
    if (version is < ApeContainer.MinModernVersion or > ApeContainer.MaxKnownVersion) {
      reason = $"packet-preserving APE muxing supports the modern 3.98-3.99 container, got {version}";
      return false;
    }
    var compressionLevel = PropertyInt(stream, "compression-level") ?? DefaultCompressionLevel;
    if (!IsCompressionLevel(compressionLevel)) {
      reason = $"unsupported Monkey's Audio compression level {compressionLevel}";
      return false;
    }
    reason = null;
    return true;
  }

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    var file = ReadAll(input);
    if (!ApeContainer.TryParseModern(file, out var parsed) || parsed is null || parsed.Frames.Count == 0) {
      stream = null;
      return false;
    }

    var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      ["version"] = parsed.Version.ToString(CultureInfo.InvariantCulture),
      ["compression-level"] = parsed.CompressionLevel.ToString(CultureInfo.InvariantCulture),
      ["format-flags"] = parsed.FormatFlags.ToString(CultureInfo.InvariantCulture),
      ["blocks-per-frame"] = parsed.BlocksPerFrame.ToString(CultureInfo.InvariantCulture),
    };
    stream = new AudioEncodedStream(
      new AudioStreamFormat(
        "ape", checked((int)parsed.SampleRate), parsed.Channels, parsed.BitsPerSample, properties),
      parsed.Frames.Select(static frame => new AudioPacket(frame.Data, frame.DurationSamples)).ToArray(),
      file);
    return true;
  }

  public void Mux(Stream output, AudioEncodedStream stream, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(options);
    if (!this.CanMux(stream.Format, options, out var reason))
      throw new NotSupportedException(reason);

    ApeContainer.Parsed? original = null;
    if (stream.CodecPrivateData is { Length: > 0 } privateData)
      ApeContainer.TryParseModern(privateData, out original);

    if (original is not null && IsUnchanged(stream, original)) {
      output.Write(original.Original);
      return;
    }

    var version = PropertyInt(stream.Format, "version") ?? original?.Version ?? 3990;
    var compressionLevel = PropertyInt(stream.Format, "compression-level") ?? original?.CompressionLevel ?? DefaultCompressionLevel;
    var formatFlags = checked((ushort)(PropertyInt(stream.Format, "format-flags") ?? original?.FormatFlags ?? 0));
    var blocksPerFrameValue = PropertyInt(stream.Format, "blocks-per-frame") ?? checked((int)(original?.BlocksPerFrame ?? 0));
    if (blocksPerFrameValue < 0)
      throw new InvalidDataException("Monkey's Audio blocks-per-frame cannot be negative.");

    var packets = stream.Packets.Where(static packet => !packet.IsHeader).ToArray();
    var totalSamples = packets.Aggregate(0L, static (sum, packet) => checked(sum + packet.DurationSamples));
    var preserveWaveEnvelope = original is not null &&
      original.SampleRate == stream.Format.SampleRate &&
      original.Channels == stream.Format.Channels &&
      original.BitsPerSample == stream.Format.BitsPerSample &&
      original.TotalSamples == totalSamples;

    ApeContainer.WriteModern(
      output,
      version,
      compressionLevel,
      formatFlags,
      checked((uint)blocksPerFrameValue),
      stream.Format.BitsPerSample,
      stream.Format.Channels,
      stream.Format.SampleRate,
      stream.Packets,
      original?.LeadingData ?? [],
      preserveWaveEnvelope ? original!.HeaderData : [],
      preserveWaveEnvelope ? original!.TerminatingData : [],
      original?.TrailingData ?? []);
  }

  private static bool IsUnchanged(AudioEncodedStream stream, ApeContainer.Parsed original) {
    if (stream.Format.SampleRate != original.SampleRate ||
        stream.Format.Channels != original.Channels ||
        stream.Format.BitsPerSample != original.BitsPerSample ||
        (PropertyInt(stream.Format, "version") ?? original.Version) != original.Version ||
        (PropertyInt(stream.Format, "compression-level") ?? original.CompressionLevel) != original.CompressionLevel ||
        (PropertyInt(stream.Format, "format-flags") ?? original.FormatFlags) != original.FormatFlags ||
        (PropertyInt(stream.Format, "blocks-per-frame") ?? checked((int)original.BlocksPerFrame)) != original.BlocksPerFrame)
      return false;

    var packets = stream.Packets.Where(static packet => !packet.IsHeader).ToArray();
    if (packets.Length != original.Frames.Count || stream.Packets.Any(static packet => packet.IsHeader))
      return false;
    for (var index = 0; index < packets.Length; ++index)
      if (packets[index].DurationSamples != original.Frames[index].DurationSamples ||
          !packets[index].Data.AsSpan().SequenceEqual(original.Frames[index].Data))
        return false;
    return true;
  }

  // ── Pseudo-archive view ─────────────────────────────────────────────────────

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    var file = ReadAll(stream);
    var entries = new List<(string, string, byte[])> { ("FULL.ape", "Container", file) };

    if (ApeContainer.TryParseModern(file, out var modern) && modern is not null) {
      AddModernEntries(modern, entries);
      AddChannelEntries(file, entries);
      return entries;
    }

    var macOffset = FindAnyMacOffset(file);
    if (macOffset < 0 || file.Length - macOffset < 6)
      return entries;
    var version = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(macOffset + 4));
    if (version < ApeContainer.MinModernVersion)
      ParseLegacy(file, macOffset, version, entries);
    AddChannelEntries(file, entries);
    return entries;
  }

  private static void AddModernEntries(
      ApeContainer.Parsed parsed,
      List<(string Name, string Kind, byte[] Data)> entries) {
    var metadata = new StringBuilder();
    metadata.AppendLine("[ape]");
    metadata.Append("version=").AppendLine(parsed.Version.ToString(CultureInfo.InvariantCulture));
    metadata.Append("compression_level=").AppendLine(parsed.CompressionLevel.ToString(CultureInfo.InvariantCulture));
    metadata.Append("format_flags=0x").AppendLine(parsed.FormatFlags.ToString("X4", CultureInfo.InvariantCulture));
    metadata.Append("sample_rate=").AppendLine(parsed.SampleRate.ToString(CultureInfo.InvariantCulture));
    metadata.Append("channels=").AppendLine(parsed.Channels.ToString(CultureInfo.InvariantCulture));
    metadata.Append("bits_per_sample=").AppendLine(parsed.BitsPerSample.ToString(CultureInfo.InvariantCulture));
    metadata.Append("total_frames=").AppendLine(parsed.TotalFrames.ToString(CultureInfo.InvariantCulture));
    metadata.Append("total_samples=").AppendLine(parsed.TotalSamples.ToString(CultureInfo.InvariantCulture));
    metadata.Append("audio_data_bytes=").AppendLine(parsed.AudioDataLength.ToString(CultureInfo.InvariantCulture));
    metadata.Append("leading_bytes=").AppendLine(parsed.MacOffset.ToString(CultureInfo.InvariantCulture));
    entries.Add(("metadata.ini", "Metadata", Encoding.UTF8.GetBytes(metadata.ToString())));

    AddEntry(entries, "leading.bin", "Metadata", parsed.LeadingData);
    AddEntry(entries, "wav_header.bin", "WavHeader", parsed.HeaderData);
    AddEntry(entries, "seek_table.bin", "SeekTable", parsed.SeekTableData);
    AddEntry(entries, "frames.bin", "Frames", parsed.AudioData);
    AddEntry(entries, "terminating.bin", "Terminating", parsed.TerminatingData);
    AddEntry(entries, "trailing.bin", "Metadata", parsed.TrailingData);
    for (var index = 0; index < parsed.Frames.Count; ++index)
      AddEntry(entries, $"frames/frame_{index:D4}.bin", "Frame", parsed.Frames[index].Data);

    // APEv2 is outside APE_DESCRIPTOR.terminatingDataBytes; that field is the preserved WAV tail.
    // Search the actual trailing region first. The terminating range is retained as a compatibility
    // fallback for malformed historical writers that incorrectly counted the tag as WAV tail.
    if (ApeTagReader.TryFind(parsed.Original, parsed.TailEnd, parsed.Original.Length, out var tag) ||
        ApeTagReader.TryFind(parsed.Original, parsed.FrameDataEnd, parsed.TailEnd, out tag)) {
      var ini = ApeTagReader.TryRenderIni(parsed.Original, tag);
      if (ini is not null)
        entries.Add(("tags.ini", "Tag", Encoding.UTF8.GetBytes(ini)));
    }
  }

  private static void AddEntry(
      List<(string Name, string Kind, byte[] Data)> entries,
      string name,
      string kind,
      byte[] data) {
    if (data.Length > 0)
      entries.Add((name, kind, data));
  }

  private static void AddChannelEntries(byte[] file, List<(string Name, string Kind, byte[] Data)> entries) {
    try {
      using var source = new MemoryStream(file, writable: false);
      var pcm = new ApeFormatDescriptor().DecodePcm(source);
      if (pcm.Format.Channels <= 1) {
        entries.Add(("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcm.InterleavedData, 1, pcm.Format.SampleRate, pcm.Format.BitsPerSample, formatCode: 1)));
        return;
      }
      foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
          pcm.InterleavedData, pcm.Format.Channels, pcm.Format.SampleRate, pcm.Format.BitsPerSample))
        entries.Add(($"{name}.wav", "Channel", wav));
    } catch (Exception) {
      // Structural inspection remains available even when the bitstream generation is unsupported/corrupt.
    }
  }

  private static void ParseLegacy(
      byte[] file,
      int macOffset,
      ushort version,
      List<(string Name, string Kind, byte[] Data)> entries) {
    const int LegacyFields = 26;
    var start = macOffset + 6;
    if (start < 0 || file.Length - start < LegacyFields)
      return;

    var compressionLevel = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(start));
    var formatFlags = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(start + 2));
    var channels = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(start + 4));
    var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(start + 6));
    var headerBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(start + 10));
    var terminatingBytes = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(start + 14));
    var totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(start + 18));
    var finalFrameBlocks = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(start + 22));
    var bitsPerSample = (formatFlags & 0x01) != 0 ? 8 : (formatFlags & 0x08) != 0 ? 24 : 16;

    var metadata = new StringBuilder();
    metadata.AppendLine("[ape]");
    metadata.Append("version=").AppendLine(version.ToString(CultureInfo.InvariantCulture));
    metadata.Append("compression_level=").AppendLine(compressionLevel.ToString(CultureInfo.InvariantCulture));
    metadata.Append("format_flags=0x").AppendLine(formatFlags.ToString("X4", CultureInfo.InvariantCulture));
    metadata.Append("sample_rate=").AppendLine(sampleRate.ToString(CultureInfo.InvariantCulture));
    metadata.Append("channels=").AppendLine(channels.ToString(CultureInfo.InvariantCulture));
    metadata.Append("bits_per_sample=").AppendLine(bitsPerSample.ToString(CultureInfo.InvariantCulture));
    metadata.Append("total_frames=").AppendLine(totalFrames.ToString(CultureInfo.InvariantCulture));
    metadata.Append("final_frame_blocks=").AppendLine(finalFrameBlocks.ToString(CultureInfo.InvariantCulture));
    metadata.Append("terminating_bytes=").AppendLine(terminatingBytes.ToString(CultureInfo.InvariantCulture));
    metadata.Append("header_bytes=").AppendLine(headerBytes.ToString(CultureInfo.InvariantCulture));
    metadata.Append("leading_bytes=").AppendLine(macOffset.ToString(CultureInfo.InvariantCulture));
    entries.Add(("metadata.ini", "Metadata", Encoding.UTF8.GetBytes(metadata.ToString())));

    var bodyStart = start + LegacyFields;
    if (bodyStart < file.Length)
      entries.Add(("frames.bin", "Frames", file.AsSpan(bodyStart).ToArray()));
  }

  private static int FindAnyMacOffset(ReadOnlySpan<byte> file) {
    for (var offset = 0; offset + 6 <= file.Length; ++offset)
      if (file.Slice(offset, 4).SequenceEqual("MAC "u8))
        return offset;
    return -1;
  }

  private static bool TryGetCompressionLevel(FormatCreateOptions options, out int level, out string? reason) {
    var raw = options.GetString("CompressionLevel") ?? options.GetString("compression-level");
    if (raw is null)
      level = options.Level ?? DefaultCompressionLevel;
    else if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out level)) {
      reason = $"invalid Monkey's Audio compression level '{raw}'";
      return false;
    }
    if (level is >= 1 and <= 5)
      level *= 1000;
    if (!IsCompressionLevel(level)) {
      reason = "Monkey's Audio compression level must be 1000, 2000, 3000, 4000, or 5000";
      return false;
    }
    reason = null;
    return true;
  }

  private static bool IsCompressionLevel(int level)
    => level is MonkeysAudioCodec.CompressionFast
      or MonkeysAudioCodec.CompressionNormal
      or MonkeysAudioCodec.CompressionHigh
      or MonkeysAudioCodec.CompressionExtraHigh
      or MonkeysAudioCodec.CompressionInsane;

  private static int? PropertyInt(AudioStreamFormat format, string key)
    => format.Properties is { } properties &&
       properties.TryGetValue(key, out var raw) &&
       int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
      ? value
      : null;

  private static byte[] ReadAll(Stream input) {
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    return memory.ToArray();
  }
}
