#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Bbc;

public sealed class BbcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {

  // 40-track SSD: 40 * 10 * 256 = 102 400 bytes. Writer emits this canonical size.
  public long? MaxTotalArchiveSize => BbcWriter.DiskSize40;
  public string AcceptedInputsDescription =>
    "BBC Micro Acorn DFS disk image (40/80-track, single or double sided).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>Canonical BBC DFS image sizes: 40-track SSD (102 400) and 80-track SSD (204 800).</summary>
  public IReadOnlyList<long> CanonicalSizes => [BbcWriter.DiskSize40, BbcWriter.DiskSize40 * 2L];

  public string Id => "Bbc";
  public string DisplayName => "BBC DFS";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  public string DefaultExtension => ".ssd";
  public IReadOnlyList<string> Extensions => [".ssd", ".dsd"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // DFS has no magic bytes — the catalog is just raw ASCII padded with spaces.
  // Detection is extension-based.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "BBC Micro Acorn DFS floppy disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var doubleSided = false;  // Callers who know better can pass the right reader directly.
    using var r = new BbcReader(stream, doubleSided);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.FullName, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new BbcReader(stream, doubleSided: false);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FullName, files)) continue;
      // Translate BBC "$.NAME" to a filesystem-safe "NAME" (or keep dir prefix as subdir
      // if it's not the default '$').
      var outName = e.Directory == '$' ? e.Name : $"{e.Directory}/{e.Name}";
      WriteFile(outputDir, outName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var total = 0L;
    foreach (var i in inputs) if (!i.IsDirectory) total += new FileInfo(i.FullPath).Length;
    if (total > BbcWriter.DiskSize40)
      throw new InvalidOperationException(
        $"BBC DFS: combined input size {total} bytes exceeds 40-track SSD capacity ({BbcWriter.DiskSize40} bytes).");

    var w = new BbcWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }
}
