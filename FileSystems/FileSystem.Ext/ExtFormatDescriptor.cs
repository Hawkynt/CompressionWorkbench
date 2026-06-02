#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ext;

public sealed class ExtFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema {

  /// <summary>
  /// Zeros all unused space in the ext2/3/4 image: free blocks, block-tip slack
  /// (the bytes between a file's real size and the end of its last allocated
  /// block), and any gaps outside the metadata regions. Driven by the generic
  /// <see cref="UnusedSpaceWiper"/> over the ext extent map, with an
  /// inode-size-based file-size lookup for block-tip precision.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new ExtReader(image);
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
    var extents = ExtExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// Tunables surfaced by the Convert Archive dialog / CLI for ext creation —
  /// revision (ext2/3/4), block size, optional journal toggle (gated on the
  /// revision selector via DependsOn), volume label, and inode size. The
  /// Journal knob is hidden in the UI for ext2 (which has no journal); for
  /// ext3/ext4 it defaults to enabled to match mkfs.ext{3,4} convention.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Version", DisplayName: "Filesystem Version", Kind: FormatOptionKind.Enum, Default: "ext4",
      AllowedValues: ["ext2", "ext3", "ext4"],
      Description: "ext filesystem revision. ext3 adds journaling; ext4 adds extents + large file support."),
    new FormatOptionDescriptor(
      Key: "BlockSize", DisplayName: "Block Size (bytes)", Kind: FormatOptionKind.Integer, Default: "4096",
      AllowedValues: ["1024", "2048", "4096"]),
    new FormatOptionDescriptor(
      Key: "Journal", DisplayName: "Enable Journal", Kind: FormatOptionKind.Boolean, Default: "true",
      DependsOn: "Version=ext3|ext4",
      Description: "Enable the journal (always on for ext3/ext4; ext2 has none)."),
    new FormatOptionDescriptor(
      Key: "VolumeLabel", DisplayName: "Volume Label", Kind: FormatOptionKind.String, Default: ""),
    new FormatOptionDescriptor(
      Key: "InodeSize", DisplayName: "Inode Size (bytes)", Kind: FormatOptionKind.Integer, Default: "256",
      AllowedValues: ["128", "256"]),
  ];

  /// <summary>
  /// Walks the superblock + BGD table + inode tree and yields the actual
  /// on-disk byte layout — every metadata region (SB, BGDT, block + inode
  /// bitmaps, inode tables) plus one extent per contiguous block run per
  /// file (coalesced for direct/indirect pointers; native ext4 extent runs
  /// surface as-is). Used by the defragment window's block-map preview.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => ExtExtentMap.Enumerate(image);

  public string Id => "Ext";
  public string DisplayName => "ext2/3/4";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  // ── IFilesystemBlockMover delegation ───────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    var mover = new ExtBlockMover();
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    mover.Init(ms.ToArray());
    mover.MoveExtent(image, srcOffset, dstOffset, length, zeroSource);
  }

  /// <inheritdoc />
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var mover = new ExtBlockMover();
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    mover.Init(ms.ToArray());
    mover.UpdateAllocationAfterMove(image, fileName, oldOffset, newOffset, length);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware ext2/3/4 defragmentor. Supports planner-driven in-place path
  /// (using <see cref="DefragPlanner"/> + <see cref="ExtBlockMover"/>) and the
  /// legacy rebuild path (using <see cref="DefragRebuilder"/>).
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode is DefragMode.ConsolidateAtStart or DefragMode.ConsolidateAtEnd or DefragMode.FillHolesLazy or DefragMode.CarveHole) {
      try {
        DefragmentWithPlanner(archive, options);
        return;
      } catch {
        archive.Position = 0;
      }
    }
    DefragmentWithRebuild(archive, options);
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new ExtBlockMover();
    mover.Init(archive); // reads only SB + first BGD (~2 KB)

    var extents = ExtExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent("scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    var moves = DefragPlanner.Plan(extents, mover.FirstDataByte, archive.Length, mover.BlockSize, options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt);
    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    // SB/BGD don't change during defrag — no per-move re-init needed.
    DefragPlannerExecutor.Execute(archive, options, mover, moves, archive.Length, reinitAfterMove: null);

    archive.Position = 0;
    var postExtents = ExtExtentMap.Enumerate(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent("complete", 1, -1, -1, archive.Length, postExtents, "Defragmentation complete"));
  }

  private void DefragmentWithRebuild(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new ExtReader(stream);
        return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new ExtWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        return w.Build();
      });
  }
  public string DefaultExtension => ".ext2";
  public IReadOnlyList<string> Extensions => [".ext2", ".ext3", ".ext4", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x53, 0xEF], 1080, 0.80f)]; // magic at superblock offset 1024 + field offset 56
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ext2/ext3/ext4 Linux filesystem image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ExtReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
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
    var r = new ExtReader(archive);
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
    var w = new ExtWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);

    // Resolve the schema-published knobs against the FormatCreateOptions bag,
    // falling back to the schema defaults if the caller didn't fill any in.
    var versionStr = options.GetOption("Version", "ext4");
    var version = versionStr switch {
      "ext2" => ExtWriter.ExtVersion.Ext2,
      "ext3" => ExtWriter.ExtVersion.Ext3,
      _ => ExtWriter.ExtVersion.Ext4,
    };
    var blockSize = options.GetOptionInt("BlockSize", 4096);
    var journal = options.GetOptionBool("Journal", true);
    var volumeLabel = options.GetOption("VolumeLabel", "");
    var inodeSize = options.GetOptionInt("InodeSize", 256);

    output.Write(w.Build(blockSize, totalBlocks: 4096, version, journal, volumeLabel, inodeSize));
  }

  /// <summary>
  /// Streaming creation: drains each <see cref="Compression.Registry.Streaming.StreamingArchiveInput"/>
  /// via its bounded <c>OpenStream</c> factory and feeds the writer one
  /// file at a time. The writer is one-pass internally (it sizes the
  /// block group + inode table after all files are added) so each input
  /// is materialised into a byte array before <c>AddFile</c>; the bound
  /// is enforced by the caller's <c>OpenStream</c> result.
  ///
  /// TODO: refactor <see cref="ExtWriter"/> to a true two-pass streaming
  /// build (extent allocation from known sizes in pass 1; per-file block
  /// copy in pass 2) to remove the per-file buffering.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new ExtWriter();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      using var src = input.OpenStream();
      using var ms = new MemoryStream(checked((int)input.Size));
      src.CopyTo(ms);
      w.AddFile(input.Name, ms.ToArray());
    }
    var versionStr = options.GetOption("Version", "ext4");
    var version = versionStr switch {
      "ext2" => ExtWriter.ExtVersion.Ext2,
      "ext3" => ExtWriter.ExtVersion.Ext3,
      _ => ExtWriter.ExtVersion.Ext4,
    };
    var blockSize = options.GetOptionInt("BlockSize", 4096);
    var journal = options.GetOptionBool("Journal", true);
    var volumeLabel = options.GetOption("VolumeLabel", "");
    var inodeSize = options.GetOptionInt("InodeSize", 256);
    output.Write(w.Build(blockSize, totalBlocks: 4096, version, journal, volumeLabel, inodeSize));
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ExtReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing ext2/3/4 image. Uses
  /// <see cref="ExtModifier"/> for true O(touched bytes) random-access I/O —
  /// only the superblock, BGD entry, block + inode bitmaps, the affected inode
  /// slot, the root dir block, and the file's data blocks are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs)) {
      // Replace-by-name semantics — drop any prior entry with the same name first.
      ExtModifier.RemoveFile(archive, name, wipeData: true);
      ExtModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Securely removes files from an existing ext2/3/4 image. Uses
  /// <see cref="ExtModifier"/> for O(touched bytes) random-access I/O — file
  /// data blocks are wiped during removal so no forensic trace remains.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ExtModifier.RemoveFile(archive, name, wipeData: true);
  }
}
