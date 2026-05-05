#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Matlab;

/// <summary>
/// MATLAB MAT v5 (<c>.mat</c>) read-only pseudo-archive. Parses the 128-byte header
/// and walks top-level data elements (including zlib-wrapped miCOMPRESSED elements),
/// surfacing each top-level array's name, class, and shape. Surfaces <c>FULL.mat</c>
/// plus <c>metadata.ini</c>. Numeric data extraction is out of scope.
/// </summary>
public sealed class MatlabFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Matlab";
  public string DisplayName => "MATLAB MAT v5";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mat";
  public IReadOnlyList<string> Extensions => [".mat"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(MatlabConstants.Magic, Offset: 0, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("matlab-v5", "MATLAB v5")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "MATLAB MAT v5 file (read-only pseudo-archive)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var fileSize = stream.Length;
    var meta = BuildMetadataIni(stream);
    return [
      new ArchiveEntryInfo(0, "FULL.mat", fileSize, -1, "Stored", false, false, null),
      new ArchiveEntryInfo(1, "metadata.ini", meta.Length, -1, "Stored", false, false, null),
    ];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);

    if (files == null || files.Length == 0 || MatchesFilter("FULL.mat", files)) {
      stream.Seek(0, SeekOrigin.Begin);
      var fullPath = Path.Combine(outputDir, "FULL.mat");
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
    string description;
    int version;
    bool isLittleEndian;
    IReadOnlyList<MatlabArrayInfo> arrays;
    string parseStatus;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var reader = new MatlabReader(stream);
      description = reader.Description;
      version = reader.Version;
      isLittleEndian = reader.IsLittleEndian;
      arrays = reader.Arrays;
      parseStatus = reader.ParseStatus;
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }

    var sb = new StringBuilder();
    sb.AppendLine("[matlab]");
    sb.Append("description = ").AppendLine(EscapeIniValue(description));
    sb.Append("version = ").AppendLine(version.ToString(CultureInfo.InvariantCulture));
    sb.Append("endian = ").AppendLine(isLittleEndian ? "LE" : "BE");
    sb.Append("array_count = ").AppendLine(arrays.Count.ToString(CultureInfo.InvariantCulture));
    sb.Append("arrays = ").AppendLine(BuildArrayList(arrays));
    sb.Append("parse_status = ").AppendLine(parseStatus);
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string BuildArrayList(IReadOnlyList<MatlabArrayInfo> arrays) {
    if (arrays.Count == 0) return string.Empty;
    var parts = new List<string>(arrays.Count);
    foreach (var a in arrays) {
      // Skip arrays whose name would break INI syntax — we record only the safe ones.
      if (string.IsNullOrEmpty(a.Name)) continue;
      if (a.Name.IndexOfAny([';', '=', '\r', '\n', '[', ']']) >= 0) continue;
      var dims = "[" + string.Join(",", a.Dimensions.Select(d => d.ToString(CultureInfo.InvariantCulture))) + "]";
      parts.Add(a.Name + ":" + a.ClassName + ":" + dims);
    }
    return string.Join("; ", parts);
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
