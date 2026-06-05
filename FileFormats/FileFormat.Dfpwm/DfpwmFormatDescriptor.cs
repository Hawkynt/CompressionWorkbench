#pragma warning disable CS1591
using System.Text;
using Codec.Dfpwm;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace FileFormat.Dfpwm;

/// <summary>
/// Exposes a DFPWM1a (<c>.dfpwm</c>) file as a pseudo-archive of <c>FULL.dfpwm</c>
/// (Kind <c>Container</c>) plus the single decoded mono channel as <c>MONO.wav</c>
/// (Kind <c>Channel</c>, 8-bit unsigned PCM) and a <c>metadata.ini</c> (Kind
/// <c>Tag</c>). DFPWM is headerless and carries no sample rate or channel count, so
/// the surfaced WAV assumes mono at <see cref="DfpwmCodec.DefaultSampleRate"/>
/// (48000 Hz — the ComputerCraft convention); detection is by extension only.
/// The descriptor is creatable (WORM): it passes a supplied <c>FULL.dfpwm</c>
/// through unchanged or encodes a mono 8-bit WAV with <see cref="DfpwmCodec"/>.
/// </summary>
public sealed class DfpwmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
  IArchiveInMemoryExtract, IArchiveWriteConstraints, IArchiveCreatable {

  public string Id => "Dfpwm";
  public string DisplayName => "DFPWM1a (ComputerCraft)";
  public FormatCategory Category => FormatCategory.Audio;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dfpwm";
  public IReadOnlyList<string> Extensions => [".dfpwm"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // Headerless format: no magic signature — detection is by extension only.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("dfpwm", "DFPWM")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Entropy;
  public string Description => "DFPWM1a 1-bit audio (ComputerCraft); full file + decoded mono PCM.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    AudioPseudoArchive.List(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    AudioPseudoArchive.Extract(BuildEntries(stream), outputDir, files);

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) =>
    AudioPseudoArchive.ExtractEntry(BuildEntries(input), entryName, output);

  // ── IArchiveWriteConstraints ────────────────────────────────────────────────

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "DFPWM archive accepts: FULL.dfpwm, MONO.wav (8-bit mono), metadata.ini";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    var name = Path.GetFileName(input.ArchiveName).ToLowerInvariant();
    if (name is "full.dfpwm" or "metadata.ini" || name.EndsWith(".wav")) {
      reason = null;
      return true;
    }
    reason = $"not a DFPWM-archive input (got {input.ArchiveName}); {AcceptedInputsDescription}";
    return false;
  }

  // ── IArchiveCreatable: pass through FULL.dfpwm or encode a mono 8-bit WAV ──────

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fileList = FormatHelpers.FilesOnly(inputs).ToList();

    var full = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).Equals("FULL.dfpwm", StringComparison.OrdinalIgnoreCase));
    if (full.Data != null) {
      output.Write(full.Data);
      return;
    }

    var wav = fileList.FirstOrDefault(f =>
      Path.GetFileName(f.Name).EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    if (wav.Data == null)
      throw new InvalidOperationException("DFPWM archive create needs either FULL.dfpwm or a mono WAV.");

    var parsed = new WavReader().Read(wav.Data);
    if (parsed.NumChannels != 1)
      throw new InvalidOperationException("DFPWM create requires a mono WAV.");
    if (parsed.BitsPerSample != 8)
      throw new InvalidOperationException("DFPWM create requires 8-bit unsigned PCM.");

    output.Write(DfpwmCodec.Compress(parsed.InterleavedPcm));
  }

  // ── Shared archive-entry builder ─────────────────────────────────────────────

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.dfpwm", "Container", blob, "dfpwm"),
    };

    try {
      var pcm = DfpwmCodec.Decompress(blob);
      entries.Add(new("MONO.wav", "Channel",
        PcmCodec.ToWavBlob(pcm, 1, DfpwmCodec.DefaultSampleRate, 8, formatCode: 1), "pcm"));

      var meta = new StringBuilder();
      meta.AppendLine("format=DFPWM1a");
      meta.AppendLine("channels=1");
      meta.AppendLine($"sample_rate={DfpwmCodec.DefaultSampleRate}");
      meta.AppendLine("bits_per_sample=8");
      meta.AppendLine($"samples={pcm.Length}");
      meta.AppendLine("note=DFPWM is headerless; sample rate and channel count assume ComputerCraft defaults.");
      entries.Add(new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString()), "stored"));
    } catch (Exception) {
      // Graceful fallback: surface the original DFPWM file only.
    }

    return entries;
  }
}
