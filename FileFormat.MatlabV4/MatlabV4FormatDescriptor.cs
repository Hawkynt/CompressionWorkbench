#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.MatlabV4;

/// <summary>
/// MATLAB MAT v4 (pre-1996, <c>.mat</c>) read-only pseudo-archive. Walks the fixed 20-byte
/// per-record headers and extracts variable name, type, and shape. Surfaces <c>FULL.mat</c>
/// plus <c>metadata.ini</c>. Numeric data extraction is intentionally out of scope.
///
/// MAT v4 has no global magic; detection is by extension. The MAT v5 descriptor's "MATLAB"
/// magic claims v5 files first, leaving extension-only fallback to capture v4 files.
/// </summary>
public sealed class MatlabV4FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "MatlabV4";
  public string DisplayName => "MATLAB MAT v4";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".mat";
  public IReadOnlyList<string> Extensions => [".mat"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("matlab-v4", "MATLAB v4")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "MATLAB MAT v4 file (read-only pseudo-archive)";

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
    string endian;
    IReadOnlyList<MatlabV4VariableInfo> variables;
    string parseStatus;
    try {
      stream.Seek(0, SeekOrigin.Begin);
      var reader = new MatlabV4Reader(stream);
      endian = MatlabV4Constants.MachineName(reader.Machine);
      variables = reader.Variables;
      parseStatus = reader.ParseStatus;
    } catch (InvalidDataException) {
      // Reader rejected the file outright — surface a partial-status metadata block rather than throwing.
      endian = "unknown";
      variables = [];
      parseStatus = "partial";
    } finally {
      stream.Seek(origin, SeekOrigin.Begin);
    }

    var sb = new StringBuilder();
    sb.AppendLine("[matlab_v4]");
    sb.Append("endian = ").AppendLine(endian);
    sb.Append("variable_count = ").AppendLine(variables.Count.ToString(CultureInfo.InvariantCulture));
    sb.Append("variables = ").AppendLine(BuildVariableList(variables));
    sb.Append("parse_status = ").AppendLine(parseStatus);
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string BuildVariableList(IReadOnlyList<MatlabV4VariableInfo> variables) {
    if (variables.Count == 0) return string.Empty;
    var parts = new List<string>(variables.Count);
    foreach (var v in variables) {
      if (string.IsNullOrEmpty(v.Name)) continue;
      if (v.Name.IndexOfAny([';', '=', '\r', '\n', '[', ']']) >= 0) continue;
      var dims = "[" + v.Rows.ToString(CultureInfo.InvariantCulture) + "," + v.Cols.ToString(CultureInfo.InvariantCulture) + "]";
      parts.Add(v.Name + ":" + v.TypeName + ":" + dims);
    }
    return string.Join("; ", parts);
  }
}
