#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Iceberg;

/// <summary>
/// Apache Iceberg table metadata.json read-only pseudo-archive. Iceberg is a table format
/// that lives across multiple files; we surface the supplied metadata.json as
/// <c>FULL.json</c> plus an INI summary of the parsed fields.
/// Detection is by JSON content sniffing (no extension or magic bytes).
/// </summary>
public sealed class IcebergFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Iceberg";
  public string DisplayName => "Apache Iceberg metadata";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".json";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("iceberg", "Iceberg metadata")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apache Iceberg table metadata (read-only pseudo-archive)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var fileSize = stream.Length;
    var meta = BuildMetadataIni(stream);
    return [
      new ArchiveEntryInfo(0, "FULL.json", fileSize, -1, "Stored", false, false, null),
      new ArchiveEntryInfo(1, "metadata.ini", meta.Length, -1, "Stored", false, false, null),
    ];
  }

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
    var fileSize = stream.Length;

    var sb = new StringBuilder();
    sb.AppendLine("[iceberg]");
    sb.Append("format_version = ").AppendLine(reader.FormatVersion.ToString(CultureInfo.InvariantCulture));
    sb.Append("table_uuid = ").AppendLine(reader.TableUuid);
    sb.Append("location = ").AppendLine(reader.Location);
    sb.Append("last_updated_ms = ").AppendLine(reader.LastUpdatedMs.ToString(CultureInfo.InvariantCulture));
    sb.Append("last_column_id = ").AppendLine(reader.LastColumnId.ToString(CultureInfo.InvariantCulture));
    sb.Append("current_schema_id = ").AppendLine(reader.CurrentSchemaId.ToString(CultureInfo.InvariantCulture));
    sb.Append("current_snapshot_id = ").AppendLine(reader.CurrentSnapshotId.ToString(CultureInfo.InvariantCulture));
    sb.Append("snapshot_count = ").AppendLine(reader.SnapshotCount.ToString(CultureInfo.InvariantCulture));
    sb.Append("partition_spec_count = ").AppendLine(reader.PartitionSpecCount.ToString(CultureInfo.InvariantCulture));
    sb.Append("sort_order_count = ").AppendLine(reader.SortOrderCount.ToString(CultureInfo.InvariantCulture));
    sb.Append("schema_columns = ").AppendLine(string.Join(",", reader.SchemaColumns));
    sb.Append("file_size = ").AppendLine(fileSize.ToString(CultureInfo.InvariantCulture));
    sb.Append("parse_status = ").AppendLine(reader.ParseStatus);
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static IcebergReader ReadMetadata(Stream stream) {
    var origin = stream.Position;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      return new IcebergReader(stream);
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }
  }
}
