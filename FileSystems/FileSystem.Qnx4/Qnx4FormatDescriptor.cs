#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Qnx4;

/// <summary>
/// R/W descriptor for QNX4 filesystem images. QNX4 has no fixed magic
/// at the start of the image — detection relies on the inode status byte
/// pattern in the root directory cluster (block 1).
///
/// <para>Add / Remove are routed through <see cref="Qnx4Modifier"/>, which
/// mutates the root cluster (LBA 1-4) and the <c>.bitmap</c> (LBA 5) in
/// place. Scope stays flat-root (29 user files) — past that Add throws
/// <see cref="NotSupportedException"/>, matching the WORM writer's capacity
/// guard. Subdirectory emission is still out of scope.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/torvalds/linux/blob/master/include/uapi/linux/qnx4_fs.h</c> — canonical on-disk structures</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/qnx4</c> — Linux reference implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/QNX</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class Qnx4FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveDefragmentable, IArchiveModifiable, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {
  public string Id => "Qnx4";
  public string DisplayName => "QNX4 FS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".qnx4";
  public IReadOnlyList<string> Extensions => [".qnx4", ".qnx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // QNX4 has no fixed superblock magic. Detection looks for any of the
    // recognised "live inode" status bytes at offset 0x23D (= block 1, first
    // inode entry's di_status field). Status bytes accepted:
    //   0x01 = QNX4_FILE_USED  (Linux-friendly short-name file)
    //   0x08 = QNX4_FILE_LINK  (long-name continuation marker — historical QNX4-utils)
    //   0x09 = QNX4_FILE_USED|LINK (root self-reference emitted by our writer)
    new([0x01], Offset: 0x23D, Confidence: 0.35),
    new([0x08], Offset: 0x23D, Confidence: 0.35),
    new([0x09], Offset: 0x23D, Confidence: 0.40),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "QNX4 filesystem image (1991-2001, QNX Software Systems) — R/W (flat root, max 29 user files).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new Qnx4Reader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new Qnx4Reader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      // Streamed, not buffered: an entry may be larger than a byte[] can hold.
      using var target = CreateEntryFile(outputDir, e.Name);
      r.ExtractTo(e, target);
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new Qnx4Reader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  // ── IArchiveCreatable ───────────────────────────────────────────────────
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new Qnx4Writer();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the volume out; reading a large input
      // into a byte[] would cap the volume at what an array can hold.
      var name = Path.GetFileName(info.ArchiveName);
      if (info.InMemoryContent is { } bytes)
        w.AddFile(name, bytes);
      else
        w.AddStreamingFile(name, new FileInfo(info.FullPath).Length, () => File.OpenRead(info.FullPath));
    }
    w.WriteTo(output);
  }

  // ── IArchiveModifiable ──────────────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by name) files inside an existing QNX4 image. Routed
  /// through <see cref="Qnx4Modifier.AddFile"/>: the root cluster + bitmap
  /// are mutated in place, the new file's data extent is allocated from the
  /// bitmap, and the inode lands in the first free slot (entries 3..31).
  /// </summary>
  /// <exception cref="NotSupportedException">Root cluster full (29 user
  /// files). The flat-root scope matches the WORM writer's capacity guard.</exception>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var (name, data) in FlatFiles(inputs))
      Qnx4Modifier.AddFile(archive, name, data);
  }

  /// <summary>
  /// Removes the named entries from an existing QNX4 image. Routed through
  /// <see cref="Qnx4Modifier.RemoveFile"/>: the dirent is located in the
  /// root cluster, the extent is freed in the bitmap, data blocks are
  /// zero-wiped, and the inode slot is cleared.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      Qnx4Modifier.RemoveFile(archive, name, wipeData: true);
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>
  /// Every QNX4 file is one contiguous extent recorded in its inode, so the map
  /// is the boot/root/bitmap region as structure plus one run per live entry.
  /// Blocks no live inode names are what a removal left behind.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      var reader = new Qnx4Reader(image);
      // Boot block, root directory cluster and the system inodes that follow it.
      var first = long.MaxValue;
      foreach (var e in reader.Entries) {
        if (e.FirstExtentBlock == 0 || e.ExtentBlockCount == 0) continue;
        var offset = (long)e.FirstExtentBlock * Qnx4Reader.BlockSize;
        if (offset < 0 || offset >= image.Length) continue;
        var length = Math.Min((long)e.ExtentBlockCount * Qnx4Reader.BlockSize, image.Length - offset);
        if (length <= 0) continue;
        result.Add(new DefragBlockInfo(offset, length,
          e.IsDirectory ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used,
          e.IsDirectory ? null : e.Name));
        first = Math.Min(first, offset);
      }
      if (first == long.MaxValue) first = Math.Min(image.Length, 64L * Qnx4Reader.BlockSize);
      result.Add(new DefragBlockInfo(0, first, DefragBlockKind.MetadataReserved));
    } catch {
      // An image we cannot walk claims nothing; wiping it would zero live data.
      return [];
    }
    return result;
  }

  /// <inheritdoc />
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;

    // A file's extent is whole blocks; the tail past its recorded size is slack.
    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new Qnx4Reader(image);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var e in reader.Entries)
          if (!e.IsDirectory)
            sizes[e.Name] = e.Size;
        fileSizeLookup = n => sizes.TryGetValue(n, out var v) ? v : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length, wipeClusterTips, fileSizeLookup);
  }

}
