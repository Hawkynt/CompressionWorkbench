using Compression.Core.DiskImage;
using Compression.Registry;

namespace Compression.Analysis;

/// <summary>
/// Automatically detects and extracts archive contents, with recursive nested archive support.
/// Supports disk image → partition table → filesystem recursive descent.
/// </summary>
public sealed class AutoExtractor {
  private const int MaxDepth = 5;
  private const int MaxFileSize = 256 * 1024 * 1024; // 256 MB
  private const int MaxPartitionSize = 512 * 1024 * 1024; // 512 MB — partitions can be larger than single entries

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
  /// If entries are themselves archives, recursively extracts them up to <see cref="MaxDepth"/> levels.
  /// For disk image formats, also detects partition tables and recursively descends into filesystems.
  /// </summary>
  public ExtractionResult? Extract(Stream stream, int depth = 0) {
    if (depth >= MaxDepth) return null;

    Compression.Lib.FormatRegistration.EnsureInitialized();

    // Read header for detection.
    var headerBuf = new byte[Math.Min(4096, stream.Length)];
    var origPos = stream.Position;
    var headerLen = stream.Read(headerBuf, 0, headerBuf.Length);
    stream.Position = origPos;

    // Detect format.
    var header = headerBuf.AsSpan(0, headerLen);
    IFormatDescriptor? bestDesc = null;
    var bestConf = 0.0;

    foreach (var desc in FormatRegistry.All) {
      foreach (var sig in desc.MagicSignatures) {
        if (sig.Offset + sig.Bytes.Length > header.Length) continue;
        var match = true;
        for (var j = 0; j < sig.Bytes.Length; j++) {
          var mask = sig.Mask != null && j < sig.Mask.Length ? sig.Mask[j] : (byte)0xFF;
          if ((header[sig.Offset + j] & mask) != (sig.Bytes[j] & mask)) { match = false; break; }
        }
        if (match && sig.Confidence > bestConf) {
          bestConf = sig.Confidence;
          bestDesc = desc;
        }
      }
    }

    if (bestDesc == null) return null;

    // Try archive extraction.
    var archiveOps = FormatRegistry.GetArchiveOps(bestDesc.Id);
    if (archiveOps != null) {
      return ExtractArchive(archiveOps, bestDesc, stream, depth);
    }

    // Try stream decompression.
    var streamOps = FormatRegistry.GetStreamOps(bestDesc.Id);
    if (streamOps != null) {
      return ExtractStream(streamOps, bestDesc, stream, depth);
    }

    return null;
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

      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var relPath = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        var data = File.ReadAllBytes(file);
        entries.Add(new ExtractedEntry { Name = relPath, Data = data });

        // For disk image formats, check if the extracted raw disk data contains a partition table.
        if (DiskImageFormatIds.Contains(desc.Id) && data.Length >= 1024) {
          partitionTable ??= TryParsePartitions(data, depth);
        }

        // Try recursive extraction on each entry.
        if (data.Length > 0 && data.Length <= MaxFileSize) {
          using var nestedStream = new MemoryStream(data);
          var nestedResult = Extract(nestedStream, depth + 1);
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
    if (data.Length > 0 && data.Length <= MaxFileSize) {
      using var nestedStream = new MemoryStream(data);
      var nestedResult = Extract(nestedStream, depth + 1);
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

  /// <summary>
  /// Attempts to detect a partition table in raw disk data and recursively process each partition.
  /// </summary>
  private PartitionTableInfo? TryParsePartitions(byte[] diskData, int depth) {
    if (depth + 1 >= MaxDepth) return null;

    var detection = PartitionTableDetector.Detect(diskData);
    if (detection.Scheme == "None" || detection.Partitions.Count == 0)
      return null;

    var partitions = new List<PartitionInfo>();
    foreach (var part in detection.Partitions) {
      ExtractionResult? nestedResult = null;

      // Only attempt to process partitions within our size limit.
      if (part.Size > 0 && part.Size <= MaxPartitionSize) {
        var partData = PartitionTableDetector.ExtractPartitionData(diskData, part);
        if (partData.Length > 0) {
          // First, try magic-based detection via the normal Extract pipeline.
          using var partStream = new MemoryStream(partData);
          nestedResult = Extract(partStream, depth + 1);

          // If magic detection failed, try known filesystem formats based on partition type.
          if (nestedResult == null) {
            nestedResult = TryFilesystemByPartitionType(partData, part.TypeName, depth);
          }
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

  /// <summary>
  /// Tries to open partition data as a known filesystem format based on the partition type name.
  /// This handles formats like FAT that have no magic signatures.
  /// </summary>
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
        return ExtractArchive(archiveOps, desc, stream, depth + 1);
      } catch {
        // This format didn't work for the partition data. Try the next one.
      }
    }

    return null;
  }
}
