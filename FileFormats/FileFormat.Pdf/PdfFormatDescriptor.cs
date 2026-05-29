#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pdf;

public sealed class PdfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "PDF is a cross-referenced object stream (xref table + indirect objects + trailer) — " +
      "rebuilding from extracted images would destroy the document structure.");
  public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => PdfLayoutMap.Enumerate(archive);

  public string Id => "Pdf";
  public string DisplayName => "PDF (Image Extraction)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pdf";
  public IReadOnlyList<string> Extensions => [".pdf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-'], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods =>
    [new("dct", "DCTDecode (JPEG)"), new("jpx", "JPXDecode (JPEG2000)"), new("flate", "FlateDecode")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PDF image extraction (JPEG, JPEG2000, raw image streams)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PdfReader(stream);
    var all = r.Entries.Concat(r.PageEntries);
    return all.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, e.Filter, false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PdfReader(stream);
    foreach (var e in r.Entries.Concat(r.PageEntries)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: embed every input file as a PDF file attachment via /EmbeddedFiles.
    // The result is a valid PDF that any viewer lists under "Attachments" and
    // our reader extracts via the /Type /Filespec + /EmbeddedFile path.
    var w = new PdfWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }
}
