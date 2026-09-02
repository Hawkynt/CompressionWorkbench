#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pdf;

/// <summary>
/// PDF document surfaced as an archive: embedded images plus EmbeddedFiles attachments, with in-place attachment R/W via ISO 32000 incremental updates.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/PDF32000_2008.pdf</c> — ISO 32000-1:2008 (PDF 1.7) as republished by Adobe — including the incremental-update and EmbeddedFiles clauses</description></item>
///   <item><description><c>https://pdfa.org</c> — PDF Association — ISO 32000-2 (PDF 2.0) resources</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/PDF</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class PdfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "PDF is a cross-referenced object stream (xref table + indirect objects + trailer) — " +
      "rebuilding from extracted images would destroy the document structure.");
    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);

  /// <inheritdoc />
    /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => PdfLayoutMap.Enumerate(archive);

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Pdf";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "PDF (Image Extraction)";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".pdf";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".pdf"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([(byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-'], Confidence: 0.90)];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods =>
    [new("dct", "DCTDecode (JPEG)"), new("jpx", "JPXDecode (JPEG2000)"), new("flate", "FlateDecode")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "PDF image extraction + file-attachment R/W. Add/Remove use ISO 32000-1 §7.5.6 " +
    "incremental updates: every byte before the original %%EOF stays byte-identical; " +
    "mutations append a new xref subsection + trailer with /Prev linking back to the " +
    "prior xref. Removal tombstones via xref free-list entries ('f' tag, generation+1).";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PdfReader(stream);
    var all = r.Entries.Concat(r.PageEntries);
    return all.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, e.Filter, false, false, null
    )).ToList();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PdfReader(stream);
    foreach (var e in r.Entries.Concat(r.PageEntries)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: embed every input file as a PDF file attachment via /EmbeddedFiles.
    // The result is a valid PDF that any viewer lists under "Attachments" and
    // our reader extracts via the /Type /Filespec + /EmbeddedFile path.
    var w = new PdfWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
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
