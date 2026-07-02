#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Epub;

/// <summary>
/// EPUB e-book — ZIP-based OCF container with a mimetype entry, META-INF/container.xml and the OPF package document.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.w3.org/TR/epub-33/</c> — EPUB 3.3 — W3C Recommendation (incl. the OCF container)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/EPUB</c> — format overview</description></item>
/// </list>
/// </summary>
public sealed class EpubFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => FileFormat.Zip.ZipLayoutMap.Enumerate(archive);

  /// <summary>Rebuild-based defrag delegating to ZIP (EPUB is a ZIP variant).</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag delegating to ZIP (EPUB is a ZIP variant).</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new FileFormat.Zip.ZipReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.FileName, r.ExtractEntry(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new FileFormat.Zip.ZipWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddEntry(n, d);
          w.Finish();
        }
        return ms.ToArray();
      });
  }

  public string Id => "Epub";
  public string DisplayName => "EPUB";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing EPUB archive. Routes to
  /// <see cref="FileFormat.Zip.ZipModifier"/> for true random-access I/O — only
  /// the central directory, EOCD, and the appended file's local file header +
  /// compressed data are read or written. Pre-existing entry LFH + payload
  /// bytes at original offsets remain byte-identical.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      FileFormat.Zip.ZipModifier.RemoveFile(archive, name, wipeData: true);
      FileFormat.Zip.ZipModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes named entries; uses <see cref="FileFormat.Zip.ZipModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      FileFormat.Zip.ZipModifier.RemoveFile(archive, name, wipeData: true);
  }
  public string DefaultExtension => ".epub";
  public IReadOnlyList<string> Extensions => [".epub"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Electronic publication e-book (ZIP-based)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FileFormat.Zip.ZipReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.UncompressedSize, e.CompressedSize,
      e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FileFormat.Zip.ZipReader(stream, password: password);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.FileName)); continue; }
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. Delegates to the
  /// underlying ZIP reader and wraps the decoded byte buffer in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized to
  /// the entry's uncompressed length, so block padding and adjacent entries
  /// are physically unreachable through the returned view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new FileFormat.Zip.ZipReader(archive, leaveOpen: true, password: password);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.ExtractEntry(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new FileFormat.Zip.ZipWriter(output, leaveOpen: true);
    foreach (var i in inputs) {
      if (i.IsDirectory) { w.AddDirectory(i.ArchiveName); continue; }
      w.AddEntry(i.ArchiveName, i.ReadContent());
    }
  }
}
