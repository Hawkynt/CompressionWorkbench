#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.D64;

public sealed class D64FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveShrinkable, IArchiveModifiable {
  public long? MaxTotalArchiveSize => 174848;  // standard 1541 single-sided D64 image size
  public string AcceptedInputsDescription =>
    "Commodore 1541 D64 disk; any file up to 174 848 bytes total (664 data sectors × 254 bytes).";

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    // C64 allows any filename internally; the PETSCII-to-ASCII mapping happens at write time.
    reason = null;
    return true;
  }

  // D64 has only one canonical size. Shrink therefore rebuilds to the fixed 174848 bytes.
  public IReadOnlyList<long> CanonicalSizes => [174848];
  public void Shrink(Stream input, Stream output) =>
    Compression.Registry.ArchiveShrinker.ShrinkViaRebuild(input, output, this, this, this.CanonicalSizes);

  public string Id => "D64";
  public string DisplayName => "D64";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing D64 image.
  /// Uses <see cref="D64Modifier"/> for true O(touched bytes) random-access
  /// I/O — only the BAM (1 sector) + directory chain (≤19 sectors) + the
  /// new file's data sectors (⌈len/254⌉ sectors) are read or written. The
  /// 174 848-byte image isn't touched outside that.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      var truncatedName = name.Length > 16 ? name[..16] : name;
      // Replacement semantics: if the file exists, remove it first.
      D64Modifier.RemoveFile(archive, truncatedName, wipeData: true);
      D64Modifier.AddFile(archive, truncatedName, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing D64 image. Uses
  /// <see cref="D64Modifier"/> for O(touched bytes) random-access I/O —
  /// walks the file chain, marks each sector free in the BAM, secure-wipes
  /// data sectors, and clears the directory entry's file-type byte.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames) {
      var truncatedName = name.Length > 16 ? name[..16] : name;
      D64Modifier.RemoveFile(archive, truncatedName, wipeData: true);
    }
  }

  public string DefaultExtension => ".d64";
  public IReadOnlyList<string> Extensions => [".d64"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Commodore 64 1541 disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new D64Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new D64Reader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new D64Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name.Length > 16 ? name[..16] : name, data);
    output.Write(w.Build());
  }
}
