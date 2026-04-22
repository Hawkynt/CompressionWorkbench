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
  public string Id => "Heif";
  public string DisplayName => "HEIF / HEIC";
  public FormatCategory Category => FormatCategory.Image;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".heic";
  public IReadOnlyList<string> Extensions => [".heic", ".heif", ".heix", ".hif"];
  public IReadOnlyList<string> CompoundExtensions => [];
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
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "HEVC / MIAF items")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "HEIF / HEIC (ISOBMFF) image container; each item surfaces as an entry.";

  private static IReadOnlyList<string> AcceptedBrands => HeifReader.HeifBrands;
  private const string ContainerExtension = ".heic";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
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
    var reader = new HeifReader(blob);

    if (!reader.MatchesAnyBrand(AcceptedBrands))
      throw new InvalidDataException($"HEIF: ftyp brand {reader.MajorBrand} not accepted.");

    var entries = new List<(string, string, byte[])> {
      ($"FULL{ContainerExtension}", "Track", blob),
    };

    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in reader.Items) {
      var data = reader.ReadItem(item.Id);
      if (data.Length == 0 && item.Type != "grid" && item.Type != "iovl") continue;

      var isPrimary = item.Id == reader.PrimaryItemId;
      var name = BuildItemName(item, isPrimary, used);
      var kind = item.Type switch {
        "Exif" => "Tag",
        "mime" => "Tag",
        "grid" => "Chunk",
        "iovl" => "Chunk",
        _ => "Frame",
      };

      var payload = item.Type switch {
        "grid" => DescribeGrid(data),
        "iovl" => DescribeOverlay(data),
        "Exif" => StripExifTiffHeader(data),
        _ => data,
      };
      entries.Add((name, kind, payload));
    }
    return entries;
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
    Encoding.UTF8.GetBytes($"iovl: {data.Length} bytes\n");

  // HEIF 'Exif' items are prefixed with a 4-byte TIFF-header offset. Skip it so
  // the output is a plain EXIF/TIFF stream that exiftool / libexif can read.
  private static byte[] StripExifTiffHeader(byte[] data) {
    if (data.Length <= 4) return data;
    return data.AsSpan(4).ToArray();
  }
}
