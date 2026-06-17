#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ocfs2;

/// <summary>
/// R/W descriptor for OCFS2 (Oracle Cluster Filesystem 2).
/// Supports: list, extract, create, true in-place modify (Add/Replace/Remove via
/// <see cref="Ocfs2InPlaceModifier"/>), defragment, extent map.
///
/// <para><b>Reading</b> is spec-correct against <c>fs/ocfs2/ocfs2_fs.h</c> (see
/// <see cref="Ocfs2Reader"/>): INODE01 dinode signatures, the real
/// <c>ocfs2_dinode</c> field offsets, the 8-byte <c>ocfs2_inline_data</c> header,
/// and the 16-byte extent-list header. It reads images produced by the reference
/// <c>mkfs.ocfs2</c> as well as the toolkit's own writer (verified by an external
/// conformance test that reads a real <c>mkfs.ocfs2 -M local</c> volume).</para>
///
/// <para><b>Writing</b> produces a single-node (no DLM) image with 4 KB
/// blocks/clusters, inline directory entries, and extent-based file data. The
/// superblock and dinode layout are spec-correct — the reference
/// <c>debugfs.ocfs2 stats</c> reads the written superblock at exit 0. The writer
/// does NOT yet emit the full chain-allocator / journal / slot-map / local-alloc
/// system-file suite a mountable volume needs, so <c>fsck.ocfs2</c> does not pass
/// on a written image. Create/modify are therefore scoped to structurally-correct
/// construction with self/round-trip readback, not fsck-clean conformance.</para>
///
/// <para>Modifier scope: root-directory mutations only (subdirectory and
/// extent-backed root directory paths fall back to the rebuild path). DLM/heartbeat
/// lockdown and multi-node cluster semantics are out of scope by design.</para>
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
    "OCFS2 (Oracle Cluster Filesystem 2) — spec-correct reader (INODE01 dinodes, "
    + "real ocfs2_dinode offsets, 8-byte inline-data header, 16-byte extent-list "
    + "header) that parses real mkfs.ocfs2 images as well as our own; extent-based "
    + "writer with true in-place Add/Replace/Remove on the root directory via "
    + "Ocfs2InPlaceModifier (O(touched bytes) random-access I/O). Written superblock "
    + "is read by the reference debugfs.ocfs2, but the writer does not yet emit the "
    + "full journal/chain-allocator system files, so written images are not yet "
    + "fsck.ocfs2-clean/mountable. Subdirectory and extent-backed-root mutations fall "
    + "back to the rebuild path. Single-node only — DLM/heartbeat lockdown and "
    + "multi-node cluster semantics are out of scope.";

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

  // ── IArchiveModifiable (true in-place R/W) ────────────────────────────

  /// <summary>
  /// Adds (or replaces by name) files in the root directory of an existing
  /// OCFS2 image using <see cref="Ocfs2InPlaceModifier"/>. Touches only the
  /// global bitmap data block, the root dir dinode, the new file dinode block,
  /// and the new data blocks — no whole-image rewrite. Subdirectory paths and
  /// extent-backed root directories fall back to the rebuild path so callers
  /// keep working when the writer's MVP scope is exceeded.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      try {
        // Replace-by-name semantics — drop any prior entry with the same name first.
        Ocfs2InPlaceModifier.RemoveFile(archive, name, wipeData: true);
        Ocfs2InPlaceModifier.AddFile(archive, name, data);
      } catch (NotSupportedException) {
        // Subdir path, extent-backed root dir, or full inline area — fall back
        // to the rebuild path so callers still get the file added.
        ModifyRebuilder.Add(archive, [ArchiveInputInfo.InMemory(name, data)], ReadFileEntries, BuildImage);
      } catch (IOException) {
        // No free clusters / no inline room — same fall-back rationale.
        ModifyRebuilder.Add(archive, [ArchiveInputInfo.InMemory(name, data)], ReadFileEntries, BuildImage);
      }
    }
  }

  /// <summary>
  /// Removes files from the root directory of an existing OCFS2 image using
  /// <see cref="Ocfs2InPlaceModifier"/>. Frees the dinode block + data
  /// clusters via global bitmap bit flips and zero-wipes them so no forensic
  /// trace remains. Names that aren't in the root directory fall back to the
  /// rebuild path (which can reach subdirectories).
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    var unhandled = new List<string>();
    foreach (var name in entryNames) {
      try {
        if (!Ocfs2InPlaceModifier.RemoveFile(archive, name, wipeData: true))
          unhandled.Add(name);
      } catch (NotSupportedException) {
        unhandled.Add(name);
      }
    }
    if (unhandled.Count > 0)
      ModifyRebuilder.Remove(archive, [.. unhandled], ReadFileEntries, BuildImage);
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
    const int blockSize = Ocfs2Writer.BlockSize;

    if (data.Length < (Ocfs2Writer.SuperBlockBlkno + 1) * blockSize) return result;

    var totalBlocks = data.Length / blockSize;

    // Identify each regular file's data-cluster run so it can surface as a Used
    // extent (clamped to logical size, leaving the cluster tip as a free gap),
    // rather than being lumped into the reserved metadata region.
    var fileDataClusters = new Dictionary<long, (long Size, string Name)>();
    try {
      foreach (var f in Ocfs2Reader.ReadFilePlacements(data)) {
        if (f.Inline || f.Size <= 0) continue;
        fileDataClusters[f.DataBlkno] = (f.Size, f.Name);
      }
    } catch { /* best-effort */ }

    // Every cluster set in the global bitmap (block 3 group descriptor) is
    // allocated. Mark allocated clusters as reserved — except file data clusters,
    // which are emitted as Used extents — so the wiper never zeroes live metadata.
    var bmpOff = Ocfs2Writer.GlobalBitmapGroupBlkno * blockSize + Ocfs2Writer.BitmapInGroupOffset;
    long cluster = 0;
    while (cluster < totalBlocks) {
      if (fileDataClusters.TryGetValue(cluster, out var file)) {
        var clusters = (file.Size + blockSize - 1) / blockSize;
        var dataOff = cluster * blockSize;
        // Used portion clamped to logical size; the remaining tip is left as a gap.
        result.Add(new DefragBlockInfo(dataOff, file.Size, DefragBlockKind.Used, file.Name));
        cluster += clusters;
        continue;
      }
      var byteIdx = bmpOff + (int)(cluster >> 3);
      var used = byteIdx < data.Length && (data[byteIdx] & (1 << (int)(cluster & 7))) != 0;
      if (used) {
        // Coalesce a run of reserved clusters.
        var runStart = cluster;
        while (cluster < totalBlocks
               && !fileDataClusters.ContainsKey(cluster)
               && (bmpOff + (int)(cluster >> 3)) < data.Length
               && (data[bmpOff + (int)(cluster >> 3)] & (1 << (int)(cluster & 7))) != 0)
          cluster++;
        result.Add(new DefragBlockInfo(runStart * blockSize, (cluster - runStart) * blockSize,
          DefragBlockKind.MetadataReserved, runStart < Ocfs2Writer.FirstFileBlkno ? "SystemMetadata" : "Allocated"));
        continue;
      }
      cluster++;
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
  /// Reads file entries from an OCFS2 image. Delegates to <see cref="Ocfs2Reader"/>,
  /// which is spec-correct against <c>fs/ocfs2/ocfs2_fs.h</c> (INODE01 dinode
  /// signature, the real dinode field offsets, the 8-byte inline-data header, and
  /// the 16-byte extent-list header) so it reads images produced by the reference
  /// <c>mkfs.ocfs2</c> as well as the toolkit's own writer. Files are surfaced at
  /// their full nested path.
  /// </summary>
  private static List<(string Name, byte[] Data)> ReadFilesFromImage(byte[] image)
    => Ocfs2Reader.ReadFiles(image);

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
