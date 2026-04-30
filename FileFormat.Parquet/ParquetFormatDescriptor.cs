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
/// </summary>
public sealed class ParquetFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Parquet";
  public string DisplayName => "Apache Parquet";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".parquet";
  public IReadOnlyList<string> Extensions => [".parquet"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("PAR1"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("parquet", "Parquet")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apache Parquet columnar (read-only pseudo-archive)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var fileSize = stream.Length;
    var meta = BuildMetadataIni(stream);
    return [
      new ArchiveEntryInfo(0, "FULL.parquet", fileSize, -1, "Stored", false, false, null),
      new ArchiveEntryInfo(1, "metadata.ini", meta.Length, -1, "Stored", false, false, null),
    ];
  }

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
