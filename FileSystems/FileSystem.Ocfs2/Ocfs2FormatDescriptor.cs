#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ocfs2;

/// <summary>
/// R/W descriptor for OCFS2 (Oracle Cluster Filesystem 2).
/// Supports: list, extract, create (WORM), modify (rebuild-based), defragment, extent map.
/// The writer produces a single-node (no DLM) image with 4 KB blocks/clusters,
/// inline directory entries, and extent-based file data allocation.
/// </summary>
public sealed class Ocfs2FormatDescriptor
    : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
      IArchiveModifiable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty {
  public string Id => "Ocfs2";
  public string DisplayName => "OCFS2 (Oracle Cluster Filesystem 2)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ocfs2";
  public IReadOnlyList<string> Extensions => [".ocfs2"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(Ocfs2Superblock.SignatureBytes, Offset: (int)Ocfs2Superblock.DefaultSuperBlockOffset, Confidence: 0.85),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "OCFS2 (Oracle Cluster Filesystem 2) — read/write with extent-based allocation.";

  // ── IArchiveFormatOperations (List / Extract) ─────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    // Try the writer-produced image path first (has real file entries)
    try {
      var image = ReadAllFull(stream);
      var files = ReadFilesFromImage(image);
      if (files.Count > 0)
        return files.Select((f, i) => new ArchiveEntryInfo(
          i, f.Name, f.Data.LongLength, f.Data.LongLength, "stored", false, false, null
        )).ToList();
    } catch {
      // Fall through to triage path
    }

    // Reset stream position for triage path
    if (stream.CanSeek) stream.Position = 0;
    // Triage path: superblock surface only
    return ListTriage(stream);
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    // Try the writer-produced image path first
    try {
      var image = ReadAllFull(stream);
      var fileEntries = ReadFilesFromImage(image);
      if (fileEntries.Count > 0) {
        foreach (var f in fileEntries) {
          if (files != null && files.Length > 0 && !MatchesFilter(f.Name, files)) continue;
          WriteFile(outputDir, f.Name, f.Data);
        }
        return;
      }
    } catch {
      // Fall through to triage path
    }

    // Reset stream position for triage path
    if (stream.CanSeek) stream.Position = 0;
    ExtractTriage(stream, outputDir, files);
  }

  // ── IArchiveCreatable ─────────────────────────────────────────────────

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new Ocfs2Writer();
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }

  // ── IArchiveModifiable (rebuild-based) ────────────────────────────────

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => ModifyRebuilder.Add(archive, inputs, ReadFileEntries, BuildImage);

  public void Remove(Stream archive, string[] entryNames)
    => ModifyRebuilder.Remove(archive, entryNames, ReadFileEntries, BuildImage);

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
    const int blockSize = Ocfs2Writer.BlockSize;

    if (data.Length < (Ocfs2Writer.SuperBlockBlkno + 1) * blockSize) return result;

    // Reserved blocks 0-1
    result.Add(new DefragBlockInfo(0, blockSize * 2, DefragBlockKind.MetadataReserved, "Reserved"));

    // Superblock at block 2
    result.Add(new DefragBlockInfo(Ocfs2Writer.SuperBlockBlkno * blockSize, blockSize, DefragBlockKind.MetadataReserved, "Superblock"));

    // Global bitmap dinode at block 3
    if (data.Length >= (Ocfs2Writer.GlobalBitmapBlkno + 1) * blockSize)
      result.Add(new DefragBlockInfo(Ocfs2Writer.GlobalBitmapBlkno * blockSize, blockSize, DefragBlockKind.MetadataReserved, "GlobalBitmap"));

    // Bitmap data at block 4
    if (data.Length >= (Ocfs2Writer.BitmapDataBlkno + 1) * blockSize)
      result.Add(new DefragBlockInfo(Ocfs2Writer.BitmapDataBlkno * blockSize, blockSize, DefragBlockKind.MetadataReserved, "BitmapData"));

    // Root dir at block 5
    if (data.Length >= (Ocfs2Writer.RootDirBlkno + 1) * blockSize)
      result.Add(new DefragBlockInfo(Ocfs2Writer.RootDirBlkno * blockSize, blockSize, DefragBlockKind.MetadataReserved, "RootDir"));

    // System dir at block 6
    if (data.Length >= (Ocfs2Writer.SystemDirBlkno + 1) * blockSize)
      result.Add(new DefragBlockInfo(Ocfs2Writer.SystemDirBlkno * blockSize, blockSize, DefragBlockKind.MetadataReserved, "SystemDir"));

    // Inode alloc at block 7
    if (data.Length >= (Ocfs2Writer.InodeAllocBlkno + 1) * blockSize)
      result.Add(new DefragBlockInfo(Ocfs2Writer.InodeAllocBlkno * blockSize, blockSize, DefragBlockKind.MetadataReserved, "InodeAlloc"));

    // File dinodes and data
    try {
      var files = ReadFilesFromImage(data);
      var nextBlk = (long)Ocfs2Writer.FirstFileBlkno;
      // Each file has a dinode block
      for (var i = 0; i < files.Count; i++) {
        var dinodeOff = nextBlk * blockSize;
        result.Add(new DefragBlockInfo(dinodeOff, blockSize, DefragBlockKind.MetadataReserved, $"Inode: {files[i].Name}"));
        nextBlk++;
      }
      // Then data blocks
      for (var i = 0; i < files.Count; i++) {
        if (files[i].Data.Length > 0) {
          var dataOff = nextBlk * blockSize;
          var clusters = (files[i].Data.Length + blockSize - 1) / blockSize;
          result.Add(new DefragBlockInfo(dataOff, Math.Min((long)clusters * blockSize, files[i].Data.Length), DefragBlockKind.Used, files[i].Name));
          nextBlk += clusters;
        }
      }
    } catch {
      // Best-effort
    }

    return result;
  }

  // ── IWipeEmpty ────────────────────────────────────────────────────────

  /// <summary>
  /// Zeros all unused space in the OCFS2 image: unallocated clusters and the
  /// cluster-tip slack between a file's logical size and the end of its last
  /// 4 KB cluster. The extent map already clamps each Used data extent to the
  /// file's logical length, so the cluster tip surfaces as a free gap and is
  /// zeroed by the generic wiper without a size lookup. Small directories are
  /// stored inline inside their dinode (no data cluster), so they have no tip.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    image.Position = 0;
    var extents = EnumerateExtents(image);

    // No fileSizeLookup is needed: the OCFS2 extent map reports each file's
    // Used data extent at its logical length, leaving the cluster tip exposed
    // as a free gap that Wipe zero-fills.
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, fileSizeLookup: null);
  }

  // ── Shared helpers ────────────────────────────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadFileEntries(Stream stream) {
    var image = ReadAllFull(stream);
    return ReadFilesFromImage(image);
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var w = new Ocfs2Writer();
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  /// <summary>
  /// Reads file entries from an OCFS2 image by walking the root directory dinode's
  /// inline directory entries, recursing into subdirectories, and extracting file
  /// data from extent records. Files are surfaced at their full nested path.
  /// </summary>
  private static List<(string Name, byte[] Data)> ReadFilesFromImage(byte[] image) {
    var result = new List<(string Name, byte[] Data)>();

    var rootOff = Ocfs2Writer.RootDirBlkno * Ocfs2Writer.BlockSize;
    if (rootOff + Ocfs2Writer.BlockSize > image.Length) return result;

    // Verify signature of the root directory dinode.
    if (!image.AsSpan(rootOff, 6).SequenceEqual(Ocfs2Superblock.SignatureBytes))
      return result;

    var visited = new HashSet<long>();
    WalkDirectory(image, Ocfs2Writer.RootDirBlkno, "", result, visited);
    return result;
  }

  /// <summary>
  /// Walks an inline directory dinode, appending regular files (with their full
  /// path prefix) to <paramref name="result"/> and recursing into subdirectories.
  /// "." / ".." and already-visited inodes are skipped to avoid cycles.
  /// </summary>
  private static void WalkDirectory(
      byte[] image, long dirBlkno, string prefix,
      List<(string Name, byte[] Data)> result, HashSet<long> visited) {
    const int blockSize = Ocfs2Writer.BlockSize;
    const int id2Off = 0xC0;

    if (!visited.Add(dirBlkno)) return;

    var dirOff = (int)(dirBlkno * blockSize);
    if (dirOff + blockSize > image.Length) return;
    if (!image.AsSpan(dirOff, 6).SequenceEqual(Ocfs2Superblock.SignatureBytes)) return;

    // Collect subdirectories to recurse into after this directory is fully read.
    var subdirs = new List<(long Blkno, string Path)>();

    // Inline data flag (i_dyn_features at +0x4C). Inline directories keep their
    // ocfs2_dir_entry records in the dinode's id2 area; extent-backed
    // directories store them in data blocks referenced by an extent list.
    var dynFeatures = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(dirOff + 0x4C, 2));
    if ((dynFeatures & 0x0001) != 0) {
      var inlineStart = dirOff + id2Off + 2; // after id_count (u16)
      var dirSize = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(dirOff + 0x1C, 8));
      var inlineEnd = inlineStart + Math.Min(dirSize, blockSize - id2Off - 2);
      ParseDirEntries(image, inlineStart, inlineEnd, prefix, result, subdirs);
    } else {
      // Extent-backed: walk the extent list (id2) and parse each directory block.
      var extOff = dirOff + id2Off;
      var nextFreeRec = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(extOff + 4, 2));
      for (var i = 0; i < nextFreeRec; i++) {
        var recOff = extOff + 8 + i * 16;
        if (recOff + 16 > image.Length) break;
        var clusters = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(recOff + 4, 2));
        var blkno = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(recOff + 8, 8));
        for (var c = 0; c < clusters; c++) {
          var blockStart = (int)((blkno + c) * blockSize);
          if (blockStart + blockSize > image.Length) break;
          ParseDirEntries(image, blockStart, blockStart + blockSize, prefix, result, subdirs);
        }
      }
    }

    foreach (var (blkno, path) in subdirs)
      WalkDirectory(image, blkno, path, result, visited);
  }

  /// <summary>
  /// Parses a run of <c>ocfs2_dir_entry</c> records in [start, end): for each
  /// record, regular files are appended to <paramref name="result"/> and
  /// subdirectories collected into <paramref name="subdirs"/>. "." / ".." are
  /// skipped. rec_len drives advancement; a zero/too-short rec_len ends the run
  /// (e.g. a block whose final entry was stretched to the boundary).
  /// </summary>
  private static void ParseDirEntries(
      byte[] image, int start, int end, string prefix,
      List<(string Name, byte[] Data)> result, List<(long Blkno, string Path)> subdirs) {
    var cursor = start;
    while (cursor + 12 <= end && cursor + 12 <= image.Length) {
      var inode = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(cursor, 8));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(cursor + 8, 2));
      var nameLen = image[cursor + 10];
      var fileType = image[cursor + 11];

      if (recLen < 12 || cursor + recLen > end) break;
      if (inode == 0 || nameLen == 0 || cursor + 12 + nameLen > image.Length) {
        cursor += recLen;
        continue;
      }

      var name = Encoding.UTF8.GetString(image, cursor + 12, nameLen);
      cursor += recLen;

      if (name is "." or "..") continue;
      var path = prefix.Length == 0 ? name : prefix + "/" + name;

      if (fileType == 1)
        result.Add((path, ExtractFileData(image, (long)inode)));
      else if (fileType == 2)
        subdirs.Add(((long)inode, path));
    }
  }

  /// <summary>Extracts file data from a file dinode's extent records.</summary>
  private static byte[] ExtractFileData(byte[] image, long dinodeBlkno) {
    const int blockSize = Ocfs2Writer.BlockSize;
    const int id2Off = 0xC0;

    var off = (int)(dinodeBlkno * blockSize);
    if (off + blockSize > image.Length) return [];

    // Verify signature
    if (!image.AsSpan(off, 6).SequenceEqual(Ocfs2Superblock.SignatureBytes))
      return [];

    var fileSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(off + 0x1C, 8));
    if (fileSize == 0) return [];

    // Read extent list
    var extOff = off + id2Off;
    var nextFreeRec = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(extOff + 4, 2));

    var result = new byte[fileSize];
    var resultPos = 0;

    for (var i = 0; i < nextFreeRec; i++) {
      var recOff = extOff + 8 + i * 16;
      if (recOff + 24 > image.Length) break;

      var clusters = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(recOff + 4, 2));
      var blkno = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(recOff + 8, 8));

      var dataOff = (int)(blkno * blockSize);
      var dataLen = clusters * blockSize;
      var copyLen = (int)Math.Min(dataLen, fileSize - resultPos);
      if (dataOff + copyLen > image.Length) copyLen = Math.Max(0, image.Length - dataOff);
      if (copyLen > 0)
        Buffer.BlockCopy(image, dataOff, result, resultPos, copyLen);
      resultPos += copyLen;
    }

    return result;
  }

  // ── Triage path (for non-writer-produced images) ──────────────────────

  private List<ArchiveEntryInfo> ListTriage(Stream stream) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.ocfs2", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    Ocfs2Superblock sb;
    try {
      sb = Ocfs2Superblock.TryParse(image);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.ocfs2", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.ocfs2", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "superblock.bin", sb.HeaderRaw.LongLength, sb.HeaderRaw.LongLength, "stored", false, false, null));
    return entries;
  }

  private void ExtractTriage(Stream stream, string outputDir, string[]? files) {
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    Ocfs2Superblock sb;
    try {
      sb = Ocfs2Superblock.TryParse(image);
    } catch {
      WriteIfMatch(outputDir, "FULL.ocfs2", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.ocfs2", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid)
      WriteIfMatch(outputDir, "superblock.bin", sb.HeaderRaw, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(Ocfs2Superblock sb) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    if (sb.Valid) {
      b.Append(ic, $"superblock_offset={sb.SuperBlockOffset}\n");
      b.Append(ic, $"detected_blocksize={sb.DetectedBlockSize}\n");
      b.Append(ic, $"version_major={sb.MajorRev}\n");
      b.Append(ic, $"version_minor={sb.MinorRev}\n");
      b.Append(ic, $"version={sb.MajorRev}.{sb.MinorRev}\n");
      b.Append(ic, $"blocksize_bits={sb.BlocksizeBits}\n");
      b.Append(ic, $"clustersize_bits={sb.ClustersizeBits}\n");
      b.Append(ic, $"blocksize={(sb.BlocksizeBits is >= 9 and <= 16 ? 1u << (int)sb.BlocksizeBits : 0)}\n");
      b.Append(ic, $"clustersize={(sb.ClustersizeBits is >= 12 and <= 24 ? 1u << (int)sb.ClustersizeBits : 0)}\n");
      b.Append(ic, $"max_slots={sb.MaxSlots}\n");
      b.Append(ic, $"root_blkno={sb.RootBlkno}\n");
      b.Append(ic, $"system_dir_blkno={sb.SystemDirBlkno}\n");
      b.Append(ic, $"first_cluster_group={sb.FirstClusterGroup}\n");
      b.Append(ic, $"label={sb.Label}\n");
      b.Append(ic, $"uuid_hex={sb.UuidHex}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }

  private static byte[] ReadAllFull(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
