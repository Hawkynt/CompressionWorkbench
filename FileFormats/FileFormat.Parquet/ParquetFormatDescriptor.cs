#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Parquet;

/// <summary>
/// Apache Parquet (<c>.parquet</c>) read-only pseudo-archive. Validates the leading and trailing
/// PAR1 magics, reads the footer length, and walks the Thrift compact-encoded FileMetaData footer
/// to surface version, row count, row-group count, schema element names and the created-by string.
/// Surfaces a <c>FULL.parquet</c> passthrough plus a <c>metadata.ini</c> summary. Page-level
/// decompression and full record decoding are out of scope.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/apache/parquet-format</c> — canonical format specification repository</description></item>
///   <item><description><c>https://parquet.apache.org</c> — Apache Parquet project documentation</description></item>
/// </list>
/// </summary>
public sealed class ParquetFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Parquet";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Apache Parquet";
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
public string DefaultExtension => ".parquet";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".parquet"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("PAR1"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("parquet", "Parquet")];
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
public string Description => "Apache Parquet columnar (read-only pseudo-archive)";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var fileSize = stream.Length;
    var meta = BuildMetadataIni(stream);
    return [
      new ArchiveEntryInfo(0, "FULL.parquet", fileSize, -1, "Stored", false, false, null),
      new ArchiveEntryInfo(1, "metadata.ini", meta.Length, -1, "Stored", false, false, null),
    ];
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);

    if (files == null || files.Length == 0 || MatchesFilter("FULL.parquet", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.parquet");
      var dir = Path.GetDirectoryName(fullPath);
      if (dir != null) Directory.CreateDirectory(dir);
      using var outStream = File.Create(fullPath);
      stream.CopyTo(outStream);
    }

    if (files == null || files.Length == 0 || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", BuildMetadataIni(stream));
  }

  private static byte[] BuildMetadataIni(Stream stream) {
    var origin = stream.Position;
    int version;
    long numRows;
    int numRowGroups;
    int numColumns;
    string schema;
    string createdBy;
    string parseStatus;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var reader = new ParquetReader(stream);
      version = reader.Version;
      numRows = reader.NumRows;
      numRowGroups = reader.NumRowGroups;
      numColumns = reader.Columns.Count;
      schema = string.Join(";", reader.Columns);
      createdBy = reader.CreatedBy ?? string.Empty;
      parseStatus = reader.ParseStatus;
    } catch (InvalidDataException) {
      version = 0;
      numRows = 0;
      numRowGroups = 0;
      numColumns = 0;
      schema = string.Empty;
      createdBy = string.Empty;
      parseStatus = "partial";
    } catch (EndOfStreamException) {
      version = 0;
      numRows = 0;
      numRowGroups = 0;
      numColumns = 0;
      schema = string.Empty;
      createdBy = string.Empty;
      parseStatus = "partial";
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }

    var sb = new StringBuilder();
    sb.AppendLine("[parquet]");
    sb.Append("version = ").AppendLine(version.ToString(CultureInfo.InvariantCulture));
    sb.Append("num_rows = ").AppendLine(numRows.ToString(CultureInfo.InvariantCulture));
    sb.Append("num_row_groups = ").AppendLine(numRowGroups.ToString(CultureInfo.InvariantCulture));
    sb.Append("num_columns = ").AppendLine(numColumns.ToString(CultureInfo.InvariantCulture));
    sb.Append("schema = ").AppendLine(EscapeIniValue(schema));
    sb.Append("created_by = ").AppendLine(EscapeIniValue(createdBy));
    sb.Append("parse_status = ").AppendLine(parseStatus);
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string EscapeIniValue(string s) {
    if (string.IsNullOrEmpty(s)) return string.Empty;
    var sb = new StringBuilder(s.Length);
    foreach (var c in s) {
      switch (c) {
        case '\\': sb.Append("\\\\"); break;
        case '"': sb.Append("\\\""); break;
        case '\r': sb.Append("\\r"); break;
        case '\n': sb.Append("\\n"); break;
        default: sb.Append(c); break;
      }
    }
    return sb.ToString();
  }
}
