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
  /// Adds (or replaces by name) files inside an existing HFS+ image.
  /// Read-extract-rebuild via <see cref="ModifyRebuilder"/>; the rebuild
  /// path doubles as a secure-wipe for replaced bytes.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs,
      readEntries: stream => {
        var r = new HfsPlusReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new HfsPlusWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });

  /// <summary>
  /// Removes the named entries from an existing HFS+ image. Image is rebuilt
  /// without the target entries — old file bytes are wiped because the new
  /// layout starts fresh.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => {
        var r = new HfsPlusReader(stream, leaveOpen: true);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new HfsPlusWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
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
