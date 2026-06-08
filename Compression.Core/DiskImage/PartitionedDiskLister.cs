using Compression.Registry;

namespace Compression.Core.DiskImage;

/// <summary>
/// Partition-aware archive surface for raw disk image streams (VHD/VHDX/VMDK/Qcow2/VDI
/// guest-disk views). When a valid MBR or GPT partition table is detected, presents
/// each partition as a top-level directory <c>PartitionN_TypeName/</c> whose contents
/// are the inner filesystem's entries (delegated through <see cref="InnerFsDetector"/>).
/// When no partition table is present, returns <c>null</c> so the caller can fall
/// through to the existing single-FS-at-offset-0 path.
/// </summary>
public static class PartitionedDiskLister {

  /// <summary>
  /// Lists entries across every partition. Returns <c>null</c> if no partition
  /// table is present so the caller can fall through to the unpartitioned path.
  /// </summary>
  public static List<ArchiveEntryInfo>? List(Stream disk, string? password) {
    var pt = PartitionTableDetector.Detect(disk);
    if (pt.Partitions.Count == 0) return null;

    var result = new List<ArchiveEntryInfo>();
    var idx = 0;
    foreach (var part in pt.Partitions) {
      var prefix = MakePartitionPrefix(part);
      using var window = new PartitionWindowStream(disk, part.StartOffset, part.Size);

      var inner = InnerFsDetector.Detect(window);
      if (inner is IArchiveFormatOperations ops) {
        try {
          window.Position = 0;
          foreach (var e in ops.List(window, password)) {
            result.Add(e with { Index = idx++, Name = $"{prefix}/{e.Name}" });
          }
          continue;
        } catch {
          // inner-FS read failed — fall through to raw partition entry
        }
      }

      // No inner FS detected (or list failed) — emit the partition as one raw blob.
      result.Add(new ArchiveEntryInfo(
        idx++, $"{prefix}.raw", part.Size, part.Size, "Stored",
        IsDirectory: false, IsEncrypted: false, LastModified: null,
        Kind: $"{pt.Scheme}-partition"));
    }
    return result;
  }

  /// <summary>
  /// Extracts entries across every partition into per-partition subdirectories of
  /// <paramref name="outputDir"/>. Returns <c>true</c> if a partition table was
  /// detected and handled; <c>false</c> if no partition table was found (caller
  /// falls through to the unpartitioned path).
  /// </summary>
  public static bool Extract(Stream disk, string outputDir, string? password, string[]? files) {
    var pt = PartitionTableDetector.Detect(disk);
    if (pt.Partitions.Count == 0) return false;

    foreach (var part in pt.Partitions) {
      var prefix = MakePartitionPrefix(part);
      var partFilter = files?.Where(f => f.StartsWith(prefix + "/", StringComparison.Ordinal))
                              .Select(f => f[(prefix.Length + 1)..]).ToArray();
      // If a filter was supplied and nothing targets this partition, skip it entirely.
      if (files != null && (partFilter?.Length ?? 0) == 0 && !files.Contains($"{prefix}.raw")) continue;

      using var window = new PartitionWindowStream(disk, part.StartOffset, part.Size);
      var partOut = Path.Combine(outputDir, prefix);

      var inner = InnerFsDetector.Detect(window);
      if (inner is IArchiveFormatOperations ops) {
        try {
          window.Position = 0;
          ops.Extract(window, partOut, password, partFilter);
          continue;
        } catch {
          // fall through to raw dump
        }
      }

      // Unrecognised FS or read failure — dump the partition bytes raw.
      Directory.CreateDirectory(partOut);
      using var raw = File.Create(Path.Combine(partOut, "raw.bin"));
      window.Position = 0;
      window.CopyTo(raw);
    }
    return true;
  }

  private static string MakePartitionPrefix(PartitionEntry part) {
    var safeName = string.Concat(part.TypeName.Select(c =>
      char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
    if (string.IsNullOrEmpty(safeName)) safeName = "raw";
    return $"Partition{part.Index + 1}_{safeName}";
  }
}
