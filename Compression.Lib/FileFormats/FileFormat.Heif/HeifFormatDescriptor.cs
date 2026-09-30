#pragma warning disable CS1591
using System.Text;
using Compression.Registry;

namespace FileFormat.Heif;

/// <summary>
/// Exposes an HEIF / HEIC file as an archive. Entries are one <c>FULL.heic</c> plus
/// one file per <c>iinf</c> item — decoded from <c>iloc</c> extents. HEVC items
/// surface as raw <c>.hevc</c> bitstreams (no Annex-B prepending: consumers that
/// need SPS/PPS can read the <c>hvcC</c> property). EXIF and MIME items land under
/// <c>metadata/</c>; grid / overlay items land as small text descriptors.
/// </summary>
public sealed class HeifFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Heif";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "HEIF / HEIC";
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
  public string DefaultExtension => ".heic";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".heic", ".heif", ".heix", ".hif"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ftyp brands we own. Higher confidence than MP4 so HEIC wins for 'heic'-branded files.
    new("ftypheic"u8.ToArray(), Offset: 4, Confidence: 0.95),
    new("ftypheix"u8.ToArray(), Offset: 4, Confidence: 0.95),
    new("ftypheim"u8.ToArray(), Offset: 4, Confidence: 0.95),
    new("ftypheis"u8.ToArray(), Offset: 4, Confidence: 0.95),
    new("ftypheif"u8.ToArray(), Offset: 4, Confidence: 0.95),
    new("ftypmif1"u8.ToArray(), Offset: 4, Confidence: 0.92),
    new("ftypmsf1"u8.ToArray(), Offset: 4, Confidence: 0.92),
    new("ftyphevc"u8.ToArray(), Offset: 4, Confidence: 0.95),
    new("ftyphevm"u8.ToArray(), Offset: 4, Confidence: 0.95),
    new("ftyphevs"u8.ToArray(), Offset: 4, Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "HEVC / MIAF items")];
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
  public string Description => "HEIF / HEIC (ISOBMFF) image container; each item surfaces as an entry.";

  private static IReadOnlyList<string> AcceptedBrands => HeifReader.HeifBrands;
  private const string ContainerExtension = ".heic";

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
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
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

  private sealed record EntryLayout(
      string Name,
      string Kind,
      IReadOnlyList<HeifReader.SourceRange> Ranges,
      byte[]? Generated = null) {

    public int Size => this.Generated?.Length ?? HeifReader.GetTotalLength(this.Ranges);
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    var blob = memory.ToArray();

    return BuildLayout(blob)
      .Select(entry => (
        entry.Name,
        entry.Kind,
        entry.Generated ?? MaterializeRanges(blob, entry.Ranges)))
      .ToList();
  }

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password) =>
    BuildLayout(archive).Select((entry, index) => new ArchiveEntryInfo(
      Index: index,
      Name: entry.Name,
      OriginalSize: entry.Size,
      CompressedSize: entry.Size,
      Method: "stored",
      IsDirectory: false,
      IsEncrypted: false,
      LastModified: null,
      Kind: entry.Kind)).ToList();

  void IArchiveFormatOperations.ExtractSpan(
      ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files) {
    foreach (var entry in BuildLayout(archive)) {
      if (files is { Length: > 0 } && !FormatHelpers.MatchesFilter(entry.Name, files))
        continue;

      if (entry.Generated is { } generated) {
        FormatHelpers.WriteFile(outputDir, entry.Name, generated);
        continue;
      }

      using var output = FormatHelpers.CreateEntryFile(outputDir, entry.Name);
      foreach (var range in entry.Ranges)
        output.Write(archive.Slice(range.Offset, range.Length));
    }
  }

  private static List<EntryLayout> BuildLayout(ReadOnlySpan<byte> blob) {
    var reader = HeifReader.ReadLayout(blob);
    if (!HeifReader.MatchesAnyBrand(reader, AcceptedBrands))
      throw new InvalidDataException($"HEIF: ftyp brand {reader.MajorBrand} not accepted.");

    var entries = new List<EntryLayout> {
      new($"FULL{ContainerExtension}", "Track", [new HeifReader.SourceRange(0, blob.Length)]),
    };

    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in reader.Items) {
      IReadOnlyList<HeifReader.SourceRange> ranges =
        HeifReader.GetItemRanges(reader, item.Id, blob.Length);
      var sourceLength = HeifReader.GetTotalLength(ranges);
      if (sourceLength == 0 && item.Type is not ("grid" or "iovl"))
        continue;

      var isPrimary = item.Id == reader.PrimaryItemId;
      var name = BuildItemName(item, isPrimary, used);
      var kind = item.Type switch {
        "Exif" => "Tag",
        "mime" => "Tag",
        "grid" => "Chunk",
        "iovl" => "Chunk",
        _ => "Frame",
      };

      byte[]? generated = null;
      switch (item.Type) {
        case "grid":
          generated = DescribeGrid(MaterializeRanges(blob, ranges));
          ranges = [];
          break;
        case "iovl":
          generated = DescribeOverlay(sourceLength);
          ranges = [];
          break;
        case "Exif" when sourceLength > 4:
          ranges = HeifReader.SkipPrefix(ranges, 4);
          break;
      }

      entries.Add(new EntryLayout(name, kind, ranges, generated));
    }

    return entries;
  }

  private static byte[] MaterializeRanges(
      ReadOnlySpan<byte> source, IReadOnlyList<HeifReader.SourceRange> ranges) {
    var result = new byte[HeifReader.GetTotalLength(ranges)];
    var destinationOffset = 0;
    foreach (var range in ranges) {
      source.Slice(range.Offset, range.Length).CopyTo(result.AsSpan(destinationOffset));
      destinationOffset += range.Length;
    }
    return result;
  }

  private static string BuildItemName(HeifReader.ItemInfo item, bool isPrimary, HashSet<string> used) {
    var ext = ChooseExtension(item.Type);
    string name = item.Type switch {
      "Exif" => "metadata/exif.bin",
      "mime" => $"metadata/{SanitizeLabel(item.Name ?? item.ContentType ?? $"item_{item.Id}")}.bin",
      "grid" => $"item_{item.Id:D3}_grid.txt",
      "iovl" => $"item_{item.Id:D3}_overlay.txt",
      _ => $"item_{item.Id:D3}_{SanitizeLabel(item.Type)}{ext}",
    };
    if (isPrimary && item.Type != "Exif" && item.Type != "mime")
      name = $"primary_{name}";
    return EnsureUnique(name, used);
  }

  private static string EnsureUnique(string name, HashSet<string> used) {
    if (used.Add(name)) return name;
    var stem = Path.GetFileNameWithoutExtension(name);
    var ext = Path.GetExtension(name);
    var dir = Path.GetDirectoryName(name);
    for (var i = 1; ; i++) {
      var candidate = string.IsNullOrEmpty(dir) ? $"{stem}_{i}{ext}" : $"{dir}/{stem}_{i}{ext}";
      if (used.Add(candidate)) return candidate;
    }
  }

  private static string ChooseExtension(string itemType) => itemType switch {
    "hvc1" or "hev1" => ".hevc",
    "av01" => ".av1",
    "avc1" => ".h264",
    "jpeg" => ".jpg",
    "png " or "png" => ".png",
    _ => ".bin",
  };

  private static string SanitizeLabel(string s) {
    var sb = new StringBuilder(s.Length);
    foreach (var c in s) {
      if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
      else sb.Append('_');
    }
    var r = sb.ToString().TrimEnd('.', '_');
    return r.Length == 0 ? "item" : r;
  }

  // 'grid' item body: version(1), flags(1), rows_minus_one(1), cols_minus_one(1),
  // output_width/height (2 or 4 bytes depending on flag bit 0).
  private static byte[] DescribeGrid(ReadOnlySpan<byte> data) {
    if (data.Length < 8) return Encoding.UTF8.GetBytes("grid: (truncated)\n");
    var flags = data[1];
    var rows = data[2] + 1;
    var cols = data[3] + 1;
    var wide = (flags & 1) != 0;
    int width, height;
    if (wide && data.Length >= 12) {
      width = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
      height = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8));
    } else {
      width = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4));
      height = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.Slice(6));
    }
    return Encoding.UTF8.GetBytes($"grid: {cols}x{rows} tiles, {width}x{height} px\n");
  }

  private static byte[] DescribeOverlay(ReadOnlySpan<byte> data) =>
    DescribeOverlay(data.Length);

  private static byte[] DescribeOverlay(int length) =>
    Encoding.UTF8.GetBytes($"iovl: {length} bytes\n");

  // HEIF 'Exif' items are prefixed with a 4-byte TIFF-header offset. Skip it so
  // the output is a plain EXIF/TIFF stream that exiftool / libexif can read.
  private static byte[] StripExifTiffHeader(byte[] data) {
    if (data.Length <= 4) return data;
    return data.AsSpan(4).ToArray();
  }
}
