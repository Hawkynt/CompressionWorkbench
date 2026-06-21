#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Btrfs;

public sealed class BtrfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveWriteConstraints, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <inheritdoc />
  /// <remarks>
  /// Btrfs write support is currently the WORM-minimal writer (single-leaf fs-tree,
  /// inline EXTENT_DATA). The knobs published here are reserved for future task #134
  /// expansion; only their defaults round-trip through the current writer. Non-default
  /// values are accepted but may be silently ignored — see <see cref="Create"/>.
  /// </remarks>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "NodeSize", DisplayName: "B-tree node size", Kind: FormatOptionKind.Integer, Default: "16384",
      AllowedValues: ["4096", "8192", "16384", "32768", "65536"],
      Description: "B-tree node size in bytes. 16KB is the modern default."),
    new FormatOptionDescriptor(
      Key: "SectorSize", DisplayName: "Sector size", Kind: FormatOptionKind.Integer, Default: "4096",
      AllowedValues: ["4096"],
      Description: "Sector size — Linux mkfs.btrfs only supports 4096 today."),
    new FormatOptionDescriptor(
      Key: "Label", DisplayName: "Volume label", Kind: FormatOptionKind.String, Default: "",
      Description: "Optional volume label."),
    new FormatOptionDescriptor(
      Key: "Features", DisplayName: "Feature flags", Kind: FormatOptionKind.String, Default: "mixed-bg,no-holes",
      Description: "Comma-separated feature list; only the listed defaults are currently supported."),
  ];

  /// <summary>
  /// Walks the superblock + chunk tree + root tree + fs-tree leaf and yields
  /// the actual on-disk byte layout. Targets the WORM writer profile (single
  /// fs-tree leaf, mostly inline EXTENT_DATA): inline extents surface as
  /// MetadataReserved tiles (file content lives inside the metadata leaf),
  /// regular extents surface as Used runs after logical→physical translation
  /// through the chunk map. Multi-leaf b-trees are not walked here — the
  /// WORM writer doesn't produce them.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => BtrfsExtentMap.Enumerate(image);



  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new BtrfsBlockMover();
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new BtrfsBlockMover();
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware Btrfs defragmentor via read-extract-rebuild dispatch through
  /// <see cref="DefragRebuilder"/>. All four <see cref="DefragMode"/> values supported.
  /// Image size preserved by writing back through BtrfsWriter.WriteTo into a
  /// MemoryStream sized to the original.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new BtrfsReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new BtrfsWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        using var ms = new MemoryStream();
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }

  // WORM-minimal writer constraints: a single leaf node holds ≤64 file
  // tuples (INODE_ITEM + DIR_INDEX + inline EXTENT_DATA). No chunk tree is
  // emitted — the reader's identity LogicalToPhysical fallback maps blocks.
  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription =>
    "Btrfs WORM image: up to 64 flat files with inline extents, single fs-tree leaf node.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (input.IsDirectory) { reason = "Btrfs writer only supports flat file lists (no directories)."; return false; }
    reason = null;
    return true;
  }

  public string Id => "Btrfs";
  public string DisplayName => "Btrfs Filesystem Image";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".btrfs";
  public IReadOnlyList<string> Extensions => [".btrfs", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("_BHRfS_M"u8.ToArray(), Offset: 0x10040, Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Btrfs copy-on-write filesystem image. The writer emits a populated
  /// <c>sys_chunk_array</c> inside the superblock and a real chunk tree
  /// with three chunks (<c>SYSTEM</c>, <c>METADATA</c>, <c>DATA</c>) that
  /// map every logical range used by the image to its physical offset,
  /// a dev tree with a <c>DEV_ITEM</c> for the single device, a root
  /// tree, and an FS tree leaf with inode + dir-index + inline
  /// <c>EXTENT_DATA</c> items per file. All metadata blocks carry
  /// CRC-32C (Castagnoli) at the start.
  /// </summary>
  public string Description => "Btrfs copy-on-write filesystem image with real chunk tree + CRC-32C metadata checksums";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new BtrfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored",
      e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new BtrfsReader(stream);
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
    var r = new BtrfsReader(archive);
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
    var w = new BtrfsWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }

  /// <summary>
  /// Two-pass streaming creation. Pass 1 plans the chunk/extent/inode layout
  /// from each input's pre-known size; pass 2 emits all metadata (with CRC-32C)
  /// plus inline file data, then streams each regular (non-inline) file's bytes
  /// into its DATA-chunk extent via 64 KB chunks — file bytes never travel
  /// through a writer-held <c>byte[]</c>. Btrfs data extents carry no checksum
  /// (the inode is NODATASUM and the CSUM_TREE is empty), so post-filling the
  /// extent bytes after the metadata CRCs are stamped is sound and the output is
  /// byte-identical to <see cref="Create"/> for the same inputs. Files smaller
  /// than one sector are stored inline in the FS-tree leaf, so their (bounded)
  /// bytes are read up front and treated like a classic <c>AddFile</c>.
  /// Non-seekable targets fall back to the buffering base implementation.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new BtrfsWriter();
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
  /// Rebuild-style add/replace (see <see cref="BtrfsModifier"/>). Emits a fresh
  /// <c>btrfs check --readonly</c>-clean image over the old bytes.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var toAdd = inputs
      .Where(i => !i.IsDirectory)
      .Select(i => (i.ArchiveName, i.ReadContent()))
      .ToList();
    BtrfsModifier.AddOrReplace(archive, toAdd);
  }

  /// <summary>
  /// Rebuild-style remove (see <see cref="BtrfsModifier"/>). The removed file's
  /// data does not survive into the rebuilt image because the new writer emits
  /// a fresh superblock, chunk tree, and fs-tree leaf.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    BtrfsModifier.Remove(archive, entryNames);
  }

  /// <summary>
  /// Zeros all unused space in a Btrfs image. The WORM writer stores every
  /// file's bytes as an <em>inline</em> EXTENT_DATA item inside the fs-tree
  /// metadata leaf, so file content surfaces as
  /// <see cref="DefragBlockKind.MetadataReserved"/> tiles (named
  /// <c>inline:&lt;file&gt;</c>) rather than as separate on-disk data extents.
  /// Consequently there are no cluster tips to wipe — inline payloads are
  /// byte-exact with no allocation slack — but the reserved DATA chunk and any
  /// gaps between metadata blocks are free and get zero-filled here.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    // Cluster tips are not applicable: file data is inline-packed in the
    // metadata leaf with byte-exact length. Pass no size lookup so the wiper
    // only reclaims free regions (the DATA chunk and inter-block gaps).
    image.Position = 0;
    var extents = BtrfsExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }
}
