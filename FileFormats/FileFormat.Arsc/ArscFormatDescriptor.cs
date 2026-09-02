#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Arsc;

/// <summary>
/// Android compiled resource table (<c>resources.arsc</c>) read-only pseudo-archive.
/// Validates the root <c>RES_TABLE_TYPE</c> chunk, walks the global string pool and each
/// <c>RES_TABLE_PACKAGE_TYPE</c> chunk, and surfaces a <c>FULL.arsc</c> passthrough plus a
/// <c>metadata.ini</c> summary (package count, package id/name list, total type-chunk count,
/// global string count). All multi-byte integers are little-endian.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://android.googlesource.com/platform/frameworks/base/</c> — AOSP frameworks/base — <c>libs/androidfw/include/androidfw/ResourceTypes.h</c> defines the RES_TABLE chunk structs</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Apk_(file_format)</c> — background (resources.arsc inside APKs)</description></item>
/// </list>
/// </summary>
public sealed class ArscFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Arsc";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Android Resources (ARSC)";
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
  public string DefaultExtension => ".arsc";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".arsc"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(ArscConstants.ResTableMagic, Offset: 0, Confidence: 0.85),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("arsc", "ARSC")];
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
  public string Description => "Android compiled resource table (read-only pseudo-archive)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var fileSize = stream.Length;
    var meta = BuildMetadataIni(stream);
    return [
      new ArchiveEntryInfo(0, "FULL.arsc", fileSize, -1, "Stored", false, false, null),
      new ArchiveEntryInfo(1, "metadata.ini", meta.Length, -1, "Stored", false, false, null),
    ];
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);

    if (files == null || files.Length == 0 || MatchesFilter("FULL.arsc", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.arsc");
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
    uint packageCount;
    uint globalStringCount;
    int typeCount;
    string packagesStr;
    string parseStatus;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var reader = new ArscReader(stream);
      packageCount = reader.PackageCount;
      globalStringCount = reader.GlobalStringCount;
      typeCount = reader.TotalTypeCount;
      packagesStr = FormatPackages(reader.Packages);
      parseStatus = reader.ParseStatus;
    } catch (InvalidDataException) {
      packageCount = 0;
      globalStringCount = 0;
      typeCount = 0;
      packagesStr = string.Empty;
      parseStatus = "partial";
    } catch (EndOfStreamException) {
      packageCount = 0;
      globalStringCount = 0;
      typeCount = 0;
      packagesStr = string.Empty;
      parseStatus = "partial";
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }

    var sb = new StringBuilder();
    sb.AppendLine("[arsc]");
    sb.Append("package_count = ").AppendLine(packageCount.ToString(CultureInfo.InvariantCulture));
    sb.Append("global_string_count = ").AppendLine(globalStringCount.ToString(CultureInfo.InvariantCulture));
    sb.Append("packages = ").AppendLine(EscapeIniValue(packagesStr));
    sb.Append("type_count = ").AppendLine(typeCount.ToString(CultureInfo.InvariantCulture));
    sb.Append("parse_status = ").AppendLine(parseStatus);
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string FormatPackages(IReadOnlyList<ArscPackageInfo> pkgs) {
    if (pkgs.Count == 0) return string.Empty;
    var sb = new StringBuilder();
    for (var i = 0; i < pkgs.Count; i++) {
      if (i > 0) sb.Append("; ");
      var p = pkgs[i];
      sb.Append("pkg").Append((i + 1).ToString(CultureInfo.InvariantCulture))
        .Append(':').Append("0x").Append(p.PackageId.ToString("X", CultureInfo.InvariantCulture))
        .Append(':').Append(p.Name);
    }
    return sb.ToString();
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
