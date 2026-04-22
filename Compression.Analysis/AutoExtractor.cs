using Compression.Core.DiskImage;
using Compression.Registry;

namespace Compression.Analysis;

/// <summary>
/// Automatically detects and extracts archive contents, with recursive nested archive support.
/// Supports disk image → partition table → filesystem recursive descent.
/// </summary>
public sealed class AutoExtractor {
  private readonly int _maxDepth;
  private readonly long _maxFileSize;
  private readonly long _maxPartitionSize;

  /// <summary>
  /// Default recursion budgets. The previous <c>MaxDepth=5</c> was tight for chains like
  /// GZ→TAR→VMDK→FAT32→MKV→video track (6 levels before we even reach media demux);
  /// 10 covers those headline scenarios without blowing up pathological inputs.
  /// </summary>
  public AutoExtractor(int maxDepth = 10, long maxFileSize = 256 * 1024 * 1024, long maxPartitionSize = 512 * 1024 * 1024) {
    this._maxDepth = maxDepth;
    this._maxFileSize = maxFileSize;
    this._maxPartitionSize = maxPartitionSize;
  }

  /// <summary>
  /// Format IDs that represent virtual disk images whose extracted "disk.img" contains raw disk data
  /// that may have a partition table.
  /// </summary>
  private static readonly HashSet<string> DiskImageFormatIds = new(StringComparer.OrdinalIgnoreCase) {
    "Vhd", "Vmdk", "Qcow2", "Vdi"
  };

  /// <summary>
  /// Result of an auto-extraction operation.
  /// </summary>
  public sealed class ExtractionResult {
    /// <summary>Detected format Id.</summary>
    public required string FormatId { get; init; }

    /// <summary>Display name of the detected format.</summary>
    public required string FormatName { get; init; }

    /// <summary>Extracted entries.</summary>
    public required List<ExtractedEntry> Entries { get; init; }

    /// <summary>Nested extraction results (for entries that are themselves archives).</summary>
    public required List<NestedResult> NestedResults { get; init; }

    /// <summary>Partition information if a partition table was detected in a disk image.</summary>
    public PartitionTableInfo? PartitionTable { get; init; }
  }

  /// <summary>
  /// A single extracted entry.
  /// </summary>
  public sealed class ExtractedEntry {
    /// <summary>Entry name/path within the archive.</summary>
    public required string Name { get; init; }

    /// <summary>Extracted data.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Whether this is a directory entry.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Conventional kind tag (Track/Channel/Plane/Tag/File); null for untagged entries.</summary>
    public string? Kind { get; init; }
  }

  /// <summary>
  /// A nested archive extraction result.
  /// </summary>
  public sealed class NestedResult {
    /// <summary>Name of the entry that contained the nested archive.</summary>
    public required string EntryName { get; init; }

    /// <summary>Extraction result for the nested archive.</summary>
    public required ExtractionResult Result { get; init; }
  }

  /// <summary>
  /// Information about a partition table detected inside a disk image.
  /// </summary>
  public sealed class PartitionTableInfo {
    /// <summary>Partition table scheme: "GPT" or "MBR".</summary>
    public required string Scheme { get; init; }

    /// <summary>List of partition details.</summary>
    public required List<PartitionInfo> Partitions { get; init; }
  }

  /// <summary>
  /// Details about a single partition within a disk image.
  /// </summary>
  public sealed class PartitionInfo {
    /// <summary>Partition index.</summary>
    public required int Index { get; init; }

    /// <summary>Partition type name (e.g. "NTFS", "Linux", "FAT32").</summary>
    public required string TypeName { get; init; }

    /// <summary>Partition size in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>Byte offset within the disk image.</summary>
    public required long Offset { get; init; }

    /// <summary>Extraction results from recursively processing this partition (if a filesystem was detected).</summary>
    public ExtractionResult? NestedResult { get; init; }
  }

  /// <summary>
  /// Detects the format of the given stream and extracts all entries.
  /// If entries are themselves archives, recursively extracts them up to the configured
  /// max depth. For disk image formats, also detects partition tables and recursively descends
  /// into filesystems.
  /// </summary>
  public ExtractionResult? Extract(Stream stream, int depth = 0) {
    if (depth >= this._maxDepth) return null;

    Compression.Lib.FormatRegistration.EnsureInitialized();

    var bestDesc = DetectFormat(stream);
    if (bestDesc == null) return null;

    var archiveOps = FormatRegistry.GetArchiveOps(bestDesc.Id);
    if (archiveOps != null)
      return this.ExtractArchive(archiveOps, bestDesc, stream, depth);

    var streamOps = FormatRegistry.GetStreamOps(bestDesc.Id);
    if (streamOps != null)
      return this.ExtractStream(streamOps, bestDesc, stream, depth);

    return null;
  }

  private static IFormatDescriptor? DetectFormat(Stream stream) {
    var headerBuf = new byte[Math.Min(4096, stream.Length)];
    var origPos = stream.Position;
    var headerLen = stream.Read(headerBuf, 0, headerBuf.Length);
    stream.Position = origPos;

    var header = headerBuf.AsSpan(0, headerLen);
    IFormatDescriptor? best = null;
    var bestConf = 0.0;
    foreach (var desc in FormatRegistry.All) {
      foreach (var sig in desc.MagicSignatures) {
        if (sig.Offset + sig.Bytes.Length > header.Length) continue;
        var match = true;
        for (var j = 0; j < sig.Bytes.Length; ++j) {
          var mask = sig.Mask != null && j < sig.Mask.Length ? sig.Mask[j] : (byte)0xFF;
          if ((header[sig.Offset + j] & mask) != (sig.Bytes[j] & mask)) { match = false; break; }
        }
        if (match && sig.Confidence > bestConf) {
          bestConf = sig.Confidence;
          best = desc;
        }
      }
    }
    return best;
  }

  /// <summary>
  /// Follows <paramref name="path"/> (slash-separated segments) through nested archives,
  /// returning the bytes of the leaf entry. Each segment is the name of an entry in
  /// the archive at that layer — the descent stops when the named entry is a leaf.
  /// Uses <see cref="IArchiveInMemoryExtract.ExtractEntry"/> where possible to avoid
  /// disk roundtrips.
  /// </summary>
  public byte[]? ExtractPath(Stream stream, string path) {
    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0) return null;

    var currentBytes = ReadAll(stream);
    foreach (var segment in segments) {
      using var ms = new MemoryStream(currentBytes);
      var desc = DetectFormat(ms);
      if (desc == null) return null;
      var ops = FormatRegistry.GetArchiveOps(desc.Id);
      if (ops == null) {
        // Try stream decompression as an implicit "whole-content" fallthrough.
        var streamOps = FormatRegistry.GetStreamOps(desc.Id);
        if (streamOps == null) return null;
        ms.Position = 0;
        using var decompressed = new MemoryStream();
        streamOps.Decompress(ms, decompressed);
        currentBytes = decompressed.ToArray();
        continue;
      }
      ms.Position = 0;
      using var next = new MemoryStream();
      if (ops is IArchiveInMemoryExtract inMem) {
        inMem.ExtractEntry(ms, segment, next, null);
      } else {
        // Fall back to temp-dir path for formats without the in-memory capability.
        ms.Position = 0;
        var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_path_" + Guid.NewGuid().ToString("N")[..8]);
        try {
          Directory.CreateDirectory(tmpDir);
          ops.Extract(ms, tmpDir, null, [segment]);
          var hit = Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories).FirstOrDefault();
          if (hit == null) return null;
          currentBytes = File.ReadAllBytes(hit);
          continue;
        } finally {
          try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
        }
      }
      currentBytes = next.ToArray();
    }
    return currentBytes;
  }

  private static byte[] ReadAll(Stream s) {
    using var ms = new MemoryStream();
    s.Position = 0;
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private ExtractionResult ExtractArchive(IArchiveFormatOperations ops, IFormatDescriptor desc, Stream stream, int depth) {
    var entries = new List<ExtractedEntry>();
    var nested = new List<NestedResult>();
    PartitionTableInfo? partitionTable = null;

    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_autoextract_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      stream.Position = 0;
      ops.Extract(stream, tmpDir, null, null);

      // Also collect entry Kinds where the descriptor exposes them via List().
      stream.Position = 0;
      var kindByName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
      try {
        foreach (var e in ops.List(stream, null))
          kindByName[e.Name] = e.Kind;
      } catch { /* best effort — some formats only support Extract */ }

      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var relPath = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        var data = File.ReadAllBytes(file);
        kindByName.TryGetValue(relPath, out var kind);
        entries.Add(new ExtractedEntry { Name = relPath, Data = data, Kind = kind });

        if (DiskImageFormatIds.Contains(desc.Id) && data.Length >= 1024)
          partitionTable ??= this.TryParsePartitions(data, depth);

        if (data.Length > 0 && data.Length <= this._maxFileSize) {
          using var nestedStream = new MemoryStream(data);
          var nestedResult = this.Extract(nestedStream, depth + 1);
          if (nestedResult != null)
            nested.Add(new NestedResult { EntryName = relPath, Result = nestedResult });
        }
      }
    } finally {
      try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
    }

    return new ExtractionResult {
      FormatId = desc.Id,
      FormatName = desc.DisplayName,
      Entries = entries,
      NestedResults = nested,
      PartitionTable = partitionTable
    };
  }

  private ExtractionResult ExtractStream(IStreamFormatOperations ops, IFormatDescriptor desc, Stream stream, int depth) {
    stream.Position = 0;
    using var output = new MemoryStream();
    ops.Decompress(stream, output);
    var data = output.ToArray();

    var entries = new List<ExtractedEntry> {
      new() { Name = "decompressed", Data = data }
    };

    var nested = new List<NestedResult>();
    if (data.Length > 0 && data.Length <= this._maxFileSize) {
      using var nestedStream = new MemoryStream(data);
      var nestedResult = this.Extract(nestedStream, depth + 1);
      if (nestedResult != null)
        nested.Add(new NestedResult { EntryName = "decompressed", Result = nestedResult });
    }

    return new ExtractionResult {
      FormatId = desc.Id,
      FormatName = desc.DisplayName,
      Entries = entries,
      NestedResults = nested
    };
  }

  /// <summary>
  /// Maps partition type names (from <see cref="PartitionTypeDatabase"/>) to format IDs
  /// that can be tried when magic-based detection fails. Many filesystem formats
  /// (FAT, exFAT) lack magic signatures and can only be identified by partition type.
  /// </summary>
  private static readonly Dictionary<string, string[]> PartitionTypeToFormatIds = new(StringComparer.OrdinalIgnoreCase) {
    { "FAT12", ["Fat"] },
    { "FAT16 (<32MB)", ["Fat"] },
    { "FAT16 (>32MB)", ["Fat"] },
    { "FAT16 (LBA)", ["Fat"] },
    { "FAT32 (CHS)", ["Fat"] },
    { "FAT32 (LBA)", ["Fat"] },
    { "NTFS/exFAT/HPFS", ["Ntfs", "ExFat"] },
    { "Linux", ["Ext"] },
    { "Linux Filesystem", ["Ext"] },
    { "macOS HFS+", ["HfsPlus"] },
    { "Apple HFS+", ["HfsPlus"] },
    { "Microsoft Basic Data", ["Ntfs", "ExFat", "Fat"] },
    { "EFI System Partition", ["Fat"] },
  };

  private PartitionTableInfo? TryParsePartitions(byte[] diskData, int depth) {
    if (depth + 1 >= this._maxDepth) return null;

    var detection = PartitionTableDetector.Detect(diskData);
    if (detection.Scheme == "None" || detection.Partitions.Count == 0)
      return null;

    var partitions = new List<PartitionInfo>();
    foreach (var part in detection.Partitions) {
      ExtractionResult? nestedResult = null;

      if (part.Size > 0 && part.Size <= this._maxPartitionSize) {
        var partData = PartitionTableDetector.ExtractPartitionData(diskData, part);
        if (partData.Length > 0) {
          using var partStream = new MemoryStream(partData);
          nestedResult = this.Extract(partStream, depth + 1);

          if (nestedResult == null)
            nestedResult = this.TryFilesystemByPartitionType(partData, part.TypeName, depth);
        }
      }

      partitions.Add(new PartitionInfo {
        Index = part.Index,
        TypeName = part.TypeName,
        Size = part.Size,
        Offset = part.StartOffset,
        NestedResult = nestedResult
      });
    }

    return new PartitionTableInfo {
      Scheme = detection.Scheme,
      Partitions = partitions
    };
  }

  private ExtractionResult? TryFilesystemByPartitionType(byte[] partData, string typeName, int depth) {
    if (!PartitionTypeToFormatIds.TryGetValue(typeName, out var formatIds))
      return null;

    foreach (var formatId in formatIds) {
      var archiveOps = FormatRegistry.GetArchiveOps(formatId);
      if (archiveOps == null) continue;

      var desc = FormatRegistry.GetById(formatId);
      if (desc == null) continue;

      try {
        using var stream = new MemoryStream(partData);
        return this.ExtractArchive(archiveOps, desc, stream, depth + 1);
      } catch {
        // This format didn't work for the partition data. Try the next one.
      }
    }

    return null;
  }
}
