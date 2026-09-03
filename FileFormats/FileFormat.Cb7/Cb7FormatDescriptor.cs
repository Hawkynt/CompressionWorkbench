#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.SevenZip;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cb7;

/// <summary>
/// Comic book archive — a 7-Zip container of sequentially named page images, conventionally suffixed .cb7.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/Comic_book_archive</c> — the .cb7/.cbz/.cbr naming convention</description></item>
///   <item><description><c>https://www.7-zip.org/7z.html</c> — official 7z format page (Igor Pavlov) — the underlying container format</description></item>
///   <item><description><c>https://py7zr.readthedocs.io/en/latest/archive_format.html</c> — a community 7z structural reference</description></item>
/// </list>
/// </summary>
public sealed class Cb7FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty {

  /// <summary>
  /// Adds new pages directly through the 7z changed-byte append path. The
  /// underlying writer validates collisions and unsupported layouts and serializes
  /// replacement metadata before its first archive write; same-name replacement or
  /// unsupported profiles therefore fall back to verified rebuild without an
  /// O(total bytes) transaction snapshot around pure additions.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    var newFiles = new List<(string Name, byte[] Data, bool IsDirectory)>();
    foreach (var input in inputs) {
      if (string.IsNullOrEmpty(input.ArchiveName)) continue;
      newFiles.Add((input.ArchiveName, input.IsDirectory ? [] : input.ReadContent(), input.IsDirectory));
    }

    if (newFiles.Count > 0) {
      try {
        archive.Position = 0;
        SevenZipInPlaceAdder.Add(archive, newFiles);
        return;
      } catch (NotSupportedException) {
        if (archive.CanSeek)
          archive.Position = 0;
      }
    }

    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var input in inputs) {
        if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
        var dest = Path.Combine(tmpDir, input.ArchiveName.Replace('/', Path.DirectorySeparatorChar));
        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        File.WriteAllBytes(dest, input.ReadContent());
      }
    });
  }

  /// <summary>
  /// Removes pages directly when they comprise complete 7z solid folders (or
  /// empty-stream metadata). Unsupported partial-solid removals and non-trivial
  /// layouts are rejected before compaction and use the verified rebuild path.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);

    try {
      archive.Position = 0;
      SevenZipInPlaceRemover.Remove(archive, entryNames);
      return;
    } catch (NotSupportedException) {
      if (archive.CanSeek)
        archive.Position = 0;
    }

    var skip = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        if (skip.Contains(rel) || skip.Contains(Path.GetFileName(rel)))
          File.Delete(file);
      }
    });
  }

  /// <summary>Rebuild-based defrag: extracts then re-creates the 7z archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag delegating to 7z (CB7 is a 7z variant).</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new SevenZipReader(stream);
        var list = new List<(string, byte[])>();
        for (var i = 0; i < r.Entries.Count; ++i) {
          var e = r.Entries[i];
          if (e.IsDirectory) continue;
          list.Add((e.Name, r.Extract(i)));
        }
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var w = new SevenZipWriter(ms, SevenZipCodec.Lzma2);
        foreach (var (n, d) in files)
          w.AddEntry(new SevenZipEntry { Name = n, Size = d.Length }, d);
        w.Finish();
        return ms.ToArray();
      });
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => SevenZipLayoutMap.Enumerate(archive);

  /// <summary>
  /// Zeros every dead byte in the archive: gaps between packed solid blocks and any
  /// junk before the compressed metadata or trailing the file. The signature header,
  /// solid blocks and end-of-archive metadata are live and preserved. Cluster-tip
  /// wiping is N/A (7z packs solid blocks with no per-file slack).
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = SevenZipLayoutMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Cb7";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "CB7";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable comic-book archive (7z variant). Supported plain-header edits
  // go straight through the native 7z block editors; unsupported profiles rebuild.
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories | FormatCapabilities.SupportsPassword;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".cb7";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".cb7"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // No magic: a .cb7 is byte-for-byte a 7z archive, so a content scan must resolve to
  // SevenZip (the real format). Cb7 is identified by extension only, exactly as the
  // sibling comic wrappers Cbr (RAR) and Cbz (ZIP) declare no signature.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzma2", "LZMA2")];
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
  public string Description => "Comic book 7-Zip archive";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SevenZipReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      string.IsNullOrEmpty(e.Method) ? "7z" : e.Method, e.IsDirectory, false, e.LastWriteTime)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SevenZipReader(stream, password: password);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      WriteFile(outputDir, e.Name, r.Extract(i));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var password = !string.IsNullOrEmpty(options.Password) ? options.Password : null;
    var w = new SevenZipWriter(output, SevenZipCodec.Lzma2, password: password);
    foreach (var i in inputs)
      if (i.IsDirectory) w.AddDirectory(i.ArchiveName);
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var data = i.ReadContent();
      w.AddEntry(new SevenZipEntry { Name = i.ArchiveName, Size = data.Length }, data);
    }
    w.Finish();
  }
}
