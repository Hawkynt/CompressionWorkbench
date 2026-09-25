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
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Jp2";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "JPEG 2000";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Image;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".jp2";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".jp2", ".jpf", ".jpx", ".j2c", ".jpc"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ISOBMFF-wrapped JP2 signature box: 0x00 00 00 0C 'jP  ' 0x0D 0A 87 0A
    new(new byte[] { 0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A }, Offset: 0, Confidence: 0.98),
    // Raw codestream: SOC (FF 4F) + SIZ (FF 51)
    new(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }, Offset: 0, Confidence: 0.95),
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
    "JPEG 2000 image container (.jp2/.jpx/.j2c). Surfaces codestream, tiles, and metadata boxes. " +
    "Codestream itself is not decoded.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private sealed record EntryLayout(string Name, string Kind, int Offset, int Length, byte[]? Generated = null) {
    public int Size => this.Generated?.Length ?? this.Length;
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();
    return BuildLayout(blob)
      .Select(e => (e.Name, e.Kind, e.Generated ?? blob.AsSpan(e.Offset, e.Length).ToArray()))
      .ToList();
  }

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password) =>
    BuildLayout(archive).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Size, CompressedSize: e.Size,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  void IArchiveFormatOperations.ExtractSpan(
      ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files) {
    foreach (var entry in BuildLayout(archive)) {
      if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files))
        continue;
      if (entry.Generated is { } generated)
        WriteFile(outputDir, entry.Name, generated);
      else {
        using var output = CreateEntryFile(outputDir, entry.Name);
        output.Write(archive.Slice(entry.Offset, entry.Length));
      }
    }
  }

  private static List<EntryLayout> BuildLayout(ReadOnlySpan<byte> blob) {
    var isCodestream = blob.Length >= 4
      && blob[0] == 0xFF && blob[1] == 0x4F
      && blob[2] == 0xFF && blob[3] == 0x51;
    var isBoxForm = blob.Length >= 12
      && blob[0] == 0x00 && blob[1] == 0x00 && blob[2] == 0x00 && blob[3] == 0x0C
      && blob[4] == 0x6A && blob[5] == 0x50 && blob[6] == 0x20 && blob[7] == 0x20;

    var entries = new List<EntryLayout> {
      new($"FULL{(isCodestream ? ".j2c" : ".jp2")}", "Track", 0, blob.Length),
    };

    var meta = new StringBuilder();
    meta.AppendLine("; JPEG 2000 container metadata");

    var codestreamOffset = 0;
    var codestreamLength = 0;
    if (isCodestream) {
      meta.AppendLine("form=codestream");
      codestreamLength = blob.Length;
      AppendSizFromCodestream(blob, meta);
    } else if (isBoxForm) {
      meta.AppendLine("form=box");
      var boxes = new BoxParser().Parse(blob);

      var ihdr = BoxParser.Find(boxes, "ihdr");
      if (ihdr != null && ihdr.BodyLength >= 14
          && ihdr.BodyOffset >= 0 && ihdr.BodyLength <= int.MaxValue
          && ihdr.BodyOffset + ihdr.BodyLength <= blob.Length) {
        var body = blob.Slice((int)ihdr.BodyOffset, (int)ihdr.BodyLength);
        var height = BinaryPrimitives.ReadUInt32BigEndian(body);
        var width = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
        var nc = BinaryPrimitives.ReadUInt16BigEndian(body[8..]);
        var bpc = body[10];
        meta.Append("width=").AppendLine(width.ToString(CultureInfo.InvariantCulture));
        meta.Append("height=").AppendLine(height.ToString(CultureInfo.InvariantCulture));
        meta.Append("num_components=").AppendLine(nc.ToString(CultureInfo.InvariantCulture));
        meta.Append("bit_depth=").AppendLine(((bpc & 0x7F) + 1).ToString(CultureInfo.InvariantCulture));
      }

      var jp2c = BoxParser.Find(boxes, "jp2c");
      if (jp2c != null && jp2c.BodyLength > 0
          && jp2c.BodyOffset >= 0 && jp2c.BodyLength <= int.MaxValue
          && jp2c.BodyOffset + jp2c.BodyLength <= blob.Length) {
        codestreamOffset = (int)jp2c.BodyOffset;
        codestreamLength = (int)jp2c.BodyLength;
        entries.Add(new EntryLayout("codestream.j2c", "Track", codestreamOffset, codestreamLength));
        if (ihdr == null)
          AppendSizFromCodestream(blob.Slice(codestreamOffset, codestreamLength), meta);
      }

      var xmlIdx = 0;
      foreach (var xml in BoxParser.FindAll(boxes, "xml ")) {
        if (xml.BodyLength < 0 || xml.BodyLength > int.MaxValue || xml.BodyOffset < 0
            || xml.BodyOffset + xml.BodyLength > blob.Length)
          continue;
        entries.Add(new EntryLayout(
          $"metadata/xml_{xmlIdx:D2}.xml", "Tag", (int)xml.BodyOffset, (int)xml.BodyLength));
        ++xmlIdx;
      }

      var uuidIdx = 0;
      foreach (var uuid in BoxParser.FindAll(boxes, "uuid")) {
        if (uuid.BodyLength < 0 || uuid.BodyLength > int.MaxValue || uuid.BodyOffset < 0
            || uuid.BodyOffset + uuid.BodyLength > blob.Length)
          continue;
        entries.Add(new EntryLayout(
          $"metadata/uuid_{uuidIdx:D2}.bin", "Tag", (int)uuid.BodyOffset, (int)uuid.BodyLength));
        ++uuidIdx;
      }
    } else {
      meta.AppendLine("form=unknown");
    }

    entries.Insert(1, new EntryLayout("metadata.ini", "Tag", 0, 0, Encoding.UTF8.GetBytes(meta.ToString())));

    if (codestreamLength > 0)
      SplitTiles(blob.Slice(codestreamOffset, codestreamLength), codestreamOffset, entries);

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

  private static void SplitTiles(
      ReadOnlySpan<byte> codestream, int sourceOffset, List<EntryLayout> entries) {
    var sots = new List<int>();
    var eoc = codestream.Length;
    for (var i = 0; i + 1 < codestream.Length; ++i) {
      if (codestream[i] != 0xFF) continue;
      var marker = codestream[i + 1];
      if (marker == 0x90)
        sots.Add(i);
      else if (marker == 0xD9) {
        eoc = i;
        break;
      }
    }

    for (var tileIndex = 0; tileIndex < sots.Count; ++tileIndex) {
      var start = sots[tileIndex];
      var end = tileIndex + 1 < sots.Count ? sots[tileIndex + 1] : eoc;
      if (end <= start) continue;
      entries.Add(new EntryLayout(
        $"images/tile_{tileIndex:D2}.j2c", "Track", sourceOffset + start, end - start));
    }
  }
}
