#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Its;

namespace FileFormat.Iti;

/// <summary>
/// Exposes an Impulse Tracker instrument (<c>.iti</c>, an <c>IMPI</c> instrument header
/// followed by its embedded <c>IMPS</c> samples) as a pseudo-archive of <c>FULL.iti</c>
/// (Kind <c>Container</c>), a <c>metadata.ini</c> (Kind <c>Tag</c>) and one playable WAV
/// per embedded sample (Kind <c>Sample</c>).
/// </summary>
/// <remarks>
/// PRAGMATIC SCOPE: rather than parse the 554-byte <c>IMPI</c> header's note/sample map and
/// envelopes, the file is scanned for <c>IMPS</c> signatures and each is decoded with the
/// shared <see cref="ItsSampleDecoder"/>. Embedded samples whose pointer is file-absolute are
/// decoded at their C5 speed; compressed samples are skipped (noted in metadata).
/// </remarks>
public sealed class ItiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Iti";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Impulse Tracker Instrument";
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
  public string DefaultExtension => ".iti";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".iti"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("IMPI"u8.ToArray(), Offset: 0, Confidence: 0.95),
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
  public string Description => "Impulse Tracker instrument; full file + playable WAVs for each embedded sample.";

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
      new("FULL.iti", "Container", blob),
    };

    var instrumentName = "";
    var sampleCount = 0;
    var compressedSkipped = 0;
    try {
      if (blob.Length >= 4 && blob[0] == 'I' && blob[1] == 'M' && blob[2] == 'P' && blob[3] == 'I') {
        // Instrument name is char[26] at offset 32 in the IMPI header.
        instrumentName = ItsSampleDecoder.ReadAsciiTrim(blob, 32, 26);

        var idx = 0;
        var pos = 4; // skip the leading IMPI so we only find embedded sample headers
        while (pos + ItsSampleDecoder.HeaderSize <= blob.Length) {
          var hit = IndexOfImps(blob, pos);
          if (hit < 0) break;
          if (ItsSampleDecoder.TryParse(blob, hit, out var s)) {
            ++idx;
            var wav = ItsSampleDecoder.BuildWav(blob, s);
            if (wav != null) {
              var label = string.IsNullOrWhiteSpace(s.Name)
                ? (string.IsNullOrWhiteSpace(s.DosName) ? "sample" : ItsSampleDecoder.SanitizeFileName(s.DosName))
                : ItsSampleDecoder.SanitizeFileName(s.Name);
              entries.Add(new($"samples/{idx:D2}_{label}.wav", "Sample", wav));
              ++sampleCount;
            } else if (s.Compressed) {
              ++compressedSkipped;
            }
            pos = hit + ItsSampleDecoder.HeaderSize;
          } else {
            pos = hit + 1;
          }
        }
      }
    } catch {
      // Graceful FULL-only fallback.
    }

    var info = new StringBuilder();
    info.AppendLine("format=IMPI");
    info.AppendLine($"instrument_name={instrumentName}");
    info.AppendLine($"sample_count={sampleCount}");
    info.AppendLine($"compressed_skipped={compressedSkipped}");
    entries.Insert(1, new("metadata.ini", "Tag", Encoding.UTF8.GetBytes(info.ToString())));

    return entries;
  }

  private static int IndexOfImps(byte[] blob, int start) {
    for (var i = start; i + 4 <= blob.Length; ++i)
      if (blob[i] == 'I' && blob[i + 1] == 'M' && blob[i + 2] == 'P' && blob[i + 3] == 'S')
        return i;
    return -1;
  }
}
