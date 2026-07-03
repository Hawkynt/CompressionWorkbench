#pragma warning disable CS1591
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

  public string Id => "PyInstaller";
  public string DisplayName => "PyInstaller";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".exe";

  // The MEI cookie sits near EOF (after the PE image), so detection is handled by
  // the installer scan rather than a start-of-file magic or a shared ".exe"
  // extension claim.
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("pyinstaller", "PyInstaller CArchive")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PyInstaller onefile executable archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var reader = new PyInstallerReader(stream);
    var toc = reader.ReadToc();
    var result = new List<ArchiveEntryInfo>(toc.Count);
    var index = 0;

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

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = new PyInstallerReader(stream);
    var index = 0;

    foreach (var entry in reader.ReadToc()) {
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
}
