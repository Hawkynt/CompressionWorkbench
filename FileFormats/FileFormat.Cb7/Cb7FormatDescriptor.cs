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
  /// Adds (or replaces by name) pages inside an existing CB7 archive. Delegates to
  /// the 7z in-place editors (CB7 is a 7z variant): a pure add of new names takes
  /// the genuine byte-additive append (<see cref="SevenZipInPlaceAdder"/>) writing a
  /// fresh solid block at the old header offset, a same-name update excises the old
  /// entry first (<see cref="SevenZipInPlaceRemover"/>). Any case the in-place path
  /// cannot serve byte-additively falls back to the verified extract -> re-create
  /// rebuild.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    // Snapshot the original bytes so a failed/aborted in-place attempt never
    // leaves the caller's stream half-written.
    archive.Position = 0;
    using var original = new MemoryStream();
    archive.CopyTo(original);
    var originalBytes = original.ToArray();

    var newFiles = new List<(string Name, byte[] Data, bool IsDirectory)>();
    foreach (var input in inputs) {
      if (string.IsNullOrEmpty(input.ArchiveName)) continue;
      newFiles.Add((input.ArchiveName, input.IsDirectory ? [] : input.ReadContent(), input.IsDirectory));
    }

    // Names that already exist must be updated, not merely appended: excise the old
    // entries in place first (whole-folder removal only), then append. Any
    // non-byte-additive case throws and routes the whole operation to the rebuild.
    var existing = ExistingNames(originalBytes);
    var collisions = newFiles.Where(f => existing.Contains(f.Name)).Select(f => f.Name).ToArray();

    try {
      using var work = new MemoryStream();
      work.Write(originalBytes, 0, originalBytes.Length);
      work.Position = 0;
      if (collisions.Length > 0)
        SevenZipInPlaceRemover.Remove(work, collisions);
      SevenZipInPlaceAdder.Add(work, newFiles);

      var result = work.ToArray();
      archive.Position = 0;
      archive.Write(result, 0, result.Length);
      archive.SetLength(result.Length);
      archive.Flush();
      return;
    } catch (NotSupportedException) {
      // Restore the untouched original, then take the verified rebuild path.
      archive.Position = 0;
      archive.Write(originalBytes, 0, originalBytes.Length);
      archive.SetLength(originalBytes.Length);
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
  /// Removes the named pages. A removal that drops one or more entire solid blocks
  /// (folders) is served by the genuine in-place remover
  /// (<see cref="SevenZipInPlaceRemover"/>); anything it cannot serve byte-additively
  /// falls back to the verified extract -> re-create rebuild.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);

    archive.Position = 0;
    using var original = new MemoryStream();
    archive.CopyTo(original);
    var originalBytes = original.ToArray();

    try {
      using var work = new MemoryStream();
      work.Write(originalBytes, 0, originalBytes.Length);
      work.Position = 0;
      SevenZipInPlaceRemover.Remove(work, entryNames);

      var result = work.ToArray();
      archive.Position = 0;
      archive.Write(result, 0, result.Length);
      archive.SetLength(result.Length);
      archive.Flush();
      return;
    } catch (NotSupportedException) {
      archive.Position = 0;
      archive.Write(originalBytes, 0, originalBytes.Length);
      archive.SetLength(originalBytes.Length);
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

  /// <summary>The non-directory entry names currently present in the CB7 (7z) archive.</summary>
  private static HashSet<string> ExistingNames(byte[] archiveBytes) {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using var ms = new MemoryStream(archiveBytes, writable: false);
    using var r = new SevenZipReader(ms, leaveOpen: true);
    foreach (var e in r.Entries)
      if (!e.IsDirectory)
        names.Add(e.Name);
    return names;
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

  public string Id => "Cb7";
  public string DisplayName => "CB7";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable comic-book archive (7z variant). Add/Replace/Remove take the
  // genuine in-place 7z block editors where byte-additive, else the verified
  // extract -> edit -> re-create rebuild. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories | FormatCapabilities.SupportsPassword;
  public string DefaultExtension => ".cb7";
  public IReadOnlyList<string> Extensions => [".cb7"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // No magic: a .cb7 is byte-for-byte a 7z archive, so a content scan must resolve to
  // SevenZip (the real format). Cb7 is identified by extension only, exactly as the
  // sibling comic wrappers Cbr (RAR) and Cbz (ZIP) declare no signature.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzma2", "LZMA2")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Comic book 7-Zip archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SevenZipReader(stream, password: password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      string.IsNullOrEmpty(e.Method) ? "7z" : e.Method, e.IsDirectory, false, e.LastWriteTime)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SevenZipReader(stream, password: password);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      WriteFile(outputDir, e.Name, r.Extract(i));
    }
  }

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
