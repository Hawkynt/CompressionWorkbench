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
/// </summary>
public sealed class MachOFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "MachO";
  public string DisplayName => "Mach-O executable";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".macho";
  public IReadOnlyList<string> Extensions => [".macho", ".dylib", ".bundle", ".o"];
  public IReadOnlyList<string> CompoundExtensions => [];
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
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Mach-O executable (single-slice or fat/universal) surfaced as an archive of " +
    "architecture slices, segments, and metadata.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new MachOReader().ReadAll(stream);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.Length, e.Data.Length, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in new MachOReader().ReadAll(stream)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }
}
