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
/// </summary>
public sealed class JxlFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Jxl";
  public string DisplayName => "JPEG XL";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".jxl";
  public IReadOnlyList<string> Extensions => [".jxl"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ISOBMFF-wrapped JXL signature box: 0x00 00 00 0C 'JXL ' 0x0D 0A 87 0A
    new(new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A }, Offset: 0, Confidence: 0.99),
    // Naked codestream: FF 0A
    new(new byte[] { 0xFF, 0x0A }, Offset: 0, Confidence: 0.92),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "JPEG XL image container (.jxl). Surfaces codestream and metadata boxes (EXIF, XMP, JUMBF). " +
    "Codestream itself is not decoded.";

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

  private static List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var isNaked = blob.Length >= 2 && blob[0] == 0xFF && blob[1] == 0x0A;
    var isBox = blob.Length >= 12
      && blob[0] == 0x00 && blob[1] == 0x00 && blob[2] == 0x00 && blob[3] == 0x0C
      && blob[4] == 0x4A && blob[5] == 0x58 && blob[6] == 0x4C && blob[7] == 0x20
      && blob[8] == 0x0D && blob[9] == 0x0A && blob[10] == 0x87 && blob[11] == 0x0A;

    var entries = new List<(string Name, byte[] Data)> {
      ("FULL.jxl", blob),
    };

    var meta = new StringBuilder();
    meta.AppendLine("; JPEG XL container metadata");

    byte[] codestream = [];
    byte[]? exif = null, xmp = null, jumb = null;
    int? level = null;

    if (isNaked) {
      meta.AppendLine("form=naked");
      codestream = blob;
    } else if (isBox) {
      meta.AppendLine("form=box");
      var boxes = new BoxParser().Parse(blob);

      // jxll: single int8 level (usually 5 or 10).
      var jxll = BoxParser.Find(boxes, "jxll");
      if (jxll != null && jxll.BodyLength >= 1) {
        level = blob[(int)jxll.BodyOffset];
        meta.Append("level=").AppendLine(level.Value.ToString(CultureInfo.InvariantCulture));
      }

      // Concatenate codestream: prefer single jxlc, else ordered jxlp parts.
      var jxlc = BoxParser.Find(boxes, "jxlc");
      if (jxlc != null && jxlc.BodyLength > 0) {
        codestream = blob.AsSpan((int)jxlc.BodyOffset, (int)jxlc.BodyLength).ToArray();
      } else {
        using var csMs = new MemoryStream();
        foreach (var part in BoxParser.FindAll(boxes, "jxlp")) {
          // Each jxlp body starts with a 4-byte partial index; skip it to get raw codestream bytes.
          if (part.BodyLength <= 4) continue;
          csMs.Write(blob, (int)(part.BodyOffset + 4), (int)(part.BodyLength - 4));
        }
        codestream = csMs.ToArray();
      }

      var exifBox = BoxParser.Find(boxes, "Exif");
      if (exifBox != null && exifBox.BodyLength > 0)
        exif = blob.AsSpan((int)exifBox.BodyOffset, (int)exifBox.BodyLength).ToArray();
      var xmpBox = BoxParser.Find(boxes, "xml ");
      if (xmpBox != null && xmpBox.BodyLength > 0)
        xmp = blob.AsSpan((int)xmpBox.BodyOffset, (int)xmpBox.BodyLength).ToArray();
      var jumbBox = BoxParser.Find(boxes, "jumb");
      if (jumbBox != null && jumbBox.BodyLength > 0)
        jumb = blob.AsSpan((int)jumbBox.BodyOffset, (int)jumbBox.BodyLength).ToArray();
    } else {
      meta.AppendLine("form=unknown");
    }

    meta.Append("has_exif=").AppendLine(exif != null ? "true" : "false");
    meta.Append("has_xmp=").AppendLine(xmp != null ? "true" : "false");
    meta.Append("has_jumb=").AppendLine(jumb != null ? "true" : "false");
    meta.Append("codestream_size=").AppendLine(codestream.Length.ToString(CultureInfo.InvariantCulture));

    entries.Add(("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString())));

    if (codestream.Length > 0)
      entries.Add(("codestream.jxl", codestream));

    if (exif != null) entries.Add(("metadata/exif.bin", exif));
    if (xmp != null) entries.Add(("metadata/xmp.xml", xmp));
    if (jumb != null) entries.Add(("metadata/jumb.bin", jumb));

    return entries;
  }
}
