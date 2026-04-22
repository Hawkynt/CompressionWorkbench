#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Heif;

namespace FileFormat.Avif;

/// <summary>
/// Exposes an AVIF (AV1 Image File Format, ISO/IEC 23000-22) file as an archive.
/// Reuses the <see cref="HeifReader"/> box walker; the only meaningful difference
/// from HEIC is the <c>ftyp</c> brand check (accepts <c>avif</c> / <c>avis</c>)
/// and primary items surface as <c>.av1</c> OBU streams.
/// </summary>
public sealed class AvifFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Avif";
  public string DisplayName => "AVIF";
  public FormatCategory Category => FormatCategory.Image;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".avif";
  public IReadOnlyList<string> Extensions => [".avif", ".avifs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ftypavif"u8.ToArray(), Offset: 4, Confidence: 0.97),
    new("ftypavis"u8.ToArray(), Offset: 4, Confidence: 0.97),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "AV1 OBU items")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "AVIF (AV1 Image File Format, ISOBMFF); items extractable as AV1 OBU streams.";

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

    if (!reader.MatchesAnyBrand(HeifReader.AvifBrands))
      throw new InvalidDataException($"AVIF: ftyp brand {reader.MajorBrand} not accepted.");

    var entries = new List<(string, string, byte[])> {
      ("FULL.avif", "Track", blob),
    };

    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FULL.avif" };
    foreach (var item in reader.Items) {
      var data = reader.ReadItem(item.Id);
      if (data.Length == 0 && item.Type != "grid") continue;

      var isPrimary = item.Id == reader.PrimaryItemId;
      var ext = item.Type switch {
        "av01" => ".av1",
        "Exif" => ".bin",
        "mime" => ".bin",
        "grid" => ".txt",
        _ => ".bin",
      };
      var stem = item.Type == "Exif" ? "metadata/exif"
               : item.Type == "mime" ? $"metadata/{Sanitize(item.Name ?? item.ContentType ?? $"item_{item.Id}")}"
               : $"item_{item.Id:D3}_{Sanitize(item.Type)}";
      var name = stem + ext;
      if (isPrimary && item.Type != "Exif" && item.Type != "mime") name = "primary_" + name;
      name = Unique(name, used);

      var payload = item.Type switch {
        "Exif" => data.Length > 4 ? data.AsSpan(4).ToArray() : data,
        "grid" => Encoding.UTF8.GetBytes($"grid: {data.Length} bytes\n"),
        _ => data,
      };
      var kind = item.Type == "Exif" || item.Type == "mime" ? "Tag"
               : item.Type == "grid" ? "Chunk"
               : "Frame";
      entries.Add((name, kind, payload));
    }
    return entries;
  }

  private static string Sanitize(string s) {
    var sb = new StringBuilder(s.Length);
    foreach (var c in s) {
      if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
      else sb.Append('_');
    }
    var r = sb.ToString().TrimEnd('.', '_');
    return r.Length == 0 ? "item" : r;
  }

  private static string Unique(string name, HashSet<string> used) {
    if (used.Add(name)) return name;
    var stem = Path.GetFileNameWithoutExtension(name);
    var ext = Path.GetExtension(name);
    var dir = Path.GetDirectoryName(name);
    for (var i = 1; ; i++) {
      var candidate = string.IsNullOrEmpty(dir) ? $"{stem}_{i}{ext}" : $"{dir}/{stem}_{i}{ext}";
      if (used.Add(candidate)) return candidate;
    }
  }
}
