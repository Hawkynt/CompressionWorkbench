#pragma warning disable CS1591
namespace FileSystem.Udf;

/// <summary>
/// Represents a UDF filesystem entry.
/// </summary>
public sealed class UdfEntry {
  /// <summary>Gets the decoded path relative to the filesystem root.</summary>
  public string Name { get; init; } = "";
  /// <summary>Gets the logical file size.</summary>
  public long Size { get; init; }
  /// <summary>Gets whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }
  /// <summary>Gets the last modification timestamp when present.</summary>
  public DateTime? LastModified { get; init; }

  // Legacy contiguous offset retained for callers that only need the common
  // one-extent profile. Native mounted reads use DataSegments instead.
  internal long DataOffset { get; init; }
  internal long DataLength { get; init; }
  internal IReadOnlyList<UdfDataSegment> DataSegments { get; init; } = [];
  internal string? MountLimitation { get; init; }
}

/// <summary>
/// One logical range of a UDF file. Recorded ranges map to an image byte
/// offset; unrecorded allocated/unallocated ranges are represented as zeroes.
/// </summary>
internal readonly record struct UdfDataSegment(
  long LogicalOffset,
  long Length,
  long PhysicalOffset,
  bool ZeroFill
);
