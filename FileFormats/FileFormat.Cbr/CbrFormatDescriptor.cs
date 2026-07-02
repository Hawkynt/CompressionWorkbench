#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cbr;

/// <summary>
/// Comic book archive — a RAR container of sequentially named page images, conventionally suffixed .cbr.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://en.wikipedia.org/wiki/Comic_book_archive</c> — the .cbr/.cbz naming convention</description></item>
///   <item><description><c>https://www.rarlab.com/technote.htm</c> — RAR 5.x technote — the underlying container format</description></item>
/// </list>
/// </summary>
public sealed class CbrFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IWipeEmpty {

  /// <summary>
  /// Adds (or replaces by name) pages inside an existing CBR archive. Delegates to
  /// the RAR in-place editors (CBR is a RAR variant): a pure add of new names takes
  /// the genuine byte-additive append (<see cref="FileFormat.Rar.RarInPlaceAdder"/>),
  /// a same-name update excises the old block first
  /// (<see cref="FileFormat.Rar.RarInPlaceRemover"/>). Any case the in-place path
  /// cannot serve byte-additively falls back to the verified extract -> re-create
  /// rebuild.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    archive.Position = 0;
    using var original = new MemoryStream();
    archive.CopyTo(original);
    var originalBytes = original.ToArray();

    var hasDirectory = false;
    var newFiles = new List<(string Name, byte[] Data, DateTimeOffset? ModifiedTime)>();
    foreach (var input in inputs) {
      if (string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (input.IsDirectory) { hasDirectory = true; continue; }
      newFiles.Add((input.ArchiveName, input.ReadContent(), null));
    }

    if (!hasDirectory && newFiles.Count > 0) {
      var existing = ExistingNames(originalBytes);
      var collisions = newFiles.Where(f => existing.Contains(f.Name)).Select(f => f.Name).ToArray();
      try {
        using var work = new MemoryStream();
        work.Write(originalBytes, 0, originalBytes.Length);
        work.Position = 0;
        if (collisions.Length > 0)
          FileFormat.Rar.RarInPlaceRemover.Remove(work, collisions);
        FileFormat.Rar.RarInPlaceAdder.Add(work, newFiles);

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
  /// Removes the named pages. Non-solid FILE blocks are excised by the genuine
  /// in-place remover (<see cref="FileFormat.Rar.RarInPlaceRemover"/>); anything it
  /// cannot serve byte-additively falls back to the verified extract -> re-create
  /// rebuild.
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
      FileFormat.Rar.RarInPlaceRemover.Remove(work, entryNames);

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

  private static HashSet<string> ExistingNames(byte[] archiveBytes) {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try {
      using var ms = new MemoryStream(archiveBytes, writable: false);
      var r = new FileFormat.Rar.RarReader(ms);
      foreach (var e in r.Entries)
        if (!e.IsDirectory)
          names.Add(e.Name);
    } catch { /* unreadable — treat as no collisions; the adder will throw if needed */ }
    return names;
  }

  /// <summary>
  /// Zeros every dead byte in the archive: gaps not covered by a live extent in
  /// the RAR layout map (markers, block headers, packed data and ENDARC are live
  /// and preserved). Cluster-tip wiping is N/A (RAR packs blocks back to back).
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;
    var extents = FileFormat.Rar.RarLayoutMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => FileFormat.Rar.RarLayoutMap.Enumerate(archive);

  /// <summary>Rebuild-based defrag delegating to RAR (CBR is a RAR variant).</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag delegating to RAR (CBR is a RAR variant).</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new FileFormat.Rar.RarReader(stream);
        var list = new List<(string Name, byte[] Data)>();
        for (var i = 0; i < r.Entries.Count; i++) {
          var e = r.Entries[i];
          if (e.IsDirectory) continue;
          list.Add((e.Name, r.Extract(i)));
        }
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new FileFormat.Rar.RarWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddFile(n, d);
          w.Finish();
        }
        return ms.ToArray();
      });
  }

  public string Id => "Cbr";
  public string DisplayName => "CBR";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable comic-book archive (RAR variant). Add/Replace/Remove take the
  // genuine in-place RAR block editors where byte-additive, else the verified
  // extract -> edit -> re-create rebuild. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories | FormatCapabilities.SupportsPassword;
  public string DefaultExtension => ".cbr";
  public IReadOnlyList<string> Extensions => [".cbr"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rar", "RAR")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Comic book RAR archive";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FileFormat.Rar.RarReader(stream, password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      $"Method{e.CompressionMethod}", e.IsDirectory, false, e.ModifiedTime?.DateTime)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FileFormat.Rar.RarReader(stream, password);
    for (var i = 0; i < r.Entries.Count; i++) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      WriteFile(outputDir, e.Name, r.Extract(i));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new FileFormat.Rar.RarWriter(output, leaveOpen: true, password: options.Password);
    foreach (var i in inputs) {
      // RAR stores directory structure implicitly via entry path components;
      // skip explicit directory inputs (mirrors how the RarReader exposes them).
      if (i.IsDirectory) continue;
      var modified = File.Exists(i.FullPath) ? new DateTimeOffset(File.GetLastWriteTimeUtc(i.FullPath), TimeSpan.Zero) : (DateTimeOffset?)null;
      w.AddFile(i.ArchiveName, i.ReadContent(), modified);
    }
  }
}
