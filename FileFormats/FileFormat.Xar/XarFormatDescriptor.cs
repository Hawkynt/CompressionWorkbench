#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Xar;

/// <summary>
/// eXtensible ARchive (XAR) — gzip-compressed XML table of contents + heap; used by Apple installer packages.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/mackyle/xar</c> — maintained xar sources (format documentation in the repository)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Xar_(archiver)</c> — Wikipedia overview</description></item>
///   <item><description>originally released as an OpenDarwin/Apple open-source project</description></item>
/// </list>
/// </summary>
public sealed class XarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the XAR archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the XAR archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new XarReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FileName, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new XarWriter(ms)) {
          foreach (var (n, d) in files) w.AddFile(n, d);
        }
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new XarReader(archive);
    foreach (var e in r.Entries) {
      if (e.CompressedSize > 0) {
        var absOffset = r.HeapStart + e.HeapOffset;
        yield return new DefragBlockInfo(absOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.FileName);
      }
    }
    // XAR header + TOC region
    if (r.HeapStart > 0)
      yield return new DefragBlockInfo(0, r.HeapStart, DefragBlockKind.MetadataReserved, FileName: "XAR Header + TOC");
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Xar";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "XAR";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".xar";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".xar"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'x', (byte)'a', (byte)'r', (byte)'!'], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("xar", "XAR")];
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
public string Description => "eXtensible ARchive format (Apple pkg)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new XarReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.Method, e.IsDirectory, false, e.LastModified)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new XarReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // leaveOpen: true — caller owns the stream (e.g. AtomicFileWriter flushes
    // it to disk after we return; closing it here would break that contract).
    using var w = new XarWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing XAR archive. Uses
  /// <see cref="XarModifier"/> for true random-access I/O — only the header,
  /// the compressed XML TOC, the new entry's heap bytes, and (when the TOC
  /// changes size) the heap-shift delta are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FlatFiles(inputs)) {
      XarModifier.RemoveFile(archive, name, wipeData: true);
      XarModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes named entries from an existing XAR archive. Uses
  /// <see cref="XarModifier"/> for random-access I/O on the TOC.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      XarModifier.RemoveFile(archive, name, wipeData: true);
  }
}
