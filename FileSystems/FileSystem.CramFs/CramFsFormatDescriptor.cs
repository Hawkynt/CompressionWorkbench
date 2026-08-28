#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.CramFs;

/// <summary>
/// Offline R/W descriptor for Linux CramFS images. The Linux filesystem is
/// intentionally read-only when mounted, but the workbench can create and edit
/// an existing image by verified rebuild and can perform physical layout moves
/// where the compressed-block metadata can be repointed safely.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.kernel.org/filesystems/cramfs.html</c> — Linux kernel cramfs documentation</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/cramfs</c> — mainline implementation (its README documents the on-disk layout)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Cramfs</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class CramFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty, ILayoutOptimizable {
  public string Id => "CramFs";
  public string DisplayName => "CramFS";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W is the public existing-instance edit contract, not a claim that a Linux
  // kernel mounts CramFS writable or that every edit is byte-local. Add/Remove
  // rebuild the image when necessary and return a valid edited CramFS image.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cramfs";
  public IReadOnlyList<string> Extensions => [".cramfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x45, 0x3D, 0xCD, 0x28], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("cramfs", "CramFS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Linux compressed ROM filesystem with offline R/W rebuild and layout maintenance support";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new CramFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size, -1,
      "cramfs", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new CramFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single filesystem entry as a bounded read-only stream. The
  /// reader produces the decoded file bytes by walking the entry's extent
  /// or block chain; the matched bytes are wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length so cluster/extent slack past the entry's
  /// end is physically unreachable through this view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new CramFsReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.FullPath, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
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
    using var w = new CramFsWriter(output, leaveOpen: true);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        w.AddDirectory(input.ArchiveName.TrimEnd('/'));
      } else {
        var data = input.ReadContent();
        w.AddFile(input.ArchiveName, data);
      }
    }
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs,
      readEntries: stream => {
        var r = new CramFsReader(stream);
        return r.Entries.Where(e => e.IsRegularFile).Select(e => (e.FullPath, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new CramFsWriter(ms, leaveOpen: true))
          foreach (var (n, d) in files) w.AddFile(n, d);
        return ms.ToArray();
      });

  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames,
      readEntries: stream => {
        var r = new CramFsReader(stream);
        return r.Entries.Where(e => e.IsRegularFile).Select(e => (e.FullPath, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new CramFsWriter(ms, leaveOpen: true))
          foreach (var (n, d) in files) w.AddFile(n, d);
        return ms.ToArray();
      });

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Lays the image out again. A file is a block pointer table followed by the
  /// compressed blocks it ends, and its inode says where that pair starts — so
  /// a move is the copy, one field, and the same delta added to every entry in
  /// the table, which is cheaper than decompressing every file and compressing
  /// it back.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // The in-place pass is kept only if every payload still reads back: it can
    // refuse partway, and a rebuild is the honest answer when it does.
    DefragContentGuard.RunOrRebuild(archive,
      readContents: stream => ReadEntries(stream).Select(e => e.Data).ToList(),
      inPlace: () => this.DefragmentWithPlanner(archive, options),
      rebuild: () => DefragRebuilder.Rebuild(archive, options,
        readEntries: stream => ReadEntries(stream),
        buildImage: files => {
          using var ms = new MemoryStream();
          using (var w = new CramFsWriter(ms, leaveOpen: true))
            foreach (var (n, d) in files) w.AddFile(n, d);
          var built = ms.ToArray();
          if (built.Length >= archive.Length) return built;
          var padded = new byte[archive.Length];
          Array.Copy(built, padded, built.Length);
          return padded;
        }));
  }

  /// <summary>Plans the moves the layout needs, commits them, and restamps.</summary>
  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new CramFsBlockMover();
    mover.Init(archive);

    var extents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.FirstDataByte, archive.Length, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    Compression.Core.Layout.DefragPlannerExecutor.Execute(archive, options, mover, moves,
      archive.Length, reinitAfterMove: null);

    // The superblock's checksum covers the whole image, so it is stamped once
    // the bytes have stopped moving rather than after every move.
    CramFsBlockMover.RestampChecksum(archive);

    archive.Position = 0;
    var postExtents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  /// <summary>Every file's path and bytes, for the rebuild and the guard.</summary>
  private static List<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    var reader = new CramFsReader(stream);
    return reader.Entries.Where(e => e.IsRegularFile)
                         .Select(e => (e.FullPath, reader.Extract(e))).ToList();
  }

  /// <summary>
  /// CramFS images produced by the canonical writer are tightly packed and have
  /// no allocation-unit cluster tips. If a non-canonical image contains gaps,
  /// the physical extent map below identifies them; this explicit override
  /// remains conservative and leaves them alone because old CramFS tooling may
  /// use alignment/padding bytes in ways that are not recoverable from inodes.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    return 0;
  }

  /// <summary>
  /// Reports where the image's bytes actually are: the superblock and the inode
  /// area as structure, and each file's block pointer table and compressed
  /// blocks under its name.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      var reader = new CramFsReader(image);

      var owned = new List<(long Start, long End)>();
      var firstData = image.Length;
      foreach (var entry in reader.Entries) {
        if (!entry.IsRegularFile) continue;
        var (offset, length) = reader.DataExtent(entry);
        if (length <= 0) continue;
        result.Add(new DefragBlockInfo(offset, length, DefragBlockKind.Used, entry.FullPath));
        owned.Add((offset, offset + length));
        firstData = Math.Min(firstData, offset);
      }
      owned.Sort((a, b) => a.Start.CompareTo(b.Start));

      if (firstData > 0 && firstData <= image.Length)
        result.Add(new DefragBlockInfo(0, firstData, DefragBlockKind.MetadataReserved,
          "superblock and inodes"));

      var cursor = firstData;
      foreach (var (start, end) in owned) {
        if (start > cursor)
          result.Add(new DefragBlockInfo(cursor, start - cursor, DefragBlockKind.Free));
        cursor = Math.Max(cursor, end);
      }
      if (cursor < image.Length)
        result.Add(new DefragBlockInfo(cursor, image.Length - cursor, DefragBlockKind.Free));
    } catch {
      // Fail closed: an image we cannot walk claims no free space.
      return [];
    }
    return result;
  }
}
