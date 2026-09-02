#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zarr;

/// <summary>
/// Zarr v2/v3 array metadata read-only pseudo-archive. Zarr is a chunked N-D array
/// store used by NumPy/SciPy/xarray; we surface a single <c>.zarray</c> (v2) or
/// <c>zarr.json</c> (v3) document as <c>FULL.json</c> plus an INI summary.
/// Detection is by JSON content sniffing (no extension or magic bytes).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://zarr-specs.readthedocs.io/</c> — Zarr storage specifications (v2 and v3)</description></item>
///   <item><description><c>https://zarr.dev/</c> — Zarr project home</description></item>
///   <item><description><c>https://github.com/zarr-developers/zarr-specs</c> — specification repository</description></item>
/// </list>
/// </summary>
public sealed class ZarrFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Zarr";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Zarr array metadata";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".json";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("zarr", "Zarr metadata")];
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
public string Description => "Zarr v2/v3 array metadata (read-only pseudo-archive)";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var fileSize = stream.Length;
    var meta = BuildMetadataIni(stream);
    return [
      new ArchiveEntryInfo(0, "FULL.json", fileSize, -1, "Stored", false, false, null),
      new ArchiveEntryInfo(1, "metadata.ini", meta.Length, -1, "Stored", false, false, null),
    ];
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);

    _ = ReadMetadata(stream);

    if (files == null || files.Length == 0 || MatchesFilter("FULL.json", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.json");
      var dir = Path.GetDirectoryName(fullPath);
      if (dir != null) Directory.CreateDirectory(dir);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }

    if (files == null || files.Length == 0 || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", BuildMetadataIni(stream));
  }

  private static byte[] BuildMetadataIni(Stream stream) {
    var reader = ReadMetadata(stream);

    var sb = new StringBuilder();
    sb.AppendLine("[zarr]");
    sb.Append("zarr_format = ").AppendLine(reader.ZarrFormat.ToString(CultureInfo.InvariantCulture));
    sb.Append("node_type = ").AppendLine(reader.NodeType);
    sb.Append("shape = ").AppendLine(string.Join(",", reader.Shape.Select(s => s.ToString(CultureInfo.InvariantCulture))));
    sb.Append("chunks = ").AppendLine(string.Join(",", reader.Chunks.Select(c => c.ToString(CultureInfo.InvariantCulture))));
    sb.Append("dtype = ").AppendLine(reader.DataType);
    sb.Append("compressor = ").AppendLine(reader.Compressor);
    sb.Append("filters_count = ").AppendLine(reader.FiltersCount.ToString(CultureInfo.InvariantCulture));
    sb.Append("codecs_count = ").AppendLine(reader.CodecsCount.ToString(CultureInfo.InvariantCulture));
    sb.Append("order = ").AppendLine(reader.Order);
    sb.Append("parse_status = ").AppendLine(reader.ParseStatus);
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static ZarrReader ReadMetadata(Stream stream) {
    var origin = stream.Position;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      return new ZarrReader(stream);
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }
  }
}
