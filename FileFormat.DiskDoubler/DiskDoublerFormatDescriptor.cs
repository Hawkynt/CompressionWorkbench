#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.DiskDoubler;

public sealed class DiskDoublerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "DiskDoubler";
  public string DisplayName => "DiskDoubler";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dd";
  public IReadOnlyList<string> Extensions => [".dd", ".sea"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // DiskDoubler has no universally reliable magic bytes; detection is primarily by extension.
  // The header version identifier at offset 0 varies by version so we use a low-confidence hint.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("diskdoubler", "DiskDoubler")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "DiskDoubler compressed Macintosh file (Salient Software, 1989-1993)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DiskDoublerReader(stream, leaveOpen: true);
    return r.Entries
      .Select((e, i) => new ArchiveEntryInfo(
        i,
        e.Name,
        e.OriginalSize,
        e.CompressedSize,
        $"Method {e.Method}",
        false,
        false,
        DateTime.MinValue))
      .ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DiskDoublerReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: store the first input as a DiskDoubler data fork (method 0 = stored).
    // DiskDoubler wraps a single Macintosh file, not a multi-file archive.
    var w = new DiskDoublerWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.SetFile(Path.GetFileName(i.ArchiveName), File.ReadAllBytes(i.FullPath));
      break;
    }
    w.WriteTo(output);
  }
}
