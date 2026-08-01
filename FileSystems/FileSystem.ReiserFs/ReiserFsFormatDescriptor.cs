#pragma warning disable CS1591
using Compression.Core.DiskImage;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ReiserFs;

/// <summary>
/// R/W descriptor for ReiserFS v3.6 filesystem images (superblock at offset
/// 65536, R5 directory hash, 4 KB blocks).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/torvalds/linux/tree/v6.6/fs/reiserfs</c> — Linux reference implementation (v6.6 LTS tree; the driver was removed from later kernels)</description></item>
///   <item><description>reiserfsprogs (<c>mkreiserfs</c> / <c>debugreiserfs</c>) — canonical userspace tooling</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/ReiserFS</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class ReiserFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveWriteConstraints, IArchiveDefragmentable, IFormatOptionsSchema, ILayoutOptimizable, IFilesystemExtentMap, IWipeEmpty {

  // ── IFormatOptionsSchema ────────────────────────────────────────────────

  /// <summary>
  /// The one tunable the writer honours: the volume label written into the
  /// superblock <c>s_label</c> field (16 bytes) via <see cref="ReiserFsWriter.Label"/>
  /// and read back as <c>ReiserFsReader.Label</c>. The 4&#160;KB block size and
  /// R5 hash are fixed by the v3.6 layout, so they are not exposed.
  /// </summary>
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 16),
  ];

  // R/W write constraints — ReiserFS has no inherent ceiling; real mkfs.reiserfs minimum ≈ 128 MB.
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 128L * 1024 * 1024;
  public string AcceptedInputsDescription => "ReiserFS v3.6 filesystem image; full multi-leaf S+tree with nested directories and INDIRECT-item file bodies.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  public string Id => "ReiserFs";
  public string DisplayName => "ReiserFS";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable filesystem. Add/Remove produce a valid modified image; the
  // implementation re-packs the volume, so existing data may move — acceptable for
  // a conceptually read-write container. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  public string DefaultExtension => ".reiserfs";
  public IReadOnlyList<string> Extensions => [".reiserfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ReIsErFs"u8.ToArray(), Offset: 65536 + 52, Confidence: 0.95),
    new("ReIsEr2Fs"u8.ToArray(), Offset: 65536 + 52, Confidence: 0.95),
    new("ReIsEr3Fs"u8.ToArray(), Offset: 65536 + 52, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// ReiserFS v3.6 filesystem image — R/W. The writer emits a real
  /// spec-compliant multi-leaf S+tree image (superblock at +65536, R5-hashed
  /// directory entries, INDIRECT items with dedicated data blocks for file
  /// bodies &gt; 1 KB, internal pages above leaves). <see cref="IArchiveModifiable"/>
  /// Add tries a GENUINE in-place splice first (<see cref="ReiserFsInPlaceAdder"/>):
  /// for a single-leaf (tree_height 2) image it allocates a fresh objectid,
  /// inserts the file's STAT_DATA + DIRECT/INDIRECT items plus the R5-hashed
  /// dirent into the root leaf, appends any INDIRECT data blocks past the tree,
  /// and updates the bitmap / block counts — leaving every existing INDIRECT
  /// data block byte-identical at its original offset. Cases the in-place path
  /// does not handle (multi-leaf descent / split, nested sub-directory targets,
  /// replace-by-name, leaf overflow) fall back to a read-modify-rebuild via
  /// <see cref="ReiserFsWriter"/>; Remove always rebuilds. Both paths pass
  /// reiserfsck.
  /// </summary>
  public string Description => "ReiserFS v3 filesystem image (R/W, full S+tree mutation via rebuild)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ReiserFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new ReiserFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      var target = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      r.ExtractTo(e, output);
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
    var r = new ReiserFsReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      // A body may span many INDIRECT items, so it is spooled to scratch rather
      // than windowed; the spill is deleted when the stream closes.
      var scratch = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
        FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
      var size = r.ExtractTo(e, scratch);
      scratch.Position = 0;
      return new Compression.Registry.Streaming.BoundedEntryStream(scratch, size, leaveOpen: false);
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
    var w = new ReiserFsWriter();
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(label))
      w.Label = label;
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }

  /// <summary>
  /// Two-pass streaming creation. ReiserFS v3.6 has NO block checksums by
  /// design, so file bodies are fully streamable. Bodies above the writer's
  /// DIRECT/tail threshold (1 KiB) become INDIRECT items backed by dedicated
  /// data blocks: pass 1 builds the S+tree with those data-block runs left zero;
  /// pass 2 seeks to each run and copies its bytes from
  /// <see cref="Compression.Registry.Streaming.StreamingArchiveInput.OpenStream"/>
  /// in 64 KiB chunks. Tail-packed bodies ≤ 1 KiB live inside shared leaves, so
  /// the writer reads those small bodies up front (a bounded read). The output
  /// is byte-identical to <see cref="Create"/> for the same inputs. Falls back
  /// to the buffered default on a non-seekable target.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new ReiserFsWriter();
    if (!output.CanSeek) {
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

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Mode-aware ReiserFS v3.6 defragmentor via read-extract-rebuild dispatch
  /// through <see cref="DefragRebuilder"/>. The writer always emits a fresh
  /// contiguous-from-start single-leaf image (superblock at +65536, root SD
  /// + DIRENTRY + per-file SD/DIRECT items, R5-hashed key ordering).
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);

    // Buffering the rebuilt image would cap the volume at what a byte[] can
    // hold, so the packing modes stream: each entry is spilled to scratch and
    // the writer pulls it back while laying out the tree.
    // Every mode streams: end-pack and carve-hole order their entries from
    // scratch inside the rebuilder, so none of them has to fall back to the
    // buffered path that a volume past two gigabytes cannot use.
    {
      ReiserFsWriter? writer = null;
      Stream? target = null;
      var spill = new List<string>();
      try {
        DefragRebuilder.RebuildStreaming(archive, options,
          readEntries: ReadEntries,
          beginWrite: s => { writer = new ReiserFsWriter(); target = s; },
          writeEntry: (name, data) => {
            var path = Path.GetTempFileName();
            spill.Add(path);
            File.WriteAllBytes(path, data);
            writer!.AddStreamingFile(name, data.LongLength, () => File.OpenRead(path));
          },
          finishWrite: () => writer!.BuildToStreaming(target!));
      } finally {
        foreach (var path in spill)
          try { File.Delete(path); } catch { /* scratch file already gone */ }
      }
    }
  }

  // ── IArchiveModifiable ────────────────────────────────────────────────
  // Read-modify-rebuild via the multi-leaf writer. Every Add and Remove
  // materialises the live entries from the current image, applies the edit
  // in memory, and rewrites the image. Covers nested paths, leaf splits and
  // merges, multi-leaf descent, INDIRECT-sized bodies and root tree-height
  // growth — every path that previously fell back to NotSupportedException.

  /// <summary>
  /// Adds (or replaces, on name collision) the given files inside an existing
  /// ReiserFS image. Routed through <see cref="ReiserFsModifier"/> which does
  /// the full read-modify-rebuild via <see cref="ReiserFsWriter"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      ReiserFsModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing ReiserFS image. The rebuild
  /// always starts from zeroed bytes so the removed file data leaves no
  /// forensic trace.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ReiserFsModifier.RemoveFile(archive, name, wipeData: true);
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new ReiserFsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new ReiserFsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  // ── IFilesystemExtentMap / IWipeEmpty ──────────────────────────────────

  /// <summary>Byte offset of the ReiserFS superblock.</summary>
  private const int SuperblockOffset = 65536;

  /// <summary>First bitmap block; the rest sit every <c>block_size * 8</c> blocks.</summary>
  private const int FirstBitmapBlock = 17;

  /// <summary>
  /// Reads the on-disk block bitmap and reports every allocated run. ReiserFS
  /// records allocation for the whole volume there — boot area, superblock,
  /// bitmaps, journal, tree and data alike — so what the bitmap leaves clear is
  /// exactly the free space, whatever a file once wrote into it.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var accessor = new ImageAccessor(image);
      if (accessor.Length < SuperblockOffset + 64) return [];

      var sb = accessor.Read(SuperblockOffset, 64);
      var blockCount = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0));
      var blockSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(44));
      if (blockSize == 0) blockSize = 4096;
      if (blockCount <= 0 || blockCount * blockSize > accessor.Length + blockSize) return [];

      var blocksPerBitmap = (long)blockSize * 8;
      long runStart = -1;
      for (var block = 0L; block < blockCount; ++block) {
        var bitmapIndex = block / blocksPerBitmap;
        var bitmapBlock = bitmapIndex == 0 ? FirstBitmapBlock : bitmapIndex * blocksPerBitmap;
        var bit = block % blocksPerBitmap;
        var byteOffset = bitmapBlock * blockSize + (bit >> 3);
        if (byteOffset >= accessor.Length) break;
        var allocated = (accessor.ReadByte(byteOffset) & (1 << (int)(bit & 7))) != 0;

        if (allocated) {
          if (runStart < 0) runStart = block;
          continue;
        }
        if (runStart >= 0) {
          result.Add(new DefragBlockInfo(runStart * blockSize, (block - runStart) * blockSize,
            DefragBlockKind.MetadataReserved));
          runStart = -1;
        }
      }
      if (runStart >= 0)
        result.Add(new DefragBlockInfo(runStart * blockSize, (blockCount - runStart) * blockSize,
          DefragBlockKind.MetadataReserved));
    } catch {
      // An image we cannot read the bitmap from claims nothing; wiping it would
      // zero live data.
      return [];
    }
    return result;
  }

  /// <inheritdoc />
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    // The bitmap is per block and says nothing about where a file ends inside
    // its last one, so there are no cluster tips to trim from it.
    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
  }

}
