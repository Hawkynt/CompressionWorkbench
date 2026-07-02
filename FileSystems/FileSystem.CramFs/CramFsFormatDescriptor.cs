#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.CramFs;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.kernel.org/filesystems/cramfs.html</c> — Linux kernel cramfs documentation</description></item>
///   <item><description><c>https://github.com/torvalds/linux/tree/master/fs/cramfs</c> — mainline implementation (its README documents the on-disk layout)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Cramfs</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class CramFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty {
  public string Id => "CramFs";
  public string DisplayName => "CramFS";
  public FormatCategory Category => FormatCategory.Archive;
  // WORM (Write-Once-Read-Many), NOT R/W: CramFS is a compressed, read-only ROM
  // filesystem. Add/Remove are implemented via the verified extract -> re-create
  // rebuild (ModifyRebuilder), which is a full rewrite — so the verb works, but the
  // image is not modified in place. Advertising CanModify would falsely claim genuine
  // in-place R/W. See Compression.Registry/FormatCapabilities.cs for the WORM vs R/W rule.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cramfs";
  public IReadOnlyList<string> Extensions => [".cramfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([0x45, 0x3D, 0xCD, 0x28], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("cramfs", "CramFS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Linux Compressed ROM filesystem";

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

  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options,
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

  /// <summary>
  /// CramFS is a compressed, read-only ROM filesystem: the superblock, inode
  /// tables, block-pointer tables and zlib-compressed page blocks are laid out
  /// tightly back-to-back (only 4-byte alignment padding, which is already
  /// zero) with no free space and no cluster tips. File data is packed at the
  /// compressed-block level, so there is no allocation slack to wipe.
  ///
  /// <para>Note: <see cref="EnumerateExtents"/> reports Used runs at synthetic,
  /// uncompressed-size offsets for the defrag preview — those offsets do
  /// <em>not</em> map to real on-disk positions, so this method deliberately
  /// does not drive the generic wiper from them (doing so would zero live
  /// compressed bytes). Nothing is reclaimable; this returns 0.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    // Fully packed, read-only image — no free regions or cluster tips exist.
    return 0;
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    yield return new DefragBlockInfo(0, CramFsConstants.SuperblockSize, DefragBlockKind.MetadataReserved, "superblock");
    var r = new CramFsReader(image);
    long offset = CramFsConstants.SuperblockSize;
    foreach (var e in r.Entries) {
      if (!e.IsRegularFile) continue;
      if (e.Size > 0) {
        yield return new DefragBlockInfo(offset, e.Size, DefragBlockKind.Used, e.FullPath);
        offset += e.Size;
      }
    }
  }
}
