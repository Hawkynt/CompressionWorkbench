#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
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
/// </summary>
public sealed class Yaffs2FormatDescriptor
    : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
      IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty {
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
    // No true magic. Detection is primarily by extension, so no signatures registered.
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Yet Another Flash File System v2 (raw NAND image) — read/write with mkyaffs2image-compatible layout.";

  // ── IArchiveFormatOperations (List / Extract) ─────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.yaffs2", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.yaffs2", image.LongLength, image.LongLength, "stored", false, false, null));
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
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    WriteIfMatch(outputDir, "FULL.yaffs2", image, files);

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

      var data = Concat(chunks, obj.Size);
      WriteIfMatch(outputDir, "files/" + path, data, files);
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
      var image = ReadAll(archive);
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
        var data = Concat(chunks, obj.Size);
        return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
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

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => Yaffs2InPlaceModifier.Add(archive, inputs);

  public void Remove(Stream archive, string[] entryNames) {
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

  public void Defragment(Stream archive, DefragOptions options)
    => DefragRebuilder.Rebuild(archive, options, ReadFileEntries, BuildImage);

  // ── IFilesystemExtentMap ──────────────────────────────────────────────

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    byte[] data;
    try {
      image.Position = 0;
      using var ms = new MemoryStream();
      image.CopyTo(ms);
      data = ms.ToArray();
    } catch {
      return [];
    }

    return EnumerateExtentsCore(data);
  }

  private static List<DefragBlockInfo> EnumerateExtentsCore(byte[] data) {
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
    for (var off = 0; off + stride <= data.Length; off += stride) {
      var spare = data.AsSpan(off + scan.ChunkSize, scan.SpareSize);
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
    image.Position = 0;
    var imageSize = image.Length;

    image.Position = 0;
    var extents = this.EnumerateExtents(image).ToList();

    // Log-structured per-chunk layout — no in-place tip slack. Wipe free chunks only.
    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips: false, fileSizeLookup: null);
  }

  // ── Shared helpers ────────────────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadFileEntries(Stream stream) {
    var image = ReadAll(stream);
    var scan = Yaffs2Scanner.Scan(image);
    if (!scan.ParseOk) yield break;

    var paths = BuildPaths(scan);
    foreach (var obj in scan.Objects) {
      if (obj.Type != Yaffs2Scanner.YObjectType.File) continue;
      if (!scan.DataChunks.TryGetValue(obj.ObjectId, out var chunks) || chunks.Count == 0) continue;
      var path = paths.TryGetValue(obj.ObjectId, out var p) ? p : obj.Name;
      if (string.IsNullOrEmpty(path)) continue;
      // Preserve the full nested path so the writer rebuilds the directory tree.
      yield return (path, Concat(chunks, obj.Size));
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

  private static byte[] Concat(List<byte[]> chunks, long declaredSize) {
    var total = chunks.Sum(c => (long)c.Length);
    var targetLen = declaredSize > 0 && declaredSize < total ? (int)declaredSize : (int)total;
    var result = new byte[targetLen];
    var pos = 0;
    foreach (var c in chunks) {
      var take = Math.Min(c.Length, targetLen - pos);
      if (take <= 0) break;
      Buffer.BlockCopy(c, 0, result, pos, take);
      pos += take;
    }
    return result;
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
}
