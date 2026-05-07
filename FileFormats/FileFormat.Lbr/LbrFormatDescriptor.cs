#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Lbr;

public sealed class LbrFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Lbr";
  public string DisplayName => "LBR";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an LBR archive. Uses
  /// <see cref="LbrModifier"/> — reuses deleted directory slots and
  /// appends data after the existing data region. Throws if the
  /// pre-allocated directory pool is full.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FlatFiles(inputs)) {
      LbrModifier.RemoveFile(archive, name, wipeData: true);
      LbrModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes named entries; uses <see cref="LbrModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      LbrModifier.RemoveFile(archive, name, wipeData: true);
  }
  public string DefaultExtension => ".lbr";
  public IReadOnlyList<string> Extensions => [".lbr"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lbr", "LBR")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "CP/M LBR library archive format";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new LbrReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName,
      (long)e.SectorCount * 128, (long)e.SectorCount * 128, "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new LbrReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new LbrWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
  }
}
