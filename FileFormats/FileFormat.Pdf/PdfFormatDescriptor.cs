#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pdf;

public sealed class PdfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

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
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pdf";
  public IReadOnlyList<string> Extensions => [".pdf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-'], Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods =>
    [new("dct", "DCTDecode (JPEG)"), new("jpx", "JPXDecode (JPEG2000)"), new("flate", "FlateDecode")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "PDF image extraction + file-attachment R/W. Add/Remove use ISO 32000-1 §7.5.6 " +
    "incremental updates: every byte before the original %%EOF stays byte-identical; " +
    "mutations append a new xref subsection + trailer with /Prev linking back to the " +
    "prior xref. Removal tombstones via xref free-list entries ('f' tag, generation+1).";

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

  /// <summary>
  /// Adds file attachments to an existing PDF via ISO 32000-1 §7.5.6
  /// incremental updates. Every byte before the original <c>%%EOF</c> stays
  /// byte-identical; a single new section is appended carrying the new
  /// EmbeddedFile + Filespec objects, a revised Catalog, a new xref
  /// subsection and a trailer with <c>/Prev</c> linking to the original
  /// xref.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var attachments = new List<(string Name, byte[] Data)>(inputs.Count);
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      attachments.Add((i.ArchiveName, i.ReadContent()));
    }
    if (attachments.Count == 0) return;
    PdfInPlaceModifier.AddFiles(archive, attachments);
  }

  /// <summary>
  /// Tombstones the named attachments via an incremental update. Their
  /// original Filespec + EmbeddedFile object bytes survive (true in-place —
  /// not overwritten) but are marked free ('f', generation+1) in the new
  /// xref subsection, so the spec-aware <see cref="PdfReader"/> stops
  /// listing them.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Length == 0) return;
    PdfInPlaceModifier.RemoveFiles(archive, entryNames);
  }
}
