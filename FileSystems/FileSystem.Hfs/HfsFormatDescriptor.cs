#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Hfs;

public sealed class HfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Hfs";
  public string DisplayName => "HFS (Classic)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Hfs image.
  /// Read-extract-rebuild via <see cref="ModifyRebuilder"/>; the rebuild
  /// path doubles as a secure-wipe for replaced bytes.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs,
      readEntries: stream => {
        var r = new HfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new HfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });

  /// <summary>
  /// Removes the named entries from an existing Hfs image. The image is
  /// rebuilt without the target entries — old file bytes are wiped because
  /// the new layout starts fresh, leaving no forensic trace.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => {
        var r = new HfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new HfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });

  public string DefaultExtension => ".hfs";
  public IReadOnlyList<string> Extensions => [".hfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x42, 0x44], Offset: 1024, Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Classic Macintosh HFS filesystem image (pre-HFS+). Writer emits a
  /// spec-compliant MDB, volume bitmap, and real extents + catalog B-trees
  /// with thread records, file records, and a root-dir record — matching
  /// Inside Macintosh: Files (1992). Scope: flat root directory, ASCII
  /// filenames, ≤ ~30 files per image (single-leaf catalog).
  /// </summary>
  public string Description => "Classic Macintosh HFS filesystem image (pre-HFS+)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new HfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new HfsWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new HfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
