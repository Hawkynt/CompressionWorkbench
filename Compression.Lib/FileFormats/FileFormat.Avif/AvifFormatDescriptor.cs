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
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Avif";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "AVIF";
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
  public string DefaultExtension => ".avif";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".avif", ".avifs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ftypavif"u8.ToArray(), Offset: 4, Confidence: 0.97),
    new("ftypavis"u8.ToArray(), Offset: 4, Confidence: 0.97),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "AV1 OBU items")];
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
  public string Description => "AVIF (AV1 Image File Format, ISOBMFF); items extractable as AV1 OBU streams.";

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
    if (!HeifReader.MatchesAnyBrand(reader, HeifReader.AvifBrands))
      throw new InvalidDataException($"AVIF: ftyp brand {reader.MajorBrand} not accepted.");

    var entries = new List<EntryLayout> {
      new("FULL.avif", "Container", [new HeifReader.SourceRange(0, blob.Length)]),
    };

    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FULL.avif" };
    foreach (var item in reader.Items) {
      IReadOnlyList<HeifReader.SourceRange> ranges =
        HeifReader.GetItemRanges(reader, item.Id, blob.Length);
      var sourceLength = HeifReader.GetTotalLength(ranges);
      if (sourceLength == 0 && item.Type != "grid")
        continue;

      var isPrimary = item.Id == reader.PrimaryItemId;
      var extension = item.Type switch {
        "av01" => ".av1",
        "Exif" => ".bin",
        "mime" => ".bin",
        "grid" => ".txt",
        _ => ".bin",
      };
      var stem = item.Type == "Exif" ? "metadata/exif"
               : item.Type == "mime" ? $"metadata/{Sanitize(item.Name ?? item.ContentType ?? $"item_{item.Id}")}"
               : $"item_{item.Id:D3}_{Sanitize(item.Type)}";
      var name = stem + extension;
      if (isPrimary && item.Type != "Exif" && item.Type != "mime")
        name = "primary_" + name;
      name = Unique(name, used);

      var kind = item.Type == "Exif" || item.Type == "mime" ? "Tag"
               : item.Type == "grid" ? "Chunk"
               : "Frame";

      byte[]? generated = null;
      switch (item.Type) {
        case "grid":
          generated = Encoding.UTF8.GetBytes($"grid: {sourceLength} bytes\n");
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
