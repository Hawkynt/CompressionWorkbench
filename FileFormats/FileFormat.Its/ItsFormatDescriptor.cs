#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Its;

/// <summary>
/// Exposes a standalone Impulse Tracker sample (<c>.its</c>, 80-byte <c>IMPS</c> header) as
/// a pseudo-archive of <c>FULL.its</c> (Kind <c>Container</c>), a <c>metadata.ini</c>
/// (Kind <c>Tag</c>) and the single decoded sample as a playable WAV (Kind <c>Sample</c>)
/// at the header's C5 speed. IT215-compressed samples (flags bit&#160;3) are not decoded;
/// the view falls back to FULL-only with a metadata note.
/// </summary>
public sealed class ItsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Its";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Impulse Tracker Sample";
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
public string DefaultExtension => ".its";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".its"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("IMPS"u8.ToArray(), Offset: 0, Confidence: 0.95),
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
public AlgorithmFamily Family => AlgorithmFamily.Classic;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Impulse Tracker sample; full file + single playable WAV at C5 speed.";

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

  private static IReadOnlyList<AudioPseudoArchive.Entry> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    return Parse(blob);
  }

  private static IReadOnlyList<AudioPseudoArchive.Entry> Parse(byte[] blob) {
    var entries = new List<AudioPseudoArchive.Entry> {
      new("FULL.its", "Container", blob),
    };

    var note = "ok";
    var rate = ItsSampleDecoder.FallbackSampleRate;
    var bits = 8;
    try {
      if (ItsSampleDecoder.TryParse(blob, 0, out var s)) {
        rate = s.SampleRate;
        bits = s.Bits;
        var wav = ItsSampleDecoder.BuildWav(blob, s);
        if (wav != null) {
          var label = string.IsNullOrWhiteSpace(s.Name)
            ? (string.IsNullOrWhiteSpace(s.DosName) ? "sample" : ItsSampleDecoder.SanitizeFileName(s.DosName))
            : ItsSampleDecoder.SanitizeFileName(s.Name);
          entries.Add(new($"samples/01_{label}.wav", "Sample", wav));
          if (s.Compressed)
            note = s.It215 ? "ok (IT215 decompressed)" : "ok (IT214 decompressed)";
        } else if (s.Compressed) {
          note = "compressed (IT2xx decode failed) — FULL only";
        } else {
          note = "no usable sample data — FULL only";
        }
      } else {
        note = "no valid IMPS header — FULL only";
      }
    } catch {
      note = "parse error — FULL only";
    }

    var info = new StringBuilder();
    info.AppendLine("format=IMPS");
    info.AppendLine($"sample_rate={rate}");
    info.AppendLine($"bits={bits}");
    info.AppendLine($"status={note}");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }
}
