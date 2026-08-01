#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using Compression.Core.DiskImage;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Yaffs2;

/// <summary>
/// R/W descriptor for YAFFS2 raw-NAND images. Auto-detects chunk/spare
/// layout, surfaces an object table and reconstructed file tree.
/// <para>
/// <b>Modify semantics — true in-place, log-structured.</b> YAFFS2 is a
/// log-structured flash filesystem by spec: modifying a file means appending
/// fresh chunks at the next free position with a higher seqNumber, never
/// rewriting an existing chunk on the medium. <see cref="Add"/> and
/// <see cref="Remove"/> route through <see cref="Yaffs2InPlaceModifier"/>,
/// which appends at <see cref="Stream.Length"/> and never touches bytes in
/// <c>[0, oldLength)</c>. The scanner resolves the live view by keeping the
/// chunk with the highest seqNumber per (objectId, chunkId), and treats a
/// header with <c>parent_obj_id == 0xFFFFFFFE</c> as a tombstone.
/// </para>
/// Supports: list, extract, create, in-place modify, defragment, extent map.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://yaffs.net/</c> — project home — hosts "How YAFFS Works" and the spec documents</description></item>
///   <item><description>Charles Manning, "How YAFFS Works" (yaffs.net documentation)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/YAFFS</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class Yaffs2FormatDescriptor
    : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveShrinkable,
      IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty, ILayoutOptimizable {
  public string Id => "Yaffs2";
  public string DisplayName => "YAFFS2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".yaffs2";
  public IReadOnlyList<string> Extensions => [".yaffs2", ".yaffs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // YAFFS2 has no superblock, but every image starts with chunk 0 holding the
    // root directory's object header: type YAFFS_OBJECT_TYPE_DIRECTORY (3) and
    // parent object id 1, followed by the zeroed checksum and an empty name.
    // Weak on its own, so the confidence stays below anything with real magic —
    // it is only here so an extensionless image is not claimed by a signature
    // that is weaker still.
    new([0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
      Offset: 0, Confidence: 0.50),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Yet Another Flash File System v2 (raw NAND image) — read/write with mkyaffs2image-compatible layout.";

  // ── IArchiveFormatOperations (List / Extract) ─────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    ImageAccessor image;
    try {
      if (stream.CanSeek) stream.Position = 0;
      image = new ImageAccessor(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.yaffs2", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    using var _ = image;
    entries.Add(new ArchiveEntryInfo(0, "FULL.yaffs2", image.Length, image.Length, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));

    Yaffs2Scanner.ScanResult scan;
    try {
      scan = Yaffs2Scanner.Scan(image);
    } catch {
      return entries;
    }

    if (scan.Objects.Count > 0)
      entries.Add(new ArchiveEntryInfo(entries.Count, "directory_tree.txt", 0, 0, "stored", false, false, null));

    // Reconstruct paths for files we have data for.
    var paths = BuildPaths(scan);
    foreach (var obj in scan.Objects) {
      if (obj.Type != Yaffs2Scanner.YObjectType.File) continue;
      if (!scan.DataChunks.TryGetValue(obj.ObjectId, out var chunks) || chunks.Count == 0) continue;
      var path = paths.TryGetValue(obj.ObjectId, out var p) ? p : obj.Name;
      if (string.IsNullOrEmpty(path)) continue;
      var size = chunks.Sum(c => (long)c.Length);
      entries.Add(new ArchiveEntryInfo(entries.Count, "files/" + path, size, size, "stored", false, false, null));
    }
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ImageAccessor image;
    try {
      if (stream.CanSeek) stream.Position = 0;
      image = new ImageAccessor(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    using var _ = image;
    if (files == null || files.Length == 0 || MatchesFilter("FULL.yaffs2", files)) {
      var full = Path.Combine(outputDir, "FULL.yaffs2");
      Directory.CreateDirectory(Path.GetDirectoryName(full) ?? outputDir);
      using var raw = File.Create(full);
      image.CopyTo(0, raw, image.Length);
    }

    Yaffs2Scanner.ScanResult scan;
    try {
      scan = Yaffs2Scanner.Scan(image);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(scan), files);
    if (scan.Objects.Count > 0)
      WriteIfMatch(outputDir, "directory_tree.txt", BuildTree(scan), files);

    var paths = BuildPaths(scan);
    foreach (var obj in scan.Objects) {
      if (obj.Type != Yaffs2Scanner.YObjectType.File) continue;
      if (!scan.DataChunks.TryGetValue(obj.ObjectId, out var chunks) || chunks.Count == 0) continue;
      var path = paths.TryGetValue(obj.ObjectId, out var p) ? p : obj.Name;
      if (string.IsNullOrEmpty(path)) continue;

      if (files != null && files.Length > 0 && !MatchesFilter("files/" + path, files)) continue;
      var target = Path.Combine(outputDir,
        ("files/" + path).Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
      using var output = File.Create(target);
      CopyChunks(image, chunks, obj.Size, output);
    }
  }

  /// <summary>
  /// Opens a single file entry as a bounded stream over the object's reassembled
  /// data chunks. Accepts the path with or without the <c>files/</c> prefix used
  /// by <see cref="Extract"/>. Reads past the entry's logical size return 0 (EOF).
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    try {
      using var image = new ImageAccessor(archive);
      var scan = Yaffs2Scanner.Scan(image);
      if (!scan.ParseOk) goto Empty;
      var paths = BuildPaths(scan);
      var target = entryName.StartsWith("files/", StringComparison.Ordinal) ? entryName[6..] : entryName;
      foreach (var obj in scan.Objects) {
        if (obj.Type != Yaffs2Scanner.YObjectType.File) continue;
        if (!scan.DataChunks.TryGetValue(obj.ObjectId, out var chunks) || chunks.Count == 0) continue;
        var path = paths.TryGetValue(obj.ObjectId, out var p) ? p : obj.Name;
        if (string.IsNullOrEmpty(path)) continue;
        if (!string.Equals(path, target, StringComparison.OrdinalIgnoreCase)
          && !string.Equals("files/" + path, entryName, StringComparison.OrdinalIgnoreCase)) continue;
        // Spilled to scratch that deletes itself on close, so an entry larger
        // than memory still opens as an ordinary stream.
        var scratch = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
          FileShare.None, 81920, FileOptions.DeleteOnClose);
        var written = CopyChunks(image, chunks, obj.Size, scratch);
        scratch.Position = 0;
        return new BoundedEntryStream(scratch, written, leaveOpen: false);
      }
    } catch {
      // fall through
    }
  Empty:
    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  // ── IArchiveCreatable ─────────────────────────────────────────────────

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new Yaffs2Writer();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }

  // ── IArchiveModifiable (TRUE in-place, log-structured) ────────────────
  //
  // Per the YAFFS2 spec, these append fresh chunks at the image tail with a
  // higher seqNumber. Existing chunks in [0, oldLength) stay byte-identical;
  // the scanner's seqNumber-max filter resolves the live view. Add detects
  // name collisions and routes them through Replace.

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    // The in-place modifier walks the volume in memory, which a volume past two
    // gigabytes does not fit in. Above that the edit unpacks and relays it out.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.AddLargeVolume(archive, inputs, this, this, SyntheticNames);
      return;
    }

    Yaffs2InPlaceModifier.Add(archive, inputs);
  }

  public void Remove(Stream archive, string[] entryNames) {
    // See Add: past two gigabytes the volume cannot be walked in memory.
    if (ModifyRebuilder.NeedsLargeVolumePath(archive)) {
      ModifyRebuilder.RemoveLargeVolume(archive, entryNames, this, this, SyntheticNames);
      return;
    }

    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames) {
      if (string.IsNullOrEmpty(name)) continue;
      // Tolerate the "files/" prefix the descriptor uses on extract.
      var bare = name.StartsWith("files/", StringComparison.Ordinal) ? name[6..] : name;
      // Tolerate full nested paths by mapping to the leaf — the in-place modifier
      // matches root-level objects by leaf name.
      var leaf = Path.GetFileName(bare);
      if (string.IsNullOrEmpty(leaf)) continue;
      try {
        Yaffs2InPlaceModifier.Remove(archive, leaf);
      } catch (InvalidOperationException) {
        // No live object by that name — silently skip, matching the rebuild path's behavior.
      }
    }
  }

  // ── IArchiveDefragmentable ────────────────────────────────────────────

  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Rewrites the log with one current chunk per file and nothing else — the
  /// garbage collection a real YAFFS2 does in the background. Files stream
  /// through scratch, so a volume larger than an array can hold still packs.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    // Every consolidate mode lands on the same layout here: the writer emits a
    // fresh volume packed from the first data block, and has no way to place
    // files against the tail. Carving a hole is the one request it cannot meet.
    if (options.Mode is DefragMode.CarveHole)
      throw new NotSupportedException(
        "YAFFS2 defragmentation cannot carve a hole: the rebuild always start-packs the volume.");

    var tempPath = Path.GetTempFileName();
    var spill = new List<string>();
    try {
      using (var temp = File.Open(tempPath, FileMode.Open, FileAccess.ReadWrite)) {
        var w = new Yaffs2Writer();
        if (archive.CanSeek) archive.Position = 0;
        using (var image = new ImageAccessor(archive)) {
          var scan = Yaffs2Scanner.Scan(image);
          if (scan.ParseOk) {
            var paths = BuildPaths(scan);
            foreach (var obj in scan.Objects) {
              if (obj.Type != Yaffs2Scanner.YObjectType.File) continue;
              if (!scan.DataChunks.TryGetValue(obj.ObjectId, out var chunks) || chunks.Count == 0) continue;
              var path = paths.TryGetValue(obj.ObjectId, out var p) ? p : obj.Name;
              if (string.IsNullOrEmpty(path)) continue;

              var scratchPath = Path.GetTempFileName();
              spill.Add(scratchPath);
              long size;
              using (var scratch = File.Create(scratchPath))
                size = CopyChunks(image, chunks, obj.Size, scratch);
              var captured = scratchPath;
              w.AddStreamingFile(path, size, () => File.OpenRead(captured));
            }
          }
        }
        w.WriteTo(temp);

        options.OnProgress?.Invoke(new DefragProgressEvent(
          Phase: "commit", Fraction: 1.0, CurrentReadOffset: archive.Length,
          CurrentWriteOffset: temp.Length, ImageSize: temp.Length, BlockMap: null));

        temp.Position = 0;
        archive.Position = 0;
        temp.CopyTo(archive);
        archive.SetLength(temp.Length);
        archive.Flush();
      }
    } finally {
      File.Delete(tempPath);
      foreach (var path in spill)
        try { File.Delete(path); } catch { /* scratch file already gone */ }
    }
  }

  // ── IFilesystemExtentMap ──────────────────────────────────────────────

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    try {
      if (image.CanSeek) image.Position = 0;
      using var data = new ImageAccessor(image);
      return EnumerateExtentsCore(data);
    } catch {
      return [];
    }
  }

  private static List<DefragBlockInfo> EnumerateExtentsCore(ImageAccessor data) {
    var result = new List<DefragBlockInfo>();

    // Try to detect the layout
    var scan = Yaffs2Scanner.Scan(data);
    if (!scan.ParseOk || scan.ChunkSize == 0) return result;

    var stride = scan.ChunkSize + scan.SpareSize;
    var paths = BuildPaths(scan);
    var objectNames = new Dictionary<int, string>();
    foreach (var obj in scan.Objects) {
      var path = paths.TryGetValue(obj.ObjectId, out var p) ? p : obj.Name;
      objectNames[obj.ObjectId] = path;
    }

    // Walk chunks and classify them
    var spareBuf = new byte[scan.SpareSize];
    for (var off = 0L; off + stride <= data.Length; off += stride) {
      data.Read(off + scan.ChunkSize, spareBuf.AsSpan());
      var spare = spareBuf.AsSpan();
      var (objId, chunkId, _) = ParseSpare(spare);

      if (chunkId == 0) {
        // Object header — metadata
        var name = objectNames.TryGetValue(objId, out var n) ? n : $"obj:{objId}";
        result.Add(new DefragBlockInfo(off, stride, DefragBlockKind.MetadataReserved, $"header:{name}"));
      } else if (objId != 0) {
        // Data chunk
        var name = objectNames.TryGetValue(objId, out var n) ? n : $"obj:{objId}";
        result.Add(new DefragBlockInfo(off, stride, DefragBlockKind.Used, name));
      } else {
        // Unrecognized or empty
        result.Add(new DefragBlockInfo(off, stride, DefragBlockKind.Free));
      }
    }

    // Trailing bytes
    var consumed = (data.Length / stride) * stride;
    if (consumed < data.Length)
      result.Add(new DefragBlockInfo(consumed, data.Length - consumed, DefragBlockKind.Free));

    return result;
  }

  private static (int ObjId, int ChunkId, uint NBytes) ParseSpare(ReadOnlySpan<byte> spare) {
    if (spare.Length < 16) return (0, 0, 0);
    try {
      var objId = BinaryPrimitives.ReadInt32LittleEndian(spare.Slice(4, 4));
      var chunkId = BinaryPrimitives.ReadInt32LittleEndian(spare.Slice(8, 4));
      var nBytes = BinaryPrimitives.ReadUInt32LittleEndian(spare.Slice(12, 4));
      if (objId is < 0 or > 1_000_000) objId = 0;
      if (chunkId is < 0 or > 1_000_000) chunkId = 0;
      return (objId, chunkId, nBytes);
    } catch {
      return (0, 0, 0);
    }
  }

  // ── IWipeEmpty ────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros every free (unallocated) chunk in a YAFFS2 raw-NAND image while
  /// leaving live object headers and data chunks untouched.
  /// <para>YAFFS2 is log-structured: each 2 KiB data chunk carries its used
  /// byte count in its packed-tags2 spare, and the unused tail of a partial
  /// chunk is an inseparable part of the logged chunk (not in-place slack).
  /// A file's data therefore spans many same-named chunk extents, so a
  /// per-file size lookup cannot map to a single extent — cluster-tip wiping
  /// is not applicable. Free chunks (and trailing bytes) are scrubbed; the
  /// <paramref name="wipeClusterTips"/> flag has no effect.</para>
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    long wiped = 0;

    // Log-structured forensic pass: a delete only appends a tombstone header, so the
    // deleted object's data chunks + old headers linger in the NAND log until GC.
    // Zero those obsolete chunks so deleted content can't be recovered; live objects'
    // current chunks are left intact (the reader walks by fixed stride and skips
    // blanked slots).
    if (wipeDeletedEntries)
      wiped += Yaffs2ForensicWiper.WipeObsolete(image);

    image.Position = 0;
    var imageSize = image.Length;
    var extents = this.EnumerateExtents(image).ToList();

    // Then zero genuine free chunks (no in-place tip slack in a per-chunk log).
    image.Position = 0;
    wiped += UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
    return wiped;
  }

  // ── Shared helpers ────────────────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadFileEntries(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var image = new ImageAccessor(stream);
    var scan = Yaffs2Scanner.Scan(image);
    if (!scan.ParseOk) yield break;

    var paths = BuildPaths(scan);
    foreach (var obj in scan.Objects) {
      if (obj.Type != Yaffs2Scanner.YObjectType.File) continue;
      if (!scan.DataChunks.TryGetValue(obj.ObjectId, out var chunks) || chunks.Count == 0) continue;
      var path = paths.TryGetValue(obj.ObjectId, out var p) ? p : obj.Name;
      if (string.IsNullOrEmpty(path)) continue;
      // Preserve the full nested path so the writer rebuilds the directory tree.
      using var buffer = new MemoryStream();
      CopyChunks(image, chunks, obj.Size, buffer);
      yield return (path, buffer.ToArray());
    }
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new Yaffs2Writer();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  /// <summary>
  /// Copies an object's live chunks, in chunk-id order, into
  /// <paramref name="destination" />, stopping at the header's declared size.
  /// Returns the number of bytes written.
  /// </summary>
  private static long CopyChunks(ImageAccessor image, List<Yaffs2Scanner.ChunkRef> chunks,
      long declaredSize, Stream destination) {
    var total = 0L;
    foreach (var c in chunks) total += c.Length;
    var target = declaredSize > 0 && declaredSize < total ? declaredSize : total;

    var written = 0L;
    foreach (var c in chunks) {
      var take = Math.Min(c.Length, target - written);
      if (take <= 0) break;
      image.CopyTo(c.Offset, destination, take);
      written += take;
    }
    return written;
  }

  private static Dictionary<int, string> BuildPaths(Yaffs2Scanner.ScanResult scan) {
    var byId = new Dictionary<int, Yaffs2Scanner.ObjectEntry>();
    foreach (var o in scan.Objects) byId[o.ObjectId] = o;
    var paths = new Dictionary<int, string>();
    foreach (var o in scan.Objects) {
      var segments = new List<string>();
      var cur = o;
      var guard = 0;
      while (cur != null && guard++ < 256) {
        if (string.IsNullOrEmpty(cur.Name)) break;
        segments.Add(cur.Name);
        if (cur.ParentId == 1 || cur.ParentId == 0 || cur.ParentId == cur.ObjectId) break;
        if (!byId.TryGetValue(cur.ParentId, out var parent)) break;
        cur = parent;
      }
      segments.Reverse();
      paths[o.ObjectId] = string.Join('/', segments);
    }
    return paths;
  }

  private static byte[] BuildMetadata(Yaffs2Scanner.ScanResult scan) {
    var sb = new StringBuilder();
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(scan.ParseOk ? "ok" : "partial")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"chunk_size={scan.ChunkSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"spare_size={scan.SpareSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"chosen_layout={scan.ChunkSize}+{scan.SpareSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"object_count={scan.Objects.Count}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildTree(Yaffs2Scanner.ScanResult scan) {
    var sb = new StringBuilder();
    foreach (var o in scan.Objects)
      sb.Append(CultureInfo.InvariantCulture, $"{o.ObjectId}\t{o.ParentId}\t{o.Type}\t{o.Name}\t{o.Size}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// The entries this reader surfaces that are not files on the volume — the
  /// raw image and its triage sheets — so an image the parser cannot fully
  /// walk still yields something useful.
  /// </summary>
  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.OrdinalIgnoreCase) { "FULL.yaffs2", "metadata.ini", "directory_tree.txt" };

  /// <summary>
  /// Re-lays the volume out with the requested geometry. The generic default
  /// wrote the synthetic entries back as files, so the rebuilt volume listed
  /// more entries than the original and the rebuild was refused.
  /// </summary>
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);

    var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
    if (options.Parameters != null)
      foreach (var kv in options.Parameters)
        parameters[kv.Key] = kv.Value;

    RebuildVerb.RebuildToStream(source, target, this, this,
      parameters.Count > 0 ? parameters : null, SyntheticNames);
  }

}
