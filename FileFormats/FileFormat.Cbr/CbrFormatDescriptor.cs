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
  /// Adds new pages directly through the RAR5 append editor when the archive profile
  /// permits it. The RAR editor validates collisions and unsupported whole-archive
  /// structures before writing, so unsupported profiles can fall back without first
  /// cloning the entire CBR. Same-name replacement remains rebuild-backed because a
  /// remove+add pair is a two-step transaction.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    var hasDirectory = false;
    var newFiles = new List<(string Name, byte[] Data, DateTimeOffset? ModifiedTime)>();
    foreach (var input in inputs) {
      if (string.IsNullOrEmpty(input.ArchiveName)) continue;
      if (input.IsDirectory) { hasDirectory = true; continue; }
      newFiles.Add((input.ArchiveName, input.ReadContent(), null));
    }

    if (!hasDirectory && newFiles.Count > 0) {
      try {
        archive.Position = 0;
        FileFormat.Rar.RarInPlaceAdder.Add(archive, newFiles);
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
  /// Removes supported non-solid pages directly through the RAR5 block remover.
  /// Unsupported encryption, recovery/quick-open, solid dependency and RAR4 cases
  /// are rejected before the first byte move and therefore safely fall back to the
  /// verified rebuild without an O(total bytes) transaction snapshot.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);

    try {
      archive.Position = 0;
      FileFormat.Rar.RarInPlaceRemover.Remove(archive, entryNames);
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
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
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

  /// <summary>Gets the id.</summary>
  public string Id => "Cbr";
  /// <summary>Gets the display name.</summary>
  public string DisplayName => "CBR";
  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable comic-book archive (RAR variant). Supported RAR5 edits go
  // straight through the native block editors; unsupported profiles rebuild.
  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories | FormatCapabilities.SupportsPassword;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".cbr";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".cbr"];
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
public IReadOnlyList<FormatMethodInfo> Methods => [new("rar", "RAR")];
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
public string Description => "Comic book RAR archive";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new FileFormat.Rar.RarReader(stream, password);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.CompressedSize,
      $"Method{e.CompressionMethod}", e.IsDirectory, false, e.ModifiedTime?.DateTime)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new FileFormat.Rar.RarReader(stream, password);
    for (var i = 0; i < r.Entries.Count; i++) {
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
    using var w = new FileFormat.Rar.RarWriter(output, leaveOpen: true, password: options.Password);
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var modified = File.Exists(i.FullPath) ? new DateTimeOffset(File.GetLastWriteTimeUtc(i.FullPath), TimeSpan.Zero) : (DateTimeOffset?)null;
      w.AddFile(i.ArchiveName, i.ReadContent(), modified);
    }
  }
}
