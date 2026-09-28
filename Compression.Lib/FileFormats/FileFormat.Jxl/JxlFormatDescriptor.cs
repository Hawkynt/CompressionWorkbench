#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using FileFormat.Mp4;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Jxl;

/// <summary>
/// JPEG XL image container surfaced as a read-only archive. Handles both forms:
/// the naked codestream (<c>FF 0A</c> prefix) and the ISOBMFF-wrapped variant
/// with a standard signature box and <c>jxlc</c>/<c>jxlp</c> codestream boxes.
/// Surfaces the full file, a metadata summary, the (re)assembled codestream,
/// and any EXIF/XMP/JUMBF metadata boxes. Does not decode the codestream.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://jpeg.org/jpegxl/</c> — JPEG committee JPEG XL page (ISO/IEC 18181; part 2 defines the box-based container)</description></item>
///   <item><description><c>https://github.com/libjxl/libjxl</c> — libjxl — reference implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/JPEG_XL</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class JxlFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Jxl";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "JPEG XL";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".jxl";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".jxl"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ISOBMFF-wrapped JXL signature box: 0x00 00 00 0C 'JXL ' 0x0D 0A 87 0A
    new(new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A }, Offset: 0, Confidence: 0.99),
    // Naked codestream: FF 0A
    new(new byte[] { 0xFF, 0x0A }, Offset: 0, Confidence: 0.92),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
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
    "JPEG XL image container (.jxl). Surfaces codestream and metadata boxes (EXIF, XMP, JUMBF). " +
    "Codestream itself is not decoded.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    List<(string Name, byte[] Data)> entries;
    try {
      entries = BuildEntries(stream);
    } catch {
      entries = [];
    }
    return entries.Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    List<(string Name, byte[] Data)> entries;
    try {
      entries = BuildEntries(stream);
    } catch {
      entries = [];
    }
    foreach (var e in entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private readonly record struct SourceRange(int Offset, int Length);

  private sealed record EntryLayout(
      string Name,
      IReadOnlyList<SourceRange> Ranges,
      byte[]? Generated = null) {

    public int Size {
      get {
        if (this.Generated is { } generated)
          return generated.Length;

        var total = 0;
        foreach (var range in this.Ranges)
          total = checked(total + range.Length);
        return total;
      }
    }
  }

  private static List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    return BuildLayout(blob)
      .Select(entry => (entry.Name, Materialize(blob, entry)))
      .ToList();
  }

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password) {
    List<EntryLayout> entries;
    try {
      entries = BuildLayout(archive);
    } catch {
      return [];
    }

    return entries.Select((entry, index) => new ArchiveEntryInfo(
      Index: index, Name: entry.Name,
      OriginalSize: entry.Size, CompressedSize: entry.Size,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null
    )).ToList();
  }

  void IArchiveFormatOperations.ExtractSpan(
      ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files) {
    List<EntryLayout> entries;
    try {
      entries = BuildLayout(archive);
    } catch {
      return;
    }

    foreach (var entry in entries) {
      if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files))
        continue;

      if (entry.Generated is { } generated) {
        WriteFile(outputDir, entry.Name, generated);
        continue;
      }

      using var output = CreateEntryFile(outputDir, entry.Name);
      foreach (var range in entry.Ranges)
        output.Write(archive.Slice(range.Offset, range.Length));
    }
  }

  private static List<EntryLayout> BuildLayout(ReadOnlySpan<byte> blob) {
    var isNaked = blob.Length >= 2 && blob[0] == 0xFF && blob[1] == 0x0A;
    var isBox = blob.Length >= 12
      && blob[0] == 0x00 && blob[1] == 0x00 && blob[2] == 0x00 && blob[3] == 0x0C
      && blob[4] == 0x4A && blob[5] == 0x58 && blob[6] == 0x4C && blob[7] == 0x20
      && blob[8] == 0x0D && blob[9] == 0x0A && blob[10] == 0x87 && blob[11] == 0x0A;

    var entries = new List<EntryLayout> {
      new("FULL.jxl", [new SourceRange(0, blob.Length)]),
    };

    var meta = new StringBuilder();
    meta.AppendLine("; JPEG XL container metadata");

    var codestreamRanges = new List<SourceRange>();
    SourceRange? exif = null;
    SourceRange? xmp = null;
    SourceRange? jumb = null;

    if (isNaked) {
      meta.AppendLine("form=naked");
      codestreamRanges.Add(new SourceRange(0, blob.Length));
    } else if (isBox) {
      meta.AppendLine("form=box");
      var boxes = new BoxParser().Parse(blob);

      var jxll = BoxParser.Find(boxes, "jxll");
      if (jxll != null && jxll.BodyLength >= 1 && IsValidRange(blob, jxll.BodyOffset, 1)) {
        var level = blob[(int)jxll.BodyOffset];
        meta.Append("level=").AppendLine(level.ToString(CultureInfo.InvariantCulture));
      }

      var jxlc = BoxParser.Find(boxes, "jxlc");
      if (jxlc != null && jxlc.BodyLength > 0 && TryRange(blob, jxlc.BodyOffset, jxlc.BodyLength, out var jxlcRange)) {
        codestreamRanges.Add(jxlcRange);
      } else {
        foreach (var part in BoxParser.FindAll(boxes, "jxlp")) {
          if (part.BodyLength <= 4)
            continue;
          if (TryRange(blob, part.BodyOffset + 4, part.BodyLength - 4, out var partRange))
            codestreamRanges.Add(partRange);
        }
      }

      var exifBox = BoxParser.Find(boxes, "Exif");
      if (exifBox != null && exifBox.BodyLength > 0
          && TryRange(blob, exifBox.BodyOffset, exifBox.BodyLength, out var exifRange))
        exif = exifRange;

      var xmpBox = BoxParser.Find(boxes, "xml ");
      if (xmpBox != null && xmpBox.BodyLength > 0
          && TryRange(blob, xmpBox.BodyOffset, xmpBox.BodyLength, out var xmpRange))
        xmp = xmpRange;

      var jumbBox = BoxParser.Find(boxes, "jumb");
      if (jumbBox != null && jumbBox.BodyLength > 0
          && TryRange(blob, jumbBox.BodyOffset, jumbBox.BodyLength, out var jumbRange))
        jumb = jumbRange;
    } else {
      meta.AppendLine("form=unknown");
    }

    var codestreamSize = 0;
    foreach (var range in codestreamRanges)
      codestreamSize = checked(codestreamSize + range.Length);

    meta.Append("has_exif=").AppendLine(exif.HasValue ? "true" : "false");
    meta.Append("has_xmp=").AppendLine(xmp.HasValue ? "true" : "false");
    meta.Append("has_jumb=").AppendLine(jumb.HasValue ? "true" : "false");
    meta.Append("codestream_size=").AppendLine(codestreamSize.ToString(CultureInfo.InvariantCulture));

    entries.Add(new EntryLayout("metadata.ini", [], Encoding.UTF8.GetBytes(meta.ToString())));

    if (codestreamRanges.Count > 0)
      entries.Add(new EntryLayout("codestream.jxl", codestreamRanges));

    if (exif is { } exifRangeValue)
      entries.Add(new EntryLayout("metadata/exif.bin", [exifRangeValue]));
    if (xmp is { } xmpRangeValue)
      entries.Add(new EntryLayout("metadata/xmp.xml", [xmpRangeValue]));
    if (jumb is { } jumbRangeValue)
      entries.Add(new EntryLayout("metadata/jumb.bin", [jumbRangeValue]));

    return entries;
  }

  private static byte[] Materialize(ReadOnlySpan<byte> blob, EntryLayout entry) {
    if (entry.Generated is { } generated)
      return generated;

    if (entry.Ranges.Count == 1) {
      var range = entry.Ranges[0];
      return blob.Slice(range.Offset, range.Length).ToArray();
    }

    var result = new byte[entry.Size];
    var destinationOffset = 0;
    foreach (var range in entry.Ranges) {
      blob.Slice(range.Offset, range.Length).CopyTo(result.AsSpan(destinationOffset));
      destinationOffset += range.Length;
    }
    return result;
  }

  private static bool IsValidRange(ReadOnlySpan<byte> blob, long offset, long length) =>
    offset >= 0 && length >= 0 && offset <= blob.Length && length <= blob.Length - offset;

  private static bool TryRange(
      ReadOnlySpan<byte> blob, long offset, long length, out SourceRange range) {
    if (!IsValidRange(blob, offset, length)) {
      range = default;
      return false;
    }

    range = new SourceRange((int)offset, (int)length);
    return true;
  }

}
