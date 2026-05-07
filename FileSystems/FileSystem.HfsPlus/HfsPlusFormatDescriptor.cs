#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.HfsPlus;

public sealed class HfsPlusFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "HfsPlus";
  public string DisplayName => "HFS+";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing HFS+ image via
  /// <see cref="HfsPlusModifier.AddFile"/>. The modifier mutates the catalog
  /// leaf, allocation bitmap, and volume header in place; on leaf overflow it
  /// transparently falls back to a writer-driven rebuild so the call always
  /// succeeds.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FlatFiles(inputs))
      HfsPlusModifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing HFS+ image via
  /// <see cref="HfsPlusModifier.RemoveFile"/>. File data blocks are wiped and
  /// the catalog records are excised from the leaf node; missing names are
  /// silently ignored.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      HfsPlusModifier.RemoveFile(archive, name, wipeData: true);
  }
  public string DefaultExtension => ".dmg";
  public IReadOnlyList<string> Extensions => [".dmg", ".hfsx", ".hfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x48, 0x2B], Offset: 1024, Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("hfsplus", "HFS+")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Apple HFS+ filesystem image. Writer emits full 248-byte TN1150
  /// HFSPlusCatalogFile records with HFSPlusForkData at offsets 88/168.
  /// </summary>
  public string Description => "Apple HFS+ filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new HfsPlusReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "Stored", e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new HfsPlusWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new HfsPlusReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }
}
