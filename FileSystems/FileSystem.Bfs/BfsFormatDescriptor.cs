#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Bfs;

/// <summary>
/// R/W descriptor for BeOS / Haiku BFS filesystem images. Can list, extract,
/// create (WORM), modify (via rebuild), and defragment BFS images. The writer
/// produces a minimal single-AG image with a single B+ tree leaf for the root
/// directory and direct block_run extents for file data.
///
/// References:
/// <list type="bullet">
///   <item><description>"Practical File System Design with the Be File System" (Dominic Giampaolo, Morgan Kaufmann, 1999) — the canonical BFS on-disk reference by its author</description></item>
///   <item><description><c>https://github.com/haiku/haiku/tree/master/src/add-ons/kernel/file_systems/bfs</c> — Haiku's maintained BFS implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Be_File_System</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class BfsFormatDescriptor
    : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable,
      IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty {

  public string Id => "Bfs";
  public string DisplayName => "BFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".bfs";
  public IReadOnlyList<string> Extensions => [".bfs", ".img"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // '1SFB' at offset 544 (offset 32 into the superblock at sector 1)
    new([0x31, 0x53, 0x46, 0x42], Offset: 544, Confidence: 0.35),
    // '1SFB' at offset 32 (no-MBR rewrap)
    new([0x31, 0x53, 0x46, 0x42], Offset: 32, Confidence: 0.30),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "BeOS / Haiku BFS filesystem image";

  // ── IArchiveFormatOperations ────────────────────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new BfsReader(stream);
      return r.Entries
        .Where(e => !e.IsDirectory)
        .Select((e, i) => new ArchiveEntryInfo(
          i, e.Name, e.Size, e.Size, "Stored", false, false, null
        )).ToList();
    } catch {
      // Fallback: surface raw image + metadata like the old R-only descriptor
      return ListFallback(stream);
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    try {
      var r = new BfsReader(stream);
      foreach (var e in r.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
        WriteFile(outputDir, e.Name, r.Extract(e));
      }
    } catch {
      // Fallback: emit raw image + metadata
      ExtractFallback(stream, outputDir, files);
    }
  }

  // ── IArchiveCreatable ──────────────────────────────────────────────

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
    var r = new BfsReader(archive);
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
    var w = new BfsWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var info = i;
      // Only the length is needed to lay the volume out; reading a large input
      // into a byte[] would cap the volume at what an array can hold.
      // Full path, not the leaf: BFS nests directories, and flattening made a
      // streamed create diverge from the buffered one.
      if (info.InMemoryContent is { } bytes)
        w.AddFile(info.ArchiveName, bytes);
      else
        w.AddStreamingFile(info.ArchiveName, new FileInfo(info.FullPath).Length,
                           () => File.OpenRead(info.FullPath));
    }
    if (output.CanSeek) w.BuildToStreaming(output);
    else output.Write(w.Build());
  }

  /// <summary>
  /// Two-pass streaming creation: the pre-known per-input sizes drive the BFS
  /// block allocation + inode + B+ tree layout in pass 1 (identical to
  /// <see cref="Create"/>); pass 2 streams each file's bytes from its
  /// <see cref="Compression.Registry.Streaming.StreamingArchiveInput.OpenStream"/>
  /// factory into its contiguous data-block run via 64 KB chunks — no file is
  /// ever buffered as a <c>byte[]</c>. Output is byte-identical to
  /// <see cref="Create"/> for the same inputs. Falls back to a buffered build
  /// when the target stream is not seekable.
  /// </summary>
  public void CreateFromStreams(Stream output, IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new BfsWriter();
    if (!output.CanSeek) {
      foreach (var input in inputs) {
        if (input.IsDirectory) continue;
        using var src = input.OpenStream();
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        w.AddFile(input.Name, ms.ToArray());
      }
      output.Write(w.Build());
      return;
    }
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      w.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }
    w.BuildToStreaming(output);
  }

  // ── IArchiveModifiable (true in-place R/W) ────────────────────────
  //
  // BfsInPlaceModifier flips inode + B+ tree leaf + AG bitmap bits at fixed
  // sector offsets and leaves every untouched block byte-identical to the
  // original image. When the requested change exceeds the MVP scope (root
  // leaf would split, subdirectory write, no contiguous run available), the
  // modifier falls back to ModifyRebuilder so the user always gets a
  // working image.

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => BfsInPlaceModifier.Add(archive, inputs,
        (a, i) => ModifyRebuilder.Add(a, i, ReadEntries, BuildImage));

  public void Remove(Stream archive, string[] entryNames)
    => BfsInPlaceModifier.Remove(archive, entryNames,
        (a, n) => ModifyRebuilder.Remove(a, n, ReadEntries, BuildImage));

  // ── IArchiveDefragmentable (rebuild-based) ─────────────────────────

  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options, ReadEntries, BuildImage);

  // ── IFilesystemExtentMap ───────────────────────────────────────────

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var imageBytes = ms.ToArray();

    var sb = BfsSuperblock.TryParse(imageBytes);
    if (!sb.Valid) yield break;

    var blockSize = (int)sb.BlockSize;
    if (blockSize < 512) yield break;

    // Superblock
    yield return new DefragBlockInfo(sb.SuperblockOffset, 1024, DefragBlockKind.MetadataReserved, "Superblock");

    // Log area (blocks 2..9 for offset-0 images, or similar)
    var logRun = ReadBlockRunFromImage(imageBytes, sb.SuperblockOffset + 88);
    if (logRun.Length > 0) {
      var logOffset = (long)logRun.Start * blockSize;
      var logSize = (long)logRun.Length * blockSize;
      yield return new DefragBlockInfo(logOffset, logSize, DefragBlockKind.MetadataReserved, "Journal/Log");
    }

    // AG bitmap (block 10 for our images — right after log)
    var agBitmapBlock = logRun.Start + logRun.Length;
    yield return new DefragBlockInfo((long)agBitmapBlock * blockSize, blockSize, DefragBlockKind.MetadataReserved, "AG Bitmap");

    // Root dir inode + B+ tree
    var rootRun = ReadBlockRunFromImage(imageBytes, sb.SuperblockOffset + 116);
    if (rootRun.Length > 0) {
      yield return new DefragBlockInfo((long)rootRun.Start * blockSize, blockSize, DefragBlockKind.MetadataReserved, "Root Dir Inode");
      // Read root inode data_stream.direct[0] for B+ tree block
      var rootInodeOff = rootRun.Start * blockSize;
      if (rootInodeOff + 72 + 8 <= imageBytes.Length) {
        var btreeRun = ReadBlockRunFromImage(imageBytes, rootInodeOff + 72);
        if (btreeRun.Length > 0)
          yield return new DefragBlockInfo((long)btreeRun.Start * blockSize, blockSize, DefragBlockKind.MetadataReserved, "Root Dir B+Tree");
      }
    }

    // Indices dir inode + B+ tree
    var idxRun = ReadBlockRunFromImage(imageBytes, sb.SuperblockOffset + 124);
    if (idxRun.Length > 0) {
      yield return new DefragBlockInfo((long)idxRun.Start * blockSize, blockSize, DefragBlockKind.MetadataReserved, "Indices Dir Inode");
      var idxInodeOff = idxRun.Start * blockSize;
      if (idxInodeOff + 72 + 8 <= imageBytes.Length) {
        var btreeRun = ReadBlockRunFromImage(imageBytes, idxInodeOff + 72);
        if (btreeRun.Length > 0)
          yield return new DefragBlockInfo((long)btreeRun.Start * blockSize, blockSize, DefragBlockKind.MetadataReserved, "Indices B+Tree");
      }
    }

    // File extents — walk the B+ tree leaf
    BfsReader reader;
    try {
      image.Position = 0;
      reader = new BfsReader(image);
    } catch {
      yield break;
    }

    foreach (var entry in reader.Entries) {
      var inodeOff = entry.InodeBlock * blockSize;
      // Inode block itself (and, for directories, the B+ tree node it points at)
      yield return new DefragBlockInfo(inodeOff, blockSize, DefragBlockKind.MetadataReserved, $"Inode: {entry.Name}");

      if (entry.IsDirectory) {
        if (inodeOff + 72 + 8 <= imageBytes.Length) {
          var dirBtree = ReadBlockRunFromImage(imageBytes, inodeOff + 72);
          if (dirBtree.Length > 0)
            yield return new DefragBlockInfo((long)dirBtree.Start * blockSize, (long)dirBtree.Length * blockSize,
              DefragBlockKind.MetadataReserved, $"Dir B+Tree: {entry.Name}");
        }
        continue;
      }

      // Data blocks from direct extents
      if (entry.Size > 0 && inodeOff + 72 + NumDirectBlocks * 8 <= imageBytes.Length) {
        var remaining = entry.Size;
        for (var i = 0; i < NumDirectBlocks && remaining > 0; i++) {
          var run = ReadBlockRunFromImage(imageBytes, inodeOff + 72 + i * 8);
          if (run.Length == 0) break;
          var runBytes = Math.Min((long)run.Length * blockSize, remaining);
          yield return new DefragBlockInfo((long)run.Start * blockSize, runBytes, DefragBlockKind.Used, entry.Name);
          remaining -= runBytes;
        }
      }
    }
  }

  private const int NumDirectBlocks = 12;

  // ── IWipeEmpty ─────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in a BFS image: every block not claimed by a live
  /// inode, B+ tree node, journal, bitmap or file data run — and the cluster-tip
  /// slack inside the last data block of each file. The BFS extent map emits a
  /// file's data run clamped to its logical size, so the trailing bytes of its
  /// final allocated block fall outside any live extent and are zeroed by the
  /// generic <see cref="UnusedSpaceWiper"/> as free space; the file-size lookup
  /// covers any reader that reports a block-aligned extent.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    Func<string, long>? fileSizeLookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        var reader = new BfsReader(image);
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
    var extents = EnumerateExtents(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup);
  }

  // ── Shared delegates ───────────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadEntries(Stream stream) {
    var r = new BfsReader(stream);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e)));
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new BfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  // ── Fallback for malformed images ──────────────────────────────────

  private static List<ArchiveEntryInfo> ListFallback(Stream stream) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      image = ms.ToArray();
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.bfs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var sb = BfsSuperblock.TryParse(image);
    entries.Add(new ArchiveEntryInfo(0, "FULL.bfs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(2, "superblock.bin", sb.RawBytes.LongLength, sb.RawBytes.LongLength, "stored", false, false, null));
    return entries;
  }

  private static void ExtractFallback(Stream stream, string outputDir, string[]? files) {
    byte[] image;
    try {
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      image = ms.ToArray();
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    BfsSuperblock sb;
    try {
      sb = BfsSuperblock.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.bfs", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.bfs", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid)
      WriteIfMatch(outputDir, "superblock.bin", sb.RawBytes, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(BfsSuperblock sb) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"superblock_offset={sb.SuperblockOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volume_name={sb.Name}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_size={sb.BlockSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"num_blocks={sb.NumBlocks}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"used_blocks={sb.UsedBlocks}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"num_ags={sb.NumAgs}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"root_dir_ino={sb.RootDirIno}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"indices_dir_ino={sb.IndicesDirIno}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic1_ok={(sb.Magic1Value == BfsSuperblock.Magic1)}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic2_ok={(sb.Magic2Value == BfsSuperblock.Magic2)}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic3_ok={(sb.Magic3Value == BfsSuperblock.Magic3)}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  private static (uint Ag, int Start, int Length) ReadBlockRunFromImage(byte[] image, int offset) {
    if (offset + 8 > image.Length) return (0, 0, 0);
    var ag = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset));
    var start = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset + 4));
    var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset + 6));
    return (ag, start, length);
  }
}
