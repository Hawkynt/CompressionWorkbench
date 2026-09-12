#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Zip;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Microsoft Office Open XML / OPC packages surfaced as their original package parts.
/// </summary>
/// <remarks>
/// <para>
/// PNGCrushCS deliberately exposes Office documents as a semantic multi-image source: a GIF, TIFF,
/// ICO or WebP part can therefore contribute several <c>RawImage</c> instances. CompressionWorkbench
/// needs the complementary structural view. This descriptor never renders or flattens a part: it
/// delegates to the repository's ZIP implementation and lists/extracts the exact OPC members such as
/// <c>word/media/image1.png</c>, <c>ppt/media/image2.tiff</c>, <c>docProps/thumbnail.jpeg</c>,
/// relationship parts, XML parts, embedded OLE objects and custom-UI resources.
/// </para>
/// <para>
/// OPC defines package parts as URI-addressable byte streams with content types and relationships;
/// ZIP is only the physical mapping used by Office Open XML. The two mandatory package plumbing
/// entries checked here are <c>[Content_Types].xml</c> and <c>_rels/.rels</c>. Application-specific
/// parts are intentionally not synthesized or interpreted by this descriptor.
/// </para>
/// <para>
/// References: ISO/IEC 29500 / ECMA-376 Open Packaging Conventions and Microsoft's OPC Parts and
/// Relationships documentation. No external implementation code is used.
/// </para>
/// </remarks>
public sealed class OfficeOpenXmlPackageDescriptor
  : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IFormatValidator {

  private static readonly ZipFormatDescriptor _Zip = new();

  public string Id => "OfficeOpenXml";
  public string DisplayName => "Office Open XML package parts";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  public string DefaultExtension => ".docx";

  public IReadOnlyList<string> Extensions => [
    ".docx", ".docm", ".dotx", ".dotm",
    ".xlsx", ".xlsm", ".xltx", ".xltm",
    ".pptx", ".pptm", ".ppsx", ".ppsm", ".potx", ".potm",
  ];

  public IReadOnlyList<string> CompoundExtensions => [];

  // Do not compete with generic ZIP during content-only detection. Office extensions select this
  // descriptor; a renamed package remains explicitly reachable through --format OfficeOpenXml.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  public IReadOnlyList<FormatMethodInfo> Methods => _Zip.Methods;
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Structural Office Open XML view that preserves and exposes every original OPC/ZIP package part without flattening embedded multi-image assets.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var entries = _ListZip(stream, password);
    _RequireOpc(entries);
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    _RequireOpc(_ListZip(stream, password));
    _Rewind(stream);
    _Zip.Extract(stream, outputDir, password, files);
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
    _RequireOpc(_ListZip(archive, password));
    _Rewind(archive);
    return _Zip.OpenEntry(archive, entryName, password);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var entry = this.OpenEntry(archive, entryName, password);
    using var memory = new MemoryStream();
    entry.CopyTo(memory);
    return memory.ToArray();
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    using var entry = this.OpenEntry(input, entryName, password);
    entry.CopyTo(output);
  }

  public ValidationResult ValidateHeader(ReadOnlySpan<byte> header, long fileSize) {
    var zip = _Zip.ValidateHeader(header, fileSize);
    return new() {
      IsValid = zip.IsValid,
      Confidence = zip.IsValid ? Math.Min(zip.Confidence, 0.70) : zip.Confidence,
      Health = zip.Health,
      Level = ValidationLevel.Header,
      Issues = zip.Issues,
      ValidEntries = zip.ValidEntries,
      TotalEntries = zip.TotalEntries,
    };
  }

  public ValidationResult ValidateStructure(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    _Rewind(stream);
    var zip = _Zip.ValidateStructure(stream);
    return _AddOpcValidation(stream, zip, ValidationLevel.Structure);
  }

  public ValidationResult ValidateIntegrity(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    _Rewind(stream);
    var zip = _Zip.ValidateIntegrity(stream);
    return _AddOpcValidation(stream, zip, ValidationLevel.Integrity);
  }

  private static ValidationResult _AddOpcValidation(
    Stream stream,
    ValidationResult zip,
    ValidationLevel level) {

    if (!zip.IsValid)
      return zip;

    List<ArchiveEntryInfo> entries;
    try {
      entries = _ListZip(stream, password: null);
    } catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException) {
      var issues = zip.Issues.ToList();
      issues.Add(new(level, IssueSeverity.Error, "OPC_LIST_FAILED", exception.Message));
      return new() {
        IsValid = false,
        Confidence = Math.Min(zip.Confidence, 0.40),
        Health = FormatHealth.Damaged,
        Level = level,
        Issues = issues,
        ValidEntries = zip.ValidEntries,
        TotalEntries = zip.TotalEntries,
      };
    }

    var missing = _MissingOpcParts(entries);
    if (missing.Count == 0)
      return new() {
        IsValid = true,
        Confidence = Math.Max(zip.Confidence, 0.99),
        Health = zip.Health,
        Level = level,
        Issues = zip.Issues,
        ValidEntries = zip.ValidEntries ?? entries.Count,
        TotalEntries = zip.TotalEntries ?? entries.Count,
      };

    var combined = zip.Issues.ToList();
    combined.Add(new(
      level,
      IssueSeverity.Error,
      "OPC_REQUIRED_PART_MISSING",
      $"Office Open XML package is missing required OPC package item(s): {string.Join(", ", missing)}."));

    return new() {
      IsValid = false,
      Confidence = Math.Min(zip.Confidence, 0.40),
      Health = FormatHealth.Damaged,
      Level = level,
      Issues = combined,
      ValidEntries = zip.ValidEntries,
      TotalEntries = zip.TotalEntries,
    };
  }

  private static List<ArchiveEntryInfo> _ListZip(Stream stream, string? password) {
    _Rewind(stream);
    return _Zip.List(stream, password);
  }

  private static void _RequireOpc(IReadOnlyCollection<ArchiveEntryInfo> entries) {
    var missing = _MissingOpcParts(entries);
    if (missing.Count != 0)
      throw new InvalidDataException(
        $"Not an Office Open XML/OPC package: missing {string.Join(", ", missing)}.");
  }

  private static List<string> _MissingOpcParts(IReadOnlyCollection<ArchiveEntryInfo> entries) {
    var names = new HashSet<string>(entries.Select(entry => entry.Name), StringComparer.OrdinalIgnoreCase);
    var missing = new List<string>(2);
    if (!names.Contains("[Content_Types].xml"))
      missing.Add("[Content_Types].xml");
    if (!names.Contains("_rels/.rels"))
      missing.Add("_rels/.rels");
    return missing;
  }

  private static void _Rewind(Stream stream) {
    if (!stream.CanSeek)
      throw new NotSupportedException("Office Open XML package access requires a seekable stream.");
    stream.Position = 0;
  }
}
