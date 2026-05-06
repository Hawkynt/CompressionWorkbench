#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.D81;

public sealed class D81FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveShrinkable, IArchiveModifiable {
  public long? MaxTotalArchiveSize => 819200;
  public string AcceptedInputsDescription =>
    "Commodore 1581 D81 disk; any file up to 819 200 bytes total.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  public IReadOnlyList<long> CanonicalSizes => [819200];
  public void Shrink(Stream input, Stream output) =>
    Compression.Registry.ArchiveShrinker.ShrinkViaRebuild(input, output, this, this, this.CanonicalSizes);

  public string Id => "D81";
  public string DisplayName => "D81";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing D81 image.
  /// Uses <c>D81Modifier</c> for true O(touched bytes) random-access
  /// I/O — only the BAM (2 sectors), the directory chain, and the file's
  /// data sectors are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      var truncated = name.Length > 16 ? name[..16] : name;
      D81Modifier.RemoveFile(archive, truncated, wipeData: true);
      D81Modifier.AddFile(archive, truncated, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing D81 image. Uses
  /// <c>D81Modifier</c> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames) {
      var truncated = name.Length > 16 ? name[..16] : name;
      D81Modifier.RemoveFile(archive, truncated, wipeData: true);
    }
  }

  public string DefaultExtension => ".d81";
  public IReadOnlyList<string> Extensions => [".d81"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Commodore 1581 3.5\" disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new D81Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new D81Reader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new D81Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name.Length > 16 ? name[..16] : name, data);
    output.Write(w.Build());
  }
}
