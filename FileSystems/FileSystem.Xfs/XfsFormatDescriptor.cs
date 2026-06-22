#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Xfs;

public sealed class XfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

  /// <summary>
  /// XFS geometry (block size, inode size, AG layout) is fixed at the
  /// <c>mkfs.xfs</c>-faithful defaults the writer emits, so the only honoured
  /// tunable is the volume label stored in the superblock <c>sb_fname[12]</c>
  /// field (ASCII, truncated to 12 bytes).
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "VolumeLabel", DisplayName: "Volume Label", Kind: FormatOptionKind.String, Default: "",
      Description: "XFS volume label stored in sb_fname (max 12 ASCII chars)."),
  ];

  /// <summary>
  /// Walks the per-AG superblock + AGF/AGI/AGFL + bnobt/cntbt/inobt headers
  /// (yielded as MetadataReserved tiles) and the root inode's directory
  /// listing, then yields each child file's BMBT_REC packed-128-bit extents
  /// as Used runs (with adjacent runs coalesced). Inline (<c>local</c>
  /// fork-format) inodes surface as MetadataReserved — the file content
  /// lives inside the inode itself.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => XfsExtentMap.Enumerate(image);

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new XfsBlockMover();
    image.Position = 0;
    mover.Init(image); // reads only the 512-byte superblock
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new XfsBlockMover();
    image.Position = 0;
    mover.Init(image); // reads only the 512-byte superblock
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware XFS defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. All four <see cref="DefragMode"/> values supported.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new XfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new XfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        using var ms = new MemoryStream();
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }

  // WORM write constraints — XFS has no inherent ceiling; real mkfs.xfs minimum ≈ 16 MB.
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 16 * 1024 * 1024;
  public string AcceptedInputsDescription => "XFS v5 (CRC) filesystem image; nested directory trees with short-form, single-block and leaf-form dir2 directories.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in an XFS image: free blocks and the cluster-tip
  /// slack at the tail of each file's last data block.
  /// <para>The XFS extent map emits each file's data as a <c>Used</c> run
  /// clipped to the file's logical size. The remainder of the file's last
  /// block (from the logical size to the block boundary) is therefore not
  /// covered by any live extent and surfaces as a free gap, which the generic
  /// wiper scrubs — that is the cluster tip. A directory-entry size lookup
  /// (keyed by the same file name the extent map uses) is supplied so any
  /// extent reported block-aligned is still trimmed precisely.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new XfsReader(image);
        var sizeMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries)
          if (!entry.IsDirectory)
            sizeMap[entry.Name] = entry.Size;
        fileSizeLookup = name => sizeMap.TryGetValue(name, out var s) ? s : -1;
      } catch {
        fileSizeLookup = null;
      }
    }

    image.Position = 0;
    var extents = XfsExtentMap.Enumerate(image).ToList();

    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  public string Id => "Xfs";
  public string DisplayName => "XFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".xfs";
  public IReadOnlyList<string> Extensions => [".xfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("XFSB"u8.ToArray(), Offset: 0, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "XFS filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new XfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new XfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
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
    var r = new XfsReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
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
    var w = new XfsWriter();
    w.SetVolumeLabel(options.GetOption("VolumeLabel", ""));
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }

  /// <summary>
  /// Two-pass streaming creation. Pass 1 plans the AG / inode / data-extent
  /// geometry from each input's pre-known size; pass 2 emits all metadata (with
  /// CRC-32C), then streams each file's bytes into its data extent via 64 KB
  /// chunks — file bytes never travel through a writer-held <c>byte[]</c>. XFS
  /// stores every regular file as a data extent (no inline file form) and file
  /// data carries no CRC (only metadata/dir blocks are checksummed), so the
  /// streamed output is byte-identical to <see cref="Create"/> for the same
  /// inputs. Non-seekable targets fall back to the buffering base implementation.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new XfsWriter();
    w.SetVolumeLabel(options.GetOption("VolumeLabel", ""));
    if (!output.CanSeek) {
      // Non-seekable target: cannot do the seek-back second pass, so buffer
      // each entry into the writer's byte[] path and emit in one shot.
      foreach (var input in inputs) {
        if (input.IsDirectory) continue;
        using var src = input.OpenStream();
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        w.AddFile(input.Name, ms.ToArray());
      }
      w.WriteTo(output);
      return;
    }
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    w.BuildToStreaming(output);
  }

  /// <summary>
  /// Rebuild-style add/replace (see <see cref="XfsModifier"/>). Emits a fresh
  /// <c>xfs_repair -n -f</c>-clean image over the old bytes.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var toAdd = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (i.ArchiveName, i.ReadContent()))
      .ToList();
    XfsModifier.AddOrReplace(archive, toAdd);
  }

  /// <summary>
  /// Rebuild-style remove (see <see cref="XfsModifier"/>). The removed file's
  /// data does not survive into the rebuilt image because the new writer emits
  /// a fresh superblock, AGF/AGI, and inode table.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    XfsModifier.Remove(archive, entryNames);
  }
}
