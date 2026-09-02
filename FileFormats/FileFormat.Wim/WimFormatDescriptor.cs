#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Wim;

/// <summary>
/// Windows Imaging Format (WIM) — file-based disk image with single-instance resource storage.
///
/// References:
/// <list type="bullet">
///   <item><description>Microsoft, "Windows Imaging File Format (WIM)" white paper — the vendor format description</description></item>
///   <item><description><c>https://wimlib.net/</c> — wimlib — open implementation with detailed format documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Windows_Imaging_Format</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class WimFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => throw new NotSupportedException(
      "WIM defragmentation is not supported — XML metadata resource references SHA-1 hashes of " +
      "compressed resource bytes; a rebuild would change those references and break the image.");
  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) => this.Defragment(archive);


  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    if (archive.Length < WimConstants.HeaderSize)
      yield break;
    yield return new DefragBlockInfo(0, WimConstants.HeaderSize, DefragBlockKind.MetadataReserved, FileName: "WIM Header");
    WimReader r;
    try {
      archive.Position = 0;
      r = new WimReader(archive);
    } catch {
      yield break;
    }
    foreach (var res in r.Resources) {
      if (res.CompressedSize <= 0 || res.Offset < 0) continue;
      var kind = res.IsMetadata ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used;
      var label = res.IsMetadata ? "Metadata Resource" : "Data Resource";
      yield return new DefragBlockInfo(res.Offset, res.CompressedSize, kind, FileName: label);
    }
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Wim";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "WIM";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".wim";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".wim", ".swm", ".esd"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'M', (byte)'S', (byte)'W', (byte)'I', (byte)'M', 0x00, 0x00, 0x00], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("wim", "WIM")];
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
public string Description => "Windows Imaging Format, file-based disk image";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new WimReader(stream);
    var namedFiles = r.GetNamedFiles();

    if (namedFiles.Count > 0) {
      return namedFiles.Select((f, i) => new ArchiveEntryInfo(i, f.FileName, f.FileSize,
        f.ResourceIndex >= 0 ? r.Resources[f.ResourceIndex].CompressedSize : 0,
        r.Header.CompressionType != WimConstants.CompressionNone ? "Compressed" : "Store",
        false, false, null)).ToList();
    }

    // Fallback: no metadata — list raw resources.
    return r.Resources
      .Where(e => !e.IsMetadata)
      .Select((e, i) => new ArchiveEntryInfo(i, $"resource_{i}", e.OriginalSize, e.CompressedSize,
        e.IsCompressed ? "Compressed" : "Store", false, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new WimReader(stream);
    var namedFiles = r.GetNamedFiles();

    if (namedFiles.Count > 0) {
      foreach (var f in namedFiles) {
        if (files != null && !MatchesFilter(f.FileName, files)) continue;
        // A file with no resource is an empty one — it still has to appear.
        WriteFile(outputDir, f.FileName, f.ResourceIndex < 0 ? [] : r.ReadResource(f.ResourceIndex));
      }
      return;
    }

    // Fallback: no metadata — extract raw resources.
    var dataIndex = 0;
    for (var i = 0; i < r.Resources.Count; ++i) {
      if (r.Resources[i].IsMetadata) continue;
      var name = $"resource_{dataIndex}";
      if (files != null && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, r.ReadResource(i));
      dataIndex++;
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var files = FormatHelpers.FilesOnly(inputs).ToList();
    var w = new WimWriter(output);
    w.Write(files);
  }

  /// <summary>
  /// Opens a single WIM resource as a bounded read-only <see cref="Stream"/>.
  /// Resolves <paramref name="entryName"/> to a resource index through the
  /// named-files metadata, then wraps the decompressed bytes in a
  /// <see cref="BoundedEntryStream"/> sized to the file size.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new WimReader(archive);
    var namedFiles = r.GetNamedFiles();
    if (namedFiles.Count > 0) {
      foreach (var f in namedFiles) {
        if (!string.Equals(f.FileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
        var bytes = f.ResourceIndex < 0 ? [] : r.ReadResource(f.ResourceIndex);
        return new BoundedEntryStream(new MemoryStream(bytes, writable: false),
          bytes.Length, leaveOpen: false);
      }
    } else {
      // Fallback: synthetic resource_N names for archives without metadata
      var dataIndex = 0;
      for (var i = 0; i < r.Resources.Count; ++i) {
        if (r.Resources[i].IsMetadata) continue;
        var name = $"resource_{dataIndex}";
        if (string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase)) {
          var bytes = r.ReadResource(i);
          return new BoundedEntryStream(new MemoryStream(bytes, writable: false),
            bytes.Length, leaveOpen: false);
        }
        ++dataIndex;
      }
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false),
      0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }
}
