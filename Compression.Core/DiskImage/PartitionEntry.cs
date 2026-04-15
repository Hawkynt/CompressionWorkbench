namespace Compression.Core.DiskImage;

/// <summary>
/// Represents a partition table entry (MBR or GPT).
/// </summary>
public sealed class PartitionEntry {
  /// <summary>Zero-based partition index.</summary>
  public required int Index { get; init; }

  /// <summary>Byte offset from the start of the disk to the partition data.</summary>
  public required long StartOffset { get; init; }

  /// <summary>Size of the partition in bytes.</summary>
  public required long Size { get; init; }

  /// <summary>Human-readable filesystem/type name (e.g. "NTFS", "Linux ext4", "FAT32").</summary>
  public required string TypeName { get; init; }

  /// <summary>Whether this partition is marked as active/bootable.</summary>
  public bool IsActive { get; init; }

  /// <summary>Raw type code (MBR: single byte, GPT: GUID string).</summary>
  public required string TypeCode { get; init; }

  /// <summary>Partition label/name (GPT only, empty for MBR).</summary>
  public string Name { get; init; } = "";

  /// <summary>Source: "MBR", "GPT", or "EBR" (extended boot record).</summary>
  public required string Source { get; init; }
}
