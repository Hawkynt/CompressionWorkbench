#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Arrow;

/// <summary>
/// Apache Arrow IPC (<c>.arrow</c>, <c>.feather</c>) read-only pseudo-archive.
/// Detects File vs Streaming variants via the leading <c>"ARROW1\0\0"</c> magic, walks the
/// FlatBuffers Message envelopes to count messages and record batches, and harvests an
/// approximate schema by string-scanning the Schema message blob. Surfaces a
/// <c>FULL.arrow</c> passthrough plus a <c>metadata.ini</c> summary. Record-batch buffer
/// decoding is out of scope.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://arrow.apache.org/docs/format/Columnar.html</c> — Arrow columnar format + IPC (File/Streaming) specification</description></item>
///   <item><description><c>https://github.com/apache/arrow</c> — canonical implementation</description></item>
/// </list>
/// </summary>
public sealed class ArrowFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Arrow";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Apache Arrow IPC";
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
  public string DefaultExtension => ".arrow";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".arrow", ".feather"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(ArrowConstants.Magic, Offset: 0, Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("arrow-ipc", "Arrow IPC")];
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
  public string Description => "Apache Arrow IPC (read-only pseudo-archive)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var fileSize = stream.Length;
    var meta = BuildMetadataIni(stream);
    return [
      new ArchiveEntryInfo(0, "FULL.arrow", fileSize, -1, "Stored", false, false, null),
      new ArchiveEntryInfo(1, "metadata.ini", meta.Length, -1, "Stored", false, false, null),
    ];
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);

    if (files == null || files.Length == 0 || MatchesFilter("FULL.arrow", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.arrow");
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
    string format;
    int messageCount;
    int recordBatchCount;
    IReadOnlyList<string> schema;
    string parseStatus;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var reader = new ArrowReader(stream);
      format = reader.Format;
      messageCount = reader.MessageCount;
      recordBatchCount = reader.RecordBatchCount;
      schema = reader.ApproximateSchema;
      parseStatus = reader.ParseStatus;
    } catch (InvalidDataException) {
      format = "unknown";
      messageCount = 0;
      recordBatchCount = 0;
      schema = [];
      parseStatus = "partial";
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }

    var sb = new StringBuilder();
    sb.AppendLine("[arrow]");
    sb.Append("format = ").AppendLine(format);
    sb.Append("message_count = ").AppendLine(messageCount.ToString(CultureInfo.InvariantCulture));
    sb.Append("record_batch_count = ").AppendLine(recordBatchCount.ToString(CultureInfo.InvariantCulture));
    sb.Append("approximate_schema = ").AppendLine(string.Join(", ", schema));
    sb.Append("parse_status = ").AppendLine(parseStatus);
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
