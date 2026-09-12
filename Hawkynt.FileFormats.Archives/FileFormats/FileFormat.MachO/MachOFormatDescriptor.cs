#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.MachO;

/// <summary>
/// Read-only archive view of Mach-O executables (single-slice and fat/universal). Fat
/// binaries expose each architecture slice as an entry carrying the raw per-slice bytes;
/// single-slice binaries expose one entry per <c>LC_SEGMENT</c>/<c>LC_SEGMENT_64</c>
/// plus synthetic <c>symbols.txt</c>, <c>metadata/uuid.bin</c>, and
/// <c>metadata/code_signature.bin</c> entries where those load commands are present.
///
/// References:
/// <list type="bullet">
///   <item><description>Apple mach-o/loader.h and mach-o/fat.h headers (macOS SDK) — the authoritative structure definitions</description></item>
///   <item><description><c>https://github.com/apple-oss-distributions/xnu</c> — Apple's published XNU source (EXTERNAL_HEADERS/mach-o)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Mach-O</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class MachOFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "MachO";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Mach-O executable";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".macho";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".macho", ".dylib", ".bundle", ".o"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Fat magic (universal binaries) — all four byte-order variants.
    new([0xCA, 0xFE, 0xBA, 0xBE], Confidence: 0.90),
    new([0xCA, 0xFE, 0xBA, 0xBF], Confidence: 0.90),
    new([0xBE, 0xBA, 0xFE, 0xCA], Confidence: 0.90),
    new([0xBF, 0xBA, 0xFE, 0xCA], Confidence: 0.90),
    // Single-slice Mach-O (32-bit and 64-bit, each big- and little-endian).
    new([0xFE, 0xED, 0xFA, 0xCE], Confidence: 0.85),
    new([0xFE, 0xED, 0xFA, 0xCF], Confidence: 0.85),
    new([0xCE, 0xFA, 0xED, 0xFE], Confidence: 0.85),
    new([0xCF, 0xFA, 0xED, 0xFE], Confidence: 0.85),
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
  public string Description =>
    "Mach-O executable (single-slice or fat/universal) surfaced as an archive of " +
    "architecture slices, segments, and metadata.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new MachOReader().ReadAll(stream);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.Length, e.Data.Length, "Stored", false, false, null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in new MachOReader().ReadAll(stream)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }
}
