#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using FileFormat.Mp4;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Jp2;

/// <summary>
/// JPEG 2000 container surfaced as a read-only archive. Handles both forms:
/// the ISOBMFF-wrapped <c>.jp2</c>/<c>.jpf</c>/<c>.jpx</c> file (signature box +
/// <c>ftyp</c> + <c>jp2h</c>/<c>jp2c</c> boxes) and the raw codestream
/// <c>.j2c</c>/<c>.jpc</c> form that starts with the SOC+SIZ marker pair.
/// Surfaces the full file, a metadata summary, the raw codestream, any XML/UUID
/// boxes, and each tile as an isolated byte range split on SOT markers. Does
/// not decode the codestream itself.
/// </summary>
public sealed class Jp2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Jp2";
  public string DisplayName => "JPEG 2000";
  public FormatCategory Category => FormatCategory.Image;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".jp2";
  public IReadOnlyList<string> Extensions => [".jp2", ".jpf", ".jpx", ".j2c", ".jpc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ISOBMFF-wrapped JP2 signature box: 0x00 00 00 0C 'jP  ' 0x0D 0A 87 0A
    new(new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A }, Offset: 0, Confidence: 0.98),
    // Raw codestream: SOC (FF 4F) + SIZ (FF 51)
    new(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }, Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "JPEG 2000 image container (.jp2/.jpx/.j2c). Surfaces codestream, tiles, and metadata boxes. " +
    "Codestream itself is not decoded.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var isCodestream = blob.Length >= 4
      && blob[0] == 0xFF && blob[1] == 0x4F
      && blob[2] == 0xFF && blob[3] == 0x51;
    var isBoxForm = blob.Length >= 12
      && blob[0] == 0x00 && blob[1] == 0x00 && blob[2] == 0x00 && blob[3] == 0x0C
      && blob[4] == 0x6A && blob[5] == 0x50 && blob[6] == 0x20 && blob[7] == 0x20;

    var entries = new List<(string Name, string Kind, byte[] Data)>();
    entries.Add(($"FULL{(isCodestream ? ".j2c" : ".jp2")}", "Track", blob));

    byte[] codestream;
    var meta = new StringBuilder();
    meta.AppendLine("; JPEG 2000 container metadata");
    if (isCodestream) {
      meta.AppendLine("form=codestream");
      codestream = blob;
      AppendSizFromCodestream(codestream, meta);
    } else if (isBoxForm) {
      meta.AppendLine("form=box");
      var boxes = new BoxParser().Parse(blob);
      // ihdr inside jp2h.
      var ihdr = BoxParser.Find(boxes, "ihdr");
      if (ihdr != null && ihdr.BodyLength >= 14) {
        var body = blob.AsSpan((int)ihdr.BodyOffset, (int)ihdr.BodyLength);
        var height = BinaryPrimitives.ReadUInt32BigEndian(body);
        var width = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
        var nc = BinaryPrimitives.ReadUInt16BigEndian(body[8..]);
        var bpc = body[10];
        meta.Append("width=").AppendLine(width.ToString(CultureInfo.InvariantCulture));
        meta.Append("height=").AppendLine(height.ToString(CultureInfo.InvariantCulture));
        meta.Append("num_components=").AppendLine(nc.ToString(CultureInfo.InvariantCulture));
        meta.Append("bit_depth=").AppendLine(((bpc & 0x7F) + 1).ToString(CultureInfo.InvariantCulture));
      }

      // Pull the jp2c codestream.
      var jp2c = BoxParser.Find(boxes, "jp2c");
      if (jp2c != null && jp2c.BodyLength > 0) {
        codestream = blob.AsSpan((int)jp2c.BodyOffset, (int)jp2c.BodyLength).ToArray();
        entries.Add(("codestream.j2c", "Track", codestream));
        if (ihdr == null) AppendSizFromCodestream(codestream, meta);
      } else {
        codestream = [];
      }

      // XML and UUID metadata boxes.
      var xmlIdx = 0;
      foreach (var xml in BoxParser.FindAll(boxes, "xml ")) {
        var xmlData = blob.AsSpan((int)xml.BodyOffset, (int)xml.BodyLength).ToArray();
        entries.Add(($"metadata/xml_{xmlIdx:D2}.xml", "Tag", xmlData));
        xmlIdx++;
      }
      var uuidIdx = 0;
      foreach (var uuid in BoxParser.FindAll(boxes, "uuid")) {
        var uuidData = blob.AsSpan((int)uuid.BodyOffset, (int)uuid.BodyLength).ToArray();
        entries.Add(($"metadata/uuid_{uuidIdx:D2}.bin", "Tag", uuidData));
        uuidIdx++;
      }
    } else {
      // Unknown form — just surface the raw blob, no further parsing.
      meta.AppendLine("form=unknown");
      codestream = [];
    }

    // Insert metadata.ini at position 1 after FULL.
    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    // Split codestream into tiles at SOT (FF 90) markers up to EOC (FF D9) or next SOT.
    if (codestream.Length > 0)
      SplitTiles(codestream, entries);

    return entries;
  }

  private static void AppendSizFromCodestream(ReadOnlySpan<byte> cs, StringBuilder meta) {
    // SIZ marker immediately after SOC: FF 51 Lsiz(2) Rsiz(2) Xsiz(4) Ysiz(4) XOsiz YOsiz XTsiz YTsiz XTO YTO Csiz(2) ...
    if (cs.Length < 2 + 2 + 2 + 2 + 4 + 4) return;
    if (cs[0] != 0xFF || cs[1] != 0x4F) return;
    if (cs[2] != 0xFF || cs[3] != 0x51) return;
    var pos = 4;
    // Skip Lsiz (2) + Rsiz (2)
    if (pos + 4 > cs.Length) return;
    pos += 4;
    if (pos + 8 > cs.Length) return;
    var xsiz = BinaryPrimitives.ReadUInt32BigEndian(cs[pos..]);
    var ysiz = BinaryPrimitives.ReadUInt32BigEndian(cs[(pos + 4)..]);
    pos += 8;
    // Skip XOsiz, YOsiz, XTsiz, YTsiz, XTOsiz, YTOsiz (6*4 = 24)
    pos += 24;
    if (pos + 2 > cs.Length) return;
    var csiz = BinaryPrimitives.ReadUInt16BigEndian(cs[pos..]);
    pos += 2;
    byte maxBitDepth = 0;
    for (var c = 0; c < csiz && pos + 3 <= cs.Length; c++) {
      var ssiz = cs[pos];
      var depth = (byte)((ssiz & 0x7F) + 1);
      if (depth > maxBitDepth) maxBitDepth = depth;
      pos += 3;
    }
    meta.Append("width=").AppendLine(xsiz.ToString(CultureInfo.InvariantCulture));
    meta.Append("height=").AppendLine(ysiz.ToString(CultureInfo.InvariantCulture));
    meta.Append("num_components=").AppendLine(csiz.ToString(CultureInfo.InvariantCulture));
    meta.Append("bit_depth=").AppendLine(maxBitDepth.ToString(CultureInfo.InvariantCulture));
  }

  private static void SplitTiles(byte[] codestream, List<(string Name, string Kind, byte[] Data)> entries) {
    // Find all SOT (FF 90) positions and the EOC (FF D9).
    var sots = new List<int>();
    var eoc = codestream.Length;
    for (var i = 0; i + 1 < codestream.Length; i++) {
      if (codestream[i] != 0xFF) continue;
      var m = codestream[i + 1];
      if (m == 0x90) sots.Add(i);
      else if (m == 0xD9) { eoc = i; break; }
    }
    for (var t = 0; t < sots.Count; t++) {
      var start = sots[t];
      var end = (t + 1 < sots.Count) ? sots[t + 1] : eoc;
      if (end <= start) continue;
      var len = end - start;
      if (len <= 0 || start + len > codestream.Length) continue;
      var tile = new byte[len];
      Array.Copy(codestream, start, tile, 0, len);
      entries.Add(($"images/tile_{t:D2}.j2c", "Track", tile));
    }
  }
}
