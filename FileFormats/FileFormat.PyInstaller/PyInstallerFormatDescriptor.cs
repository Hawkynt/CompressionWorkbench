#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.PyInstaller;

/// <summary>
/// Read-only descriptor for the PyInstaller CArchive appended to a "onefile"
/// executable. Detection is by the trailing MEI cookie (see
/// <c>FormatDetector.DetectInstaller</c>); listing/extraction is delegated to
/// <see cref="PyInstallerReader"/>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/pyinstaller/pyinstaller</c> — canonical implementation (bootloader sources define the MEI cookie and CArchive TOC)</description></item>
///   <item><description><c>https://pyinstaller.org/en/stable/advanced-topics.html</c> — official docs on the CArchive / ZlibArchive layout and the bootstrap process</description></item>
/// </list>
/// </summary>
public sealed class PyInstallerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "PyInstaller";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "PyInstaller";
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
public string DefaultExtension => ".exe";

  // The MEI cookie sits near EOF (after the PE image), so detection is handled by
  // the installer scan rather than a start-of-file magic or a shared ".exe"
  // extension claim.
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
public IReadOnlyList<FormatMethodInfo> Methods => [new("pyinstaller", "PyInstaller CArchive")];
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
public string Description => "PyInstaller onefile executable archive";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var image = ReadAll(stream);
    using var imageStream = new MemoryStream(image, writable: false);
    var reader = new PyInstallerReader(imageStream);
    var toc = reader.ReadToc();
    var metadata = BuildMetadataJson(reader, toc, image.LongLength);
    var diagnostics = BuildDiagnosticsJson(reader, toc);
    var result = new List<ArchiveEntryInfo>(toc.Count + 3) {
      new(0, "metadata.json", metadata.LongLength, metadata.LongLength, "stored", false, false, null, Kind: "metadata"),
      new(1, "diagnostics.json", diagnostics.LongLength, diagnostics.LongLength, "stored", false, false, null, Kind: "diagnostics"),
      new(2, "original_packed.bin", image.LongLength, image.LongLength, "stored", false, false, null, Kind: "original packed executable"),
    };
    var index = 3;

    foreach (var entry in toc) {
      var method = entry.IsCompressed ? "zlib" : "stored";
      result.Add(new ArchiveEntryInfo(
        index++, entry.Name, entry.UncompressedLength, entry.CompressedLength,
        method, false, false, null, Kind: TypeDescription(entry.TypeCode)));

      // Surface the modules bundled inside a PYZ as child listing entries.
      if (entry.TypeCode is not ('z' or 'Z'))
        continue;

      foreach (var module in reader.GetPyzModuleNames(entry))
        result.Add(new ArchiveEntryInfo(
          index++, entry.Name + "/" + module, 0, 0,
          "pyz", false, false, null, Kind: "PYZ module"));
    }

    return result;
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var image = ReadAll(stream);
    using var imageStream = new MemoryStream(image, writable: false);
    var reader = new PyInstallerReader(imageStream);
    var toc = reader.ReadToc();

    WriteArtifact(outputDir, "metadata.json", BuildMetadataJson(reader, toc, image.LongLength), files);
    WriteArtifact(outputDir, "diagnostics.json", BuildDiagnosticsJson(reader, toc), files);
    WriteArtifact(outputDir, "original_packed.bin", image, files);

    var index = 0;

    foreach (var entry in toc) {
      var name = EntryFileName(entry, index++);
      if (files != null && !MatchesFilter(name, files))
        continue;

      byte[] data;
      try {
        data = reader.GetData(entry);
      } catch (InvalidDataException) {
        continue; // Skip an entry we cannot inflate rather than abort the whole extract.
      }

      WriteFile(outputDir, name, data);
    }
  }

  private static byte[] ReadAll(Stream stream) {
    if (stream is MemoryStream ms)
      return ms.ToArray();
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  private static void WriteArtifact(string outputDir, string name, byte[] data, string[]? files) {
    if (files != null && !MatchesFilter(name, files))
      return;
    WriteFile(outputDir, name, data);
  }

  private static string EntryFileName(PyInstallerEntry entry, int index) {
    var name = entry.Name.Replace('\\', '/').Trim('/');
    return string.IsNullOrEmpty(name) ? $"entry_{index:D4}_{entry.TypeCode}" : name;
  }

  private static string TypeDescription(char typeCode) => typeCode switch {
    'z' or 'Z' => "PYZ archive",
    'm' => "module",
    'M' => "package module",
    's' => "pyc source",
    'b' => "binary",
    'x' => "data",
    'o' => "runtime option",
    'd' => "dependency",
    _ => "data"
  };

  private static byte[] BuildMetadataJson(PyInstallerReader reader, IReadOnlyList<PyInstallerEntry> toc, long imageSize) {
    var pyzCount = toc.Count(e => e.TypeCode is 'z' or 'Z');
    var compressedCount = toc.Count(e => e.IsCompressed);
    return Encoding.UTF8.GetBytes(
      $$"""
      {
        "packer": "pyinstaller",
        "container": "onefile-carchive",
        "capabilityLevel": "PayloadDecompressed",
        "imageSize": {{imageSize}},
        "pythonVersion": {{reader.PythonVersion}},
        "pythonLibraryName": "{{Json(reader.PythonLibraryName)}}",
        "entryCount": {{toc.Count}},
        "compressedEntryCount": {{compressedCount}},
        "pyzArchiveCount": {{pyzCount}}
      }
      """);
  }

  private static byte[] BuildDiagnosticsJson(PyInstallerReader reader, IReadOnlyList<PyInstallerEntry> toc) {
    var outputs = toc.Select(e => EntryFileName(e, 0)).Distinct(StringComparer.Ordinal).ToArray();
    var sb = new StringBuilder();
    sb.AppendLine("{");
    sb.AppendLine("  \"packer\": \"pyinstaller\",");
    sb.AppendLine("  \"container\": \"onefile-carchive\",");
    sb.AppendLine("  \"capabilityLevel\": \"PayloadDecompressed\",");
    sb.AppendLine("  \"canRebuildExecutable\": false,");
    sb.Append(CultureInfo.InvariantCulture, $"  \"pythonVersion\": {reader.PythonVersion},\n");
    sb.AppendLine("  \"warnings\": [");
    sb.AppendLine("    \"PyInstaller onefile extraction reconstructs bundled archive entries; it does not rebuild the original source project or a runnable unpacked executable.\"");
    sb.AppendLine("  ],");
    sb.AppendLine("  \"outputs\": [");
    sb.AppendLine("    \"metadata.json\",");
    sb.AppendLine("    \"diagnostics.json\",");
    sb.AppendLine("    \"original_packed.bin\"");
    foreach (var output in outputs)
      sb.Append(CultureInfo.InvariantCulture, $",\n    \"{Json(output)}\"");
    sb.AppendLine();
    sb.AppendLine("  ]");
    sb.AppendLine("}");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string Json(string value) =>
    value.Replace("\\", "\\\\", StringComparison.Ordinal)
      .Replace("\"", "\\\"", StringComparison.Ordinal)
      .Replace("\r", "\\r", StringComparison.Ordinal)
      .Replace("\n", "\\n", StringComparison.Ordinal);
}
