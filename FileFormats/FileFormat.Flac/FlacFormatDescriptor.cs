#pragma warning disable CS1591

using System.Buffers.Binary;
using Codec.Flac;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Flac;

/// <summary>
/// Native FLAC stream descriptor with archive, canonical PCM decode/encode and
/// channel-WAV creation surfaces.
/// </summary>
public sealed class FlacFormatDescriptor : IFormatDescriptor, IStreamFormatOperations,
  IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveLayoutMap, IArchiveCreatable,
  IArchiveWriteConstraints, IAudioContainerFormat, IAudioPcmSource, IAudioPcmTarget {

  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => FlacLayoutMap.Enumerate(archive);

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Flac";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "FLAC";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Stream;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.CanList | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".flac";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".flac", ".fla"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x66, 0x4C, 0x61, 0x43], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("flac", "FLAC")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Free Lossless Audio Codec; read/write plus decoded per-channel PCM.";

  // ── IStreamFormatOperations ──────────────────────────────────────────
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Decompress(Stream input, Stream output) => FlacReader.Decompress(input, output);
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public void Compress(Stream input, Stream output) => FlacWriter.Compress(input, output);

  // ── IArchiveFormatOperations ─────────────────────────────────────────

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: e.Name.Equals("FULL.flac", StringComparison.OrdinalIgnoreCase) ? "flac" : "pcm",
      IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files is { Length: > 0 } && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  // ── IArchiveInMemoryExtract ──────────────────────────────────────────

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (!e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) continue;
      output.Write(e.Data);
      return;
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "FLAC accepts FULL.flac or 1-8 mono integer-PCM WAV channels with matching geometry.";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName);
    if (name.Equals("FULL.flac", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
      reason = null;
      return true;
    }
    reason = $"not a FLAC input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var files = FormatHelpers.FilesOnly(inputs).ToList();
    var full = files.FirstOrDefault(static file =>
      Path.GetFileName(file.Name).Equals("FULL.flac", StringComparison.OrdinalIgnoreCase));
    if (full.Data is not null) {
      output.Write(full.Data);
      return;
    }

    var channels = files
      .Where(static file => file.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static file => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(file.Name)))
      .Select(static file => new WavReader().Read(file.Data))
      .ToArray();
    if (channels.Length is < 1 or > 8)
      throw new InvalidOperationException("FLAC creation requires 1-8 mono WAV channel inputs.");

    var first = channels[0];
    if (first.NumChannels != 1 || first.FormatCode != 1 || first.BitsPerSample is not (8 or 16 or 24 or 32))
      throw new InvalidOperationException("FLAC creation requires integer PCM WAV input at 8/16/24/32 bits.");
    if (channels.Any(channel => channel.NumChannels != 1 || channel.FormatCode != 1 ||
                                channel.BitsPerSample != first.BitsPerSample || channel.SampleRate != first.SampleRate ||
                                channel.InterleavedPcm.Length != first.InterleavedPcm.Length))
      throw new InvalidOperationException("All FLAC channel WAVs must have matching PCM geometry and frame count.");

    var interleaved = PcmCodec.Interleave(channels.Select(static channel => channel.InterleavedPcm).ToList(), first.BitsPerSample);
    var encoding = first.BitsPerSample == 8 ? AudioPcmEncoding.UnsignedInteger : AudioPcmEncoding.SignedInteger;
    this.EncodePcm(output,
      new AudioPcmBuffer(new AudioPcmFormat(first.SampleRate, channels.Length, first.BitsPerSample, encoding), interleaved),
      "flac", options);
  }

  public IReadOnlyList<string> SupportedEncodeCodecs => ["flac"];

  public bool CanEncode(AudioPcmFormat format, string codecId, FormatCreateOptions options, out string? reason) {
    if (!codecId.Equals("flac", StringComparison.OrdinalIgnoreCase)) {
      reason = $"codec '{codecId}' is not FLAC";
      return false;
    }
    if (format.Channels is < 1 or > 8 || format.SampleRate is < 1 or > 1_048_575) {
      reason = "FLAC requires 1-8 channels and a positive 20-bit sample rate";
      return false;
    }
    if (format.BitsPerSample is not (8 or 16 or 24 or 32) || format.Encoding == AudioPcmEncoding.IeeeFloat) {
      reason = "FLAC target currently accepts 8/16/24/32-bit integer PCM";
      return false;
    }
    if (format.BitsPerSample != 8 && format.Encoding != AudioPcmEncoding.SignedInteger) {
      reason = "multi-byte FLAC PCM must be signed integer";
      return false;
    }
    reason = null;
    return true;
  }

  public void EncodePcm(Stream output, AudioPcmBuffer pcm, string codecId, FormatCreateOptions options) {
    if (!this.CanEncode(pcm.Format, codecId, options, out var reason))
      throw new NotSupportedException(reason);
    var samples = DecodeIntegerPcm(pcm);
    var blockSize = options.TryGetInt("block-size", out var configuredBlockSize) ? configuredBlockSize : 4096;
    var compression = options.GetString("subframe")?.ToLowerInvariant() switch {
      "verbatim" => FlacSubframeMode.Verbatim,
      "fixed0" => FlacSubframeMode.Fixed0,
      "fixed1" => FlacSubframeMode.Fixed1,
      "fixed2" => FlacSubframeMode.Fixed2,
      "fixed3" => FlacSubframeMode.Fixed3,
      "fixed4" => FlacSubframeMode.Fixed4,
      _ => FlacSubframeMode.Auto,
    };
    var stereo = options.GetString("stereo-mode")?.ToLowerInvariant() switch {
      "independent" => FlacStereoMode.Independent,
      "left-side" => FlacStereoMode.LeftSide,
      "right-side" => FlacStereoMode.RightSide,
      "mid-side" or "midside" or "ms" => FlacStereoMode.MidSide,
      _ => FlacStereoMode.Auto,
    };
    output.Write(FlacCodec.Encode(samples, new FlacEncoderOptions(
      pcm.Format.SampleRate, pcm.Format.Channels, pcm.Format.BitsPerSample, blockSize, compression, stereo)));
  }

  public AudioPcmBuffer DecodePcm(Stream input) {
    var data = ReadAll(input);
    var props = FlacReader.ReadAudioProperties(data);
    using var source = new MemoryStream(data, writable: false);
    using var pcm = new MemoryStream();
    FlacReader.Decompress(source, pcm);
    return new AudioPcmBuffer(
      new AudioPcmFormat(props.SampleRate, props.Channels, props.BitsPerSample, AudioPcmEncoding.SignedInteger),
      pcm.ToArray());
  }

  private static int[] DecodeIntegerPcm(AudioPcmBuffer pcm) {
    var bytesPerSample = pcm.Format.BytesPerSample;
    if (pcm.InterleavedData.Length % bytesPerSample != 0)
      throw new InvalidDataException("PCM byte count is not aligned to its sample width.");
    var samples = new int[pcm.InterleavedData.Length / bytesPerSample];
    for (var i = 0; i < samples.Length; ++i) {
      var span = pcm.InterleavedData.AsSpan(i * bytesPerSample, bytesPerSample);
      samples[i] = pcm.Format.BitsPerSample switch {
        8 => pcm.Format.Encoding == AudioPcmEncoding.UnsignedInteger ? span[0] - 128 : (sbyte)span[0],
        16 => BinaryPrimitives.ReadInt16LittleEndian(span),
        24 => SignExtend24(span),
        32 => BinaryPrimitives.ReadInt32LittleEndian(span),
        _ => throw new NotSupportedException($"Unsupported FLAC PCM width {pcm.Format.BitsPerSample}."),
      };
    }
    return samples;
  }

  private static int SignExtend24(ReadOnlySpan<byte> bytes) {
    var value = bytes[0] | bytes[1] << 8 | bytes[2] << 16;
    return (value & 0x0080_0000) != 0 ? value | unchecked((int)0xFF00_0000) : value;
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    var blob = ReadAll(stream);
    var entries = new List<(string, string, byte[])> {
      ("FULL.flac", "Container", blob),
    };

    var props = FlacReader.ReadAudioProperties(blob);
    using var src = new MemoryStream(blob);
    using var pcm = new MemoryStream();
    FlacReader.Decompress(src, pcm);
    var pcmBytes = pcm.ToArray();

    if (props.Channels == 1) {
      entries.Add(("MONO.wav", "Channel",
        PcmCodec.ToWavBlob(pcmBytes, 1, props.SampleRate, props.BitsPerSample)));
    } else {
      foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(
          pcmBytes, props.Channels, props.SampleRate, props.BitsPerSample))
        entries.Add(($"{name}.wav", "Channel", wav));
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
