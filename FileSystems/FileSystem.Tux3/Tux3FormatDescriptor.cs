#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Tux3;

/// <summary>
/// Read-only descriptor for TUX3 — Daniel Phillips's version-tree
/// successor to TUX2 (linux-tux3 prototype). Magic "TUX3SUPR" sits
/// at file offset 4096 (the start of the superblock block). Full
/// itable/otable/atable B-tree traversal is out of scope; this
/// descriptor surfaces the parsed superblock as structured metadata
/// plus the raw image.
/// </summary>
public sealed class Tux3FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveDefragmentable {
  public string Id => "Tux3";
  public string DisplayName => "TUX3";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".tux3";
  public IReadOnlyList<string> Extensions => [".tux3"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("TUX3SUPR"u8.ToArray(), Offset: 4096, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "TUX3 version-tree research filesystem (linux-tux3) — superblock surface only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Tux3Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Tux3Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("Tux3 read-only — defragmentation requires a writer.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("Tux3 read-only — defragmentation requires a writer.");
}
