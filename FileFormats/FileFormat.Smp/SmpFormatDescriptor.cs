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

  public string Id => "Smp";
  public string DisplayName => "SampleVision (Turtle Beach)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".smp";
  public IReadOnlyList<string> Extensions => [".smp"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("SOUND SAMPLE DATA "u8.ToArray(), Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "SampleVision (Turtle Beach .smp); full file + mono WAV.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password)
    => AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveCreatable: assemble a SampleVision file from a mono WAV ──

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

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "SMP archive accepts: FULL.smp, MONO .wav (mono only)";

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
