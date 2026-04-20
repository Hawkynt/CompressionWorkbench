#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ThumbsDb;

public sealed class ThumbsDbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "ThumbsDb";
  public string DisplayName => "Thumbs.db";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".db";
  public IReadOnlyList<string> Extensions => [".db"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("cfb", "Compound File Binary")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Windows thumbnail cache (OLE2)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Msi.MsiReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.FullPath, e.Size, e.Size, "Stored",
      e.IsDirectory, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Msi.MsiReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: structurally-valid CFB envelope; not a real Thumbs.db (which has a
    // specific Catalog stream + per-thumbnail JPEG streams).
    var w = new Msi.CfbWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddStream(CfbStreamName(i.ArchiveName), File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }

  private static string CfbStreamName(string archiveName) {
    var leaf = Path.GetFileName(archiveName);
    if (string.IsNullOrEmpty(leaf)) leaf = archiveName;
    return leaf.Length > 31 ? leaf[..31] : leaf;
  }
}
