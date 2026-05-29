#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tux2;

/// <summary>
/// Read-only descriptor for TUX2 — Daniel Phillips's 2000-era phase-tree
/// filesystem proposal. TUX2 never reached a stable shipping on-disk
/// format; this descriptor recognises a deterministic synthetic header
/// pattern (magic "TUX2FS\0\0" at offset 0) so research images we
/// generate can be detected and round-tripped. Real legacy prototype
/// images would need a custom parser matching the specific snapshot of
/// the in-progress code that produced them.
/// </summary>
public sealed class Tux2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable {
  public string Id => "Tux2";
  public string DisplayName => "TUX2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".tux2";
  public IReadOnlyList<string> Extensions => [".tux2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("TUX2FS\0\0"u8.ToArray(), Offset: 0, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "TUX2 phase-tree research filesystem (Daniel Phillips, ~2000) — read-only synthetic.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Tux2Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Tux2Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("Tux2 read-only — defragmentation requires a writer.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("Tux2 read-only — defragmentation requires a writer.");
}
