#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Hps;

/// <summary>
/// Exposes a GameCube <c>.hps</c> (HAL HALPST stream) as an archive of <c>FULL.hps</c> plus one
/// decoded mono WAV per channel (named per <see cref="ChannelLayout"/>) plus a <c>metadata.ini</c>
/// describing sample rate, channels and sample count. DSP-ADPCM is decoded with continuous per-channel
/// history across blocks; anything the reader cannot parse falls back gracefully to <c>FULL.hps</c>
/// only.
/// </summary>
public sealed class HpsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Hps";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "HPS (GameCube HALPST stream)";
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
  public string DefaultExtension => ".hps";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".hps"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(" HALPST\0"u8.ToArray(), Confidence: 0.90),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
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
  public string Description => "HPS (GameCube HALPST stream); full file + per-channel decoded WAVs + metadata.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveCreatable: FULL passthrough OR per-channel mono WAVs → DSP-ADPCM HPS ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.hps", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("HPS archive create needs either FULL.hps or one or more per-channel WAVs.");

    var channels = new List<WavReader.ParsedWav>();
    foreach (var (_, data) in channelBlobs) channels.Add(new WavReader().ReadCanonicalPcm(data));

    var first = channels[0];
    if (channels.Any(c => c.NumChannels != 1 || c.SampleRate != first.SampleRate || c.BitsPerSample != 16))
      throw new InvalidOperationException("All channel WAVs must be mono 16-bit and share the same sample rate.");

    var pcmChannels = channels.Select(ToShorts).ToList();
    var sampleCount = pcmChannels[0].Length;
    if (pcmChannels.Any(c => c.Length != sampleCount))
      throw new InvalidOperationException("All channel WAVs must have the same sample count.");

    output.Write(new HpsWriter().Write(pcmChannels, first.SampleRate));
  }

  private static short[] ToShorts(WavReader.ParsedWav wav) {
    var pcm = wav.InterleavedPcm;
    var samples = new short[pcm.Length / 2];
    for (var i = 0; i < samples.Length; ++i)
      samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
    return samples;
  }

  // ── IArchiveWriteConstraints ──────────────────────────────────────────────

  /// <summary>
  /// Gets the max total archive size.
  /// </summary>
  public long? MaxTotalArchiveSize => null;
  /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
  public string AcceptedInputsDescription =>
    "HPS archive accepts: FULL.hps, or LEFT/RIGHT/CENTER/… .wav (per-channel mono 16-bit)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    var dir = Path.GetDirectoryName(input.ArchiveName)?.Replace('\\', '/').ToLowerInvariant() ?? "";
    if (dir == "" && (name == "full.hps" || name.EndsWith(".wav"))) { reason = null; return true; }
    reason = $"not an HPS-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.hps", "Container", blob),
    };

    try {
      var parsed = new HpsReader().Read(blob);
      var names = ChannelLayout.DefaultNames(parsed.Info.NumChannels);
      for (var c = 0; c < parsed.Info.NumChannels; ++c) {
        var pcm = ShortsToLe(parsed.Pcm[c]);
        var wav = PcmCodec.ToWavBlob(pcm, channels: 1, parsed.Info.SampleRate, bitsPerSample: 16);
        entries.Add(new($"{names[c]}.wav", "Channel", wav, "pcm"));
      }
      entries.Add(new("metadata.ini", "Tag", BuildMetadata(parsed.Info)));
    } catch (Exception ex) when (ex is InvalidDataException or ArgumentException
                                   or IndexOutOfRangeException or ArgumentOutOfRangeException) {
      // Unparseable HPS: surface FULL only.
    }

    return entries;
  }

  private static byte[] BuildMetadata(HpsReader.Header info) {
    var sb = new StringBuilder();
    sb.Append("[hps]\n");
    sb.Append(CultureInfo.InvariantCulture, $"sampleRate={info.SampleRate}\n");
    sb.Append(CultureInfo.InvariantCulture, $"channels={info.NumChannels}\n");
    sb.Append("codec=DSP-ADPCM\n");
    sb.Append(CultureInfo.InvariantCulture, $"sampleCount={info.SampleCount}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] ShortsToLe(short[] samples) {
    var pcm = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), samples[i]);
    return pcm;
  }
}
