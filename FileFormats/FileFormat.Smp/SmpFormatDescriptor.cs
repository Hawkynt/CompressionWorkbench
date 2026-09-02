#pragma warning disable CS1591
using System.Text;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Smp;

/// <summary>
/// Exposes a Turtle Beach SampleVision (.smp) file as an archive of <c>FULL.smp</c>
/// plus a single mono WAV (the signed 16-bit little-endian samples surface verbatim as
/// <c>MONO.wav</c>) and a <c>metadata.ini</c>. SampleVision is a mono-only format.
/// </summary>
public sealed class SmpFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Smp";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "SampleVision (Turtle Beach)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Audio;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".smp";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".smp"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SOUND SAMPLE DATA "u8.ToArray(), Confidence: 0.95),
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
  public string Description => "SampleVision (Turtle Beach .smp); full file + mono WAV.";

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

  // ── IArchiveCreatable: assemble a SampleVision file from a mono WAV ──

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.smp", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var channelBlobs = fileList
      .Where(f => {
        var name = Path.GetFileName(f.Name);
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("FULL.smp", StringComparison.OrdinalIgnoreCase);
      })
      .OrderBy(f => ChannelLayout.OrderIndex(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

    if (channelBlobs.Count != 1)
      throw new InvalidOperationException("SMP archive create needs FULL.smp or exactly one (mono) WAV.");

    var wav = new WavReader().Read(channelBlobs[0].Data);
    if (wav.NumChannels != 1)
      throw new InvalidOperationException("SampleVision is mono; supply a single-channel WAV.");
    if (wav.BitsPerSample != 16)
      throw new InvalidOperationException("SMP assembly accepts 16-bit mono WAVs.");

    var blob = new SmpWriter().Write(wav.InterleavedPcm, wav.SampleRate);
    output.Write(blob);
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
    "SMP archive accepts: FULL.smp, MONO .wav (mono only)";

  /// <summary>
  /// Performs the can accept operation.
  /// </summary>
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name == "full.smp" || name.EndsWith(".wav")) { reason = null; return true; }
    reason = $"not an SMP-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.smp", "Container", blob),
    };

    try {
      var parsed = new SmpReader().Read(blob);
      var rate = parsed.SampleRate > 0 ? parsed.SampleRate : 8000;

      if (parsed.SamplesLe.Length > 0)
        entries.Add(new("MONO.wav", "Channel", PcmCodec.ToWavBlob(parsed.SamplesLe, 1, rate, 16)));

      var info = new StringBuilder();
      if (!string.IsNullOrEmpty(parsed.Name)) info.AppendLine($"name={parsed.Name}");
      if (!string.IsNullOrEmpty(parsed.Comment)) info.AppendLine($"comment={parsed.Comment}");
      info.AppendLine($"version={parsed.Version}");
      info.AppendLine($"sample_rate={rate}");
      info.AppendLine($"channels=1");
      info.AppendLine($"bits=16");
      info.AppendLine($"midi_unity={parsed.MidiUnity}");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));
    } catch (InvalidDataException) {
      // Graceful FULL-only fallback for malformed SMP files.
    }

    return entries;
  }
}
