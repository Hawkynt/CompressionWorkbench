#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Orc;

/// <summary>
/// Apache ORC (<c>.orc</c>) read-only pseudo-archive. Validates the leading "ORC" magic, reads
/// the 1-byte PostScript-length trailer, walks the uncompressed PostScript Protobuf to extract
/// the compression codec, footer length and writer version, and (when compression is NONE) walks
/// the Footer Protobuf to surface row count, type count and stripe count. Surfaces a
/// <c>FULL.orc</c> passthrough plus a <c>metadata.ini</c> summary. Stripe-level decompression
/// and full record decoding are intentionally out of scope.
/// </summary>
public sealed class OrcFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Orc";
  public string DisplayName => "Apache ORC";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".orc";
  public IReadOnlyList<string> Extensions => [".orc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ORC"u8.ToArray(), Offset: 0, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("orc", "ORC")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apache ORC columnar (read-only pseudo-archive)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var fileSize = stream.Length;
    var meta = BuildMetadataIni(stream);
    return [
      new ArchiveEntryInfo(0, "FULL.orc", fileSize, -1, "Stored", false, false, null),
      new ArchiveEntryInfo(1, "metadata.ini", meta.Length, -1, "Stored", false, false, null),
    ];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);

    if (files == null || files.Length == 0 || MatchesFilter("FULL.orc", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.orc");
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
    bool magicOk;
    int psLength;
    long footerLength;
    string compression;
    string writerVersion;
    long numberOfRows;
    int stripeCount;
    int typeCount;
    string parseStatus;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var reader = new OrcReader(stream);
      magicOk = reader.MagicOk;
      psLength = reader.PsLength;
      footerLength = reader.FooterLength;
      compression = reader.Compression;
      writerVersion = reader.WriterVersion;
      numberOfRows = reader.NumberOfRows;
      stripeCount = reader.StripeCount;
      typeCount = reader.TypeCount;
      parseStatus = reader.ParseStatus;
    } catch (InvalidDataException) {
      magicOk = false;
      psLength = 0;
      footerLength = 0;
      compression = "unknown";
      writerVersion = string.Empty;
      numberOfRows = 0;
      stripeCount = 0;
      typeCount = 0;
      parseStatus = "partial";
    } catch (EndOfStreamException) {
      magicOk = false;
      psLength = 0;
      footerLength = 0;
      compression = "unknown";
      writerVersion = string.Empty;
      numberOfRows = 0;
      stripeCount = 0;
      typeCount = 0;
      parseStatus = "partial";
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }

    var sb = new StringBuilder();
    sb.AppendLine("[orc]");
    sb.Append("magic_ok = ").AppendLine(magicOk ? "true" : "false");
    sb.Append("ps_length = ").AppendLine(psLength.ToString(CultureInfo.InvariantCulture));
    sb.Append("footer_length = ").AppendLine(footerLength.ToString(CultureInfo.InvariantCulture));
    sb.Append("compression = ").AppendLine(compression);
    sb.Append("writer_version = ").AppendLine(EscapeIniValue(writerVersion));
    sb.Append("number_of_rows = ").AppendLine(numberOfRows.ToString(CultureInfo.InvariantCulture));
    sb.Append("stripe_count = ").AppendLine(stripeCount.ToString(CultureInfo.InvariantCulture));
    sb.Append("type_count = ").AppendLine(typeCount.ToString(CultureInfo.InvariantCulture));
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
