#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ext;

/// <summary>
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.kernel.org/filesystems/ext4/index.html</c> — the kernel's ext4 on-disk layout documentation (superblock, group descriptors, inodes, extents; ext2/3 are subsets)</description></item>
///   <item><description><c>https://e2fsprogs.sourceforge.net/ext2intro.html</c> — Card/Ts'o/Tweedie, "Design and Implementation of the Second Extended Filesystem"</description></item>
///   <item><description><c>https://github.com/tytso/e2fsprogs</c> — e2fsprogs, the canonical userspace implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Ext4</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class ExtFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IFilesystemBlockMover, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {

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
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
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

  // ── IArchiveShrinkable: genuine in-place shrink ─────────────────────────

  /// <summary>
  /// Genuine in-place ext shrink: trims trailing free blocks via
  /// <see cref="ExtInPlaceShrinker"/> (updating bitmap / descriptors / superblock /
  /// backups / checksums; every surviving block stays byte-identical). Falls back to
  /// the <see cref="IArchiveShrinkable"/> default (verified rebuild / copy-through)
  /// when the in-place path declines — e.g. a target that would need genuine block
  /// relocation or block-group removal.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    try {
      input.Position = 0;
      using var work = new MemoryStream();
      input.CopyTo(work);
      var result = ExtInPlaceShrinker.ShrinkToFit(work);
      if (result.WasReduced) {
        output.Position = 0;
        output.SetLength(0);
        work.Position = 0;
        work.CopyTo(output);
        return;
      }
    } catch (NotSupportedException) {
      // fall through to the rebuild/copy-through default
    } catch (InvalidDataException) {
      // not an ext image we can parse in place; fall through
    }

    ((IArchiveShrinkable)this).ShrinkDefault(input, output);
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
    var entries = r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified,
      Kind: null, IsSymlink: e.IsSymlink, LinkTarget: e.LinkTarget
    )).ToList();
    return SymlinkResolver.Resolve(entries);
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
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the volume out. Reading a large input
      // into a byte[] just to hand it over would cap the volume at 2 GB even
      // though the writer places file data by seek.
      // Names are flattened to their leaf, as this path has always done.
      var name = Path.GetFileName(info.ArchiveName);
      if (info.InMemoryContent is { } bytes)
        w.AddFile(name, bytes);
      else
        w.AddStreamingFile(name, new FileInfo(info.FullPath).Length,
          () => File.OpenRead(info.FullPath));
    }

    // Resolve the schema-published knobs against the FormatCreateOptions bag,
    // falling back to the schema defaults if the caller didn't fill any in.
    var versionStr = options.GetOption("Version", "ext4");
    var version = versionStr switch {
      "ext2" => ExtWriter.ExtVersion.Ext2,
      "ext3" => ExtWriter.ExtVersion.Ext3,
      _ => ExtWriter.ExtVersion.Ext4,
    };
    // Block size: when the caller pinned one, honour it byte-for-byte; when it
    // was left unset, let the layout optimiser pick the legal block size that
    // minimises slack + metadata overhead for the actual file-set.
    var journal = options.GetOptionBool("Journal", true);
    var volumeLabel = options.GetOption("VolumeLabel", "");
    var inodeSize = options.GetOptionInt("InodeSize", 256);
    var blockSize = options.HasOption("BlockSize")
      ? options.GetOptionInt("BlockSize", 4096)
      : w.SelectOptimalBlockSize(inodeSize);

    // Size the volume to the payload. A fixed 4096-block image is ~16 MB at a
    // 4 KB block, so anything larger overran the buffer instead of growing.
    if (output.CanSeek)
      w.BuildToStreamingAutoSized(output, blockSize, version, journal, volumeLabel, inodeSize);
    else
      output.Write(w.BuildAutoSized(blockSize, version, journal, volumeLabel, inodeSize));
  }

  /// <summary>
  /// Two-pass streaming creation: pre-known per-input sizes drive ext block
  /// group sizing in pass 1; pass 2 emits superblock + BGD + bitmaps +
  /// inode table + directory blocks with file data blocks left zero, then
  /// streams each input's bytes from its
  /// <see cref="Compression.Registry.Streaming.StreamingArchiveInput.OpenStream"/>
  /// factory into its first allocated block via 64 KB chunks. Block tail
  /// past each entry's exact <c>Size</c> stays sparse-zero.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new ExtWriter();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    var versionStr = options.GetOption("Version", "ext4");
    var version = versionStr switch {
      "ext2" => ExtWriter.ExtVersion.Ext2,
      "ext3" => ExtWriter.ExtVersion.Ext3,
      _ => ExtWriter.ExtVersion.Ext4,
    };
    var journal = options.GetOptionBool("Journal", true);
    var volumeLabel = options.GetOption("VolumeLabel", "");
    var inodeSize = options.GetOptionInt("InodeSize", 256);
    var blockSize = options.HasOption("BlockSize")
      ? options.GetOptionInt("BlockSize", 4096)
      : w.SelectOptimalBlockSize(inodeSize);
    if (output.CanSeek) {
      w.BuildToStreaming(output, blockSize, totalBlocks: 4096, version, journal, volumeLabel, inodeSize);
      return;
    }
    // Size the volume to the payload. A fixed 4096-block image is ~16 MB at a
    // 4 KB block, so anything larger overran the buffer instead of growing.
    output.Write(w.BuildAutoSized(blockSize, version, journal, volumeLabel, inodeSize));
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ExtReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      // Streamed, not buffered: ext records i_size as a uint32, so an entry can
      // approach 4 GB -- more than the byte[] Extract returns could hold.
      using var target = CreateEntryFile(outputDir, e.Name);
      r.ExtractTo(e, target);
    }
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing ext2/3/4 image. Uses
  /// <see cref="ExtModifier"/> for true O(touched bytes) random-access I/O —
  /// only the superblock, BGD entry, block + inode bitmaps, the affected inode
  /// slot, the root dir block, and the file's data blocks are read or written.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var files = FormatHelpers.FilesOnly(inputs).ToList();
    // Genuine in-place add (no whole-image re-pack): touches only the affected
    // metadata + data blocks. Cases the in-place writer cannot handle yet (htree
    // directory growth, nested target paths, very fragmented extent layouts) throw
    // InPlaceUnsupportedException; for those we fall back to a read-then-rebuild so
    // the modify still yields a valid result.
    var rebuild = new List<(string Name, byte[] Data)>();
    foreach (var (name, data) in files) {
      try {
        // Replace-by-name semantics — drop any prior entry with the same name first.
        ExtModifier.RemoveFile(archive, name, wipeData: true);
        ExtModifier.AddFile(archive, name, data);
      } catch (ExtModifier.InPlaceUnsupportedException) {
        rebuild.Add((name, data));
      }
    }
    if (rebuild.Count > 0)
      ExtModifier.Mutate(archive, rebuild, System.Array.Empty<string>());
  }

  /// <summary>
  /// Securely removes files from an existing ext2/3/4 image. Uses
  /// <see cref="ExtModifier"/> for O(touched bytes) random-access I/O — file
  /// data blocks are wiped during removal so no forensic trace remains.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    // The in-place remover targets the root directory. Nested-path targets (which the
    // in-place adder also routes through the rebuild) are deleted via the verified
    // extract→re-create rebuild so they don't silently no-op.
    var flat = entryNames.Where(n => !n.Contains('/') && !n.Contains('\\')).ToArray();
    var nested = entryNames.Where(n => n.Contains('/') || n.Contains('\\')).ToArray();
    foreach (var name in flat)
      ExtModifier.RemoveFile(archive, name, wipeData: true);
    if (nested.Length > 0)
      ExtModifier.Mutate(archive, [], nested);
  }

  // ── ILayoutOptimizable ────────────────────────────────────────────────
  //
  // ext exposes a genuine per-file block allocation, so the block-size choice
  // materially changes file-tail slack. AnalyzeLayout reads only the superblock
  // (s_log_block_size at +24) plus the file-set sizes; RebuildStreaming re-emits
  // the volume at the target block size via the same auto-sizing path Create
  // uses. PatchInPlace handles the 16-byte volume label (superblock +120); a
  // block-size change is a structural rebuild, not an in-place patch.

  /// <inheritdoc />
  public LayoutAnalysis AnalyzeLayout(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.CanSeek) image.Position = 0;

    // s_log_block_size lives at superblock offset 1024 + 24; blockSize = 1024 << v.
    Span<byte> sb = stackalloc byte[28];
    image.Position = 1024;
    image.ReadExactly(sb);
    var logBlockSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sb[24..]);
    var current = 1024 << (int)logBlockSize;

    if (image.CanSeek) image.Position = 0;
    var reader = new ExtReader(image, leaveOpen: true);
    var fileSizes = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Size).ToList();

    int[] candidates = [1024, 2048, 4096];
    var optimal = Compression.Core.Layout.LayoutOptimizerAdapter.SelectAllocationUnit(
      candidates, fileSizes,
      fixedOverhead: bs => 4L * bs + (long)Math.Max(16, fileSizes.Count + 1) * 256);

    var currentSlack = Compression.Core.Layout.LayoutOptimizerAdapter.SlackAt(fileSizes, current);
    var optimalSlack = Compression.Core.Layout.LayoutOptimizerAdapter.SlackAt(fileSizes, optimal);
    return new LayoutAnalysis {
      ImageSize = image.CanSeek ? image.Length : 0,
      CurrentUnitSize = current,
      CurrentSlackBytes = currentSlack,
      OptimalUnitSize = optimal,
      OptimalSlackBytes = optimalSlack,
      InPlaceChanges = ["volume label"],
      RequiresRebuild = optimal != current ? ["block size"] : [],
      Notes = optimal == current
        ? ["Block size is already optimal for this file-set."]
        : [$"Rebuild at {optimal}-byte blocks saves {currentSlack - optimalSlack} slack bytes."],
    };
  }

  /// <inheritdoc />
  public void PatchInPlace(Stream image, LayoutPatch patch) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(patch);
    if (patch.VolumeLabel is { } label) {
      // s_volume_name: 16 bytes at superblock offset 1024 + 120.
      Span<byte> buf = stackalloc byte[16];
      var bytes = System.Text.Encoding.ASCII.GetBytes(label);
      bytes.AsSpan(0, Math.Min(bytes.Length, 16)).CopyTo(buf);
      image.Position = 1024 + 120;
      image.Write(buf);
    }
  }

  /// <inheritdoc />
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);
    if (source.CanSeek) source.Position = 0;
    var reader = new ExtReader(source, leaveOpen: true);
    var w = new ExtWriter();
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      w.AddFile(e.Name, reader.Extract(e));
    }
    // UnitSize 0 = auto-select the slack-optimal block size; explicit wins.
    var image = w.BuildAutoSized(options.UnitSize);
    target.Write(image);
    options.OnProgress?.Invoke(image.Length, image.Length);
  }
}
