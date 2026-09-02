#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Avro;

/// <summary>
/// Apache Avro Object Container File (<c>.avro</c>) read-only pseudo-archive.
/// Parses the OCF header (magic, meta map, sync marker) and walks block headers to
/// surface block/record counts. Surfaces a <c>FULL.avro</c> passthrough plus a
/// <c>metadata.ini</c> summary. Schema-bound record decoding is out of scope.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://avro.apache.org/docs/current/specification/</c> — Avro specification incl. the Object Container File layout</description></item>
///   <item><description><c>https://github.com/apache/avro</c> — canonical implementation</description></item>
/// </list>
/// </summary>
public sealed class AvroFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Avro";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Apache Avro OCF";
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
  public string DefaultExtension => ".avro";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".avro"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(AvroConstants.Magic, Offset: 0, Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("avro-ocf", "Avro OCF")];
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
  public string Description => "Apache Avro Object Container File (read-only pseudo-archive)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var fileSize = stream.Length;
    var meta = BuildMetadataIni(stream);
    return [
      new ArchiveEntryInfo(0, "FULL.avro", fileSize, -1, "Stored", false, false, null),
      new ArchiveEntryInfo(1, "metadata.ini", meta.Length, -1, "Stored", false, false, null),
    ];
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);

    if (files == null || files.Length == 0 || MatchesFilter("FULL.avro", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.avro");
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
    string schema;
    string codec;
    byte[] sync;
    int blockCount;
    long recordCount;
    string parseStatus;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var reader = new AvroReader(stream);
      schema = reader.Schema;
      codec = reader.Codec;
      sync = reader.SyncMarker;
      blockCount = reader.BlockCount;
      recordCount = reader.RecordCount;
      parseStatus = reader.ParseStatus;
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }

    var sb = new StringBuilder();
    sb.AppendLine("[avro]");
    sb.AppendLine("magic = Obj\\x01");
    sb.Append("schema = ").AppendLine(EscapeIniValue(schema));
    sb.Append("codec = ").AppendLine(codec);
    sb.Append("sync_marker_hex = ").AppendLine(ToHex(sync));
    sb.Append("block_count = ").AppendLine(blockCount.ToString(CultureInfo.InvariantCulture));
    sb.Append("record_count = ").AppendLine(recordCount.ToString(CultureInfo.InvariantCulture));
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

  private static string ToHex(byte[] bytes) {
    if (bytes.Length == 0) return string.Empty;
    var sb = new StringBuilder(bytes.Length * 2);
    foreach (var b in bytes)
      sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
    return sb.ToString();
  }
}
