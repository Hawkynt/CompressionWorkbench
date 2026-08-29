#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Aac;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Aac;

/// <summary>
/// AAC-LC in ADTS framing. Besides the pseudo-archive view this descriptor exposes
/// canonical PCM encode/decode and raw AAC access units for packet-preserving remux.
/// </summary>
public sealed class AacFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable,
  IAudioContainerFormat, IAudioPcmSource, IAudioPcmTarget, IAudioDemuxSource {

  private static readonly string[] EncodeCodecs = ["aac", "aac-lc"];

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Aac";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "AAC (ADTS)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".aac";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".aac"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // ADTS sync is a 12-bit 0xFFF word; the most common variant is MPEG-4, no CRC
  // (0xFFF1). Moderate confidence keeps false positives low for arbitrary streams.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xFF, 0xF1], Confidence: 0.40),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("aac", "AAC-LC / ADTS")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "AAC-LC/ADTS audio; decode, encode, access-unit demux and per-channel PCM.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    AudioPseudoArchive.List(BuildEntries(stream));

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "AAC accepts FULL.aac or one/two mono PCM16 WAV channel files with matching sample rate and length.";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.aac" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not an AAC input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var files = FormatHelpers.FilesOnly(inputs).ToList();
    var full = files.FirstOrDefault(static file =>
      Path.GetFileName(file.Name).Equals("FULL.aac", StringComparison.OrdinalIgnoreCase));
    if (full.Data is not null) {
      output.Write(full.Data);
      return;
    }

    var channels = files
      .Where(static file => file.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static file => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(file.Name)))
      .Select(static file => new WavReader().Read(file.Data))
      .ToArray();
    if (channels.Length is < 1 or > 2)
      throw new InvalidOperationException("AAC-LC creation requires one or two mono WAV channels.");

    var first = channels[0];
    if (first.NumChannels != 1 || first.FormatCode != 1 || first.BitsPerSample != 16)
      throw new InvalidOperationException("AAC-LC creation requires PCM16 mono WAV inputs.");
    if (channels.Any(channel => channel.NumChannels != 1 || channel.FormatCode != 1 ||
                                channel.BitsPerSample != 16 || channel.SampleRate != first.SampleRate ||
                                channel.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All AAC channel WAVs must be PCM16 mono with matching sample rate and frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(static channel => channel.InterleavedPcm).ToList(), 16);
    var pcm = new AudioPcmBuffer(
      new AudioPcmFormat(first.SampleRate, channels.Length, 16),
      interleaved);
    var codec = options.Method ?? options.GetString("codec") ?? "aac";
    this.EncodePcm(output, pcm, codec, options);
  }

  public IReadOnlyList<string> SupportedEncodeCodecs => EncodeCodecs;

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    if (!EncodeCodecs.Contains(codecId, StringComparer.OrdinalIgnoreCase)) {
      reason = $"codec '{codecId}' is not AAC-LC";
      return false;
    }
    if (format.Encoding != AudioPcmEncoding.SignedInteger || format.BitsPerSample != 16) {
      reason = "AAC-LC encoder input must be signed PCM16";
      return false;
    }
    if (format.Channels is < 1 or > 2) {
      reason = "AAC-LC encoder supports mono or stereo";
      return false;
    }
    if (Array.IndexOf(AacAdtsReader.SampleRateTable, format.SampleRate) is < 0 or > 12) {
      reason = "sample rate is not an ADTS standard rate";
      return false;
    }
    reason = null;
    return true;
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);

    var samples = new short[pcm.InterleavedData.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.InterleavedData.AsSpan(i * 2, 2));

    var bitrate = options.TryGetInt("bitrate", out var configuredBitrate)
      ? configuredBitrate
      : pcm.Format.Channels == 1 ? 64_000 : 128_000;
    var cutoff = options.TryGetInt("cutoff", out var configuredCutoff) ? configuredCutoff : 0;
    var window = options.GetString("window")?.ToLowerInvariant() switch {
      "kbd" => AacEncoderWindowShape.Kbd,
      _ => AacEncoderWindowShape.Sine,
    };
    var stereoMode = options.GetString("stereo-mode")?.ToLowerInvariant() switch {
      "independent" => AacStereoCodingMode.Independent,
      "ms" or "mid-side" or "midside" => AacStereoCodingMode.MidSide,
      _ => AacStereoCodingMode.Auto,
    };
    var pad = options.GetString("pad-final-frame") is { } padText
      ? bool.Parse(padText)
      : true;

    var encoded = AacEncoder.Encode(samples, new AacEncoderOptions(
      pcm.Format.SampleRate, pcm.Format.Channels, bitrate, cutoff, window, stereoMode, pad));
    output.Write(encoded);
  }

  public AudioPcmBuffer DecodePcm(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var data = ReadAll(input);
    using var probe = new MemoryStream(data, writable: false);
    var info = AacCodec.ReadStreamInfo(probe);
    using var rateProbe = new MemoryStream(data, writable: false);
    var sampleRate = AacCodec.ReadCoreSampleRate(rateProbe);
    using var source = new MemoryStream(data, writable: false);
    using var pcm = new MemoryStream();
    AacCodec.Decompress(source, pcm);
    return new AudioPcmBuffer(
      new AudioPcmFormat(sampleRate, info.Channels, 16),
      pcm.ToArray());
  }

  public bool TryDemux(Stream input, out AudioEncodedStream? stream) {
    ArgumentNullException.ThrowIfNull(input);
    var data = ReadAll(input);
    var packets = new List<AudioPacket>();
    AdtsHeader? first = null;
    var offset = 0;
    try {
      while (offset + AacAdtsReader.ShortHeaderLength <= data.Length) {
        var header = AacAdtsReader.ParseHeader(data, offset);
        if (header.FrameLength < header.HeaderLengthBytes || offset + header.FrameLength > data.Length)
          throw new InvalidDataException("ADTS frame overruns input.");
        first ??= header;
        var payloadLength = header.FrameLength - header.HeaderLengthBytes;
        packets.Add(new AudioPacket(
          data.AsSpan(offset + header.HeaderLengthBytes, payloadLength).ToArray(),
          DurationSamples: (header.NumberOfRawDataBlocks + 1L) * AacEncoder.FrameSamples));
        offset += header.FrameLength;
      }
    } catch (InvalidDataException) {
      stream = null;
      return false;
    }

    if (first is not { } initial || packets.Count == 0 || offset != data.Length) {
      stream = null;
      return false;
    }

    var objectType = initial.Profile + 1;
    var asc = new byte[2];
    asc[0] = (byte)((objectType << 3) | (initial.SampleRateIndex >> 1));
    asc[1] = (byte)(((initial.SampleRateIndex & 1) << 7) | (initial.ChannelConfiguration << 3));
    stream = new AudioEncodedStream(
      new AudioStreamFormat(
        "aac",
        initial.SampleRate,
        initial.ChannelConfiguration,
        Properties: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
          ["object-type"] = objectType.ToString(System.Globalization.CultureInfo.InvariantCulture),
          ["sample-rate-index"] = initial.SampleRateIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }),
      packets,
      asc);
    return true;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    var blob = ReadAll(stream);
    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.aac", "Container", blob, "aac"),
    };

    try {
      var descriptor = new AacFormatDescriptor();
      using var source = new MemoryStream(blob, writable: false);
      var pcm = descriptor.DecodePcm(source);
      if (pcm.Format.Channels <= 1) {
        entries.Add(new("MONO.wav", "Channel",
          PcmCodec.ToWavBlob(pcm.InterleavedData, 1, pcm.Format.SampleRate, 16, formatCode: 1), "pcm"));
      } else {
        foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
            pcm.InterleavedData, pcm.Format.Channels, pcm.Format.SampleRate, 16))
          entries.Add(new($"{name}.wav", "Channel", wav, "pcm"));
      }
    } catch (Exception) {
      // Graceful archive-view fallback for unsupported/malformed AAC.
    }

    return entries;
  }

  private static byte[] ReadAll(Stream input) {
    if (input.CanSeek) input.Position = 0;
    using var memory = new MemoryStream();
    input.CopyTo(memory);
    return memory.ToArray();
  }
}
