#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.BitRock;

/// <summary>
/// Format descriptor for BitRock / InstallBuilder self-extracting installers.
/// Detection is content-based (the end magic and mk4vfs schema near EOF), so it
/// works regardless of file extension.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://installbuilder.com</c> — InstallBuilder (formerly BitRock), the tool that produces these installers; the container layout is undocumented by the vendor</description></item>
///   <item><description>Jean-Claude Wippler's Metakit (Mk4) file format — the tclkit runtime VFS embedded in the installer stub, reverse-read here as the <c>mk4vfs</c> catalog</description></item>
///   <item><description>Tcl cookfs archive format — the content region holding the gzip-tar payload components</description></item>
/// </list>
/// </summary>
public sealed class BitRockFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "BitRock";
  public string DisplayName => "BitRock / InstallBuilder";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".exe";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];

  // The identifying bytes live at EOF, not at a fixed start offset, so detection
  // is performed by the installer tail scan in FormatDetector. No start-magic.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("zlib", "zlib/deflate"), new("gzip", "gzip/deflate")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "BitRock/InstallBuilder installer (tclkit runtime VFS + gzip-tar payload)";

  // Entries are namespaced so callers can tell the tclkit runtime apart from the real deliverable:
  //   runtime/…                       — files from the embedded Metakit (Mk4) VFS
  //   payload/<component.tar>/…        — application files recovered from a content-region tar
  private const string RuntimePrefix = "runtime/";
  private const string PayloadPrefix = "payload/";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var reader = BitRockReader.Open(stream);
    var result = new List<ArchiveEntryInfo>();
    var index = 0;

    foreach (var dir in reader.DirectoryPaths) {
      if (string.IsNullOrEmpty(dir))
        continue;
      result.Add(new ArchiveEntryInfo(index++, RuntimePrefix + dir + "/", 0, 0, "store", true, false, null, "dir"));
    }

    foreach (var file in reader.Files)
      result.Add(new ArchiveEntryInfo(index++, RuntimePrefix + file.Name, file.Content.Length,
        file.Content.Length, "zlib", false, false, null, "file"));

    // Reconstruct the cookfs content region once, then stream each gzip-tar component's entries.
    var tmp = BitRockContentScanner.ReconstructContent(stream, reader.VfsStart);
    if (tmp != null) {
      try {
        using var content = File.OpenRead(tmp);
        foreach (var component in BitRockContentScanner.ScanMembers(content)) {
          var root = PayloadPrefix + component.Name + "/";
          result.Add(new ArchiveEntryInfo(index++, root, 0, component.Length, "gzip", true, false, null, "dir"));
          foreach (var (path, size, isDir) in BitRockContentScanner.EnumerateComponent(content, component))
            result.Add(new ArchiveEntryInfo(index++, root + path, isDir ? 0 : size, isDir ? 0 : size,
              "gzip", isDir, false, null, isDir ? "dir" : "file"));
        }
      } finally {
        try { File.Delete(tmp); } catch { /* best-effort */ }
      }
    }

    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = BitRockReader.Open(stream);

    foreach (var file in reader.Files) {
      var name = RuntimePrefix + file.Name;
      if (files == null || MatchesFilter(name, files))
        WriteFile(outputDir, name, file.Content);
    }

    // payload/<component.tar>/… — reconstruct the cookfs content once, then stream every application
    // file straight to disk (bounded memory; the reconstructed content lives on disk, not in RAM).
    var tmp = BitRockContentScanner.ReconstructContent(stream, reader.VfsStart);
    if (tmp == null)
      return;
    try {
      using var content = File.OpenRead(tmp);
      foreach (var component in BitRockContentScanner.ScanMembers(content)) {
        var root = PayloadPrefix + component.Name;
        var componentDir = Path.Combine(outputDir, root.Replace('/', Path.DirectorySeparatorChar));
        BitRockContentScanner.ExtractComponentToDisk(content, component, componentDir,
          files == null ? null : path => MatchesFilter(root + "/" + path, files));
      }
    } finally {
      try { File.Delete(tmp); } catch { /* best-effort */ }
    }
  }
}
