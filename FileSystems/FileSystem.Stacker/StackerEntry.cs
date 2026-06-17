#pragma warning disable CS1591
namespace FileSystem.Stacker;

public sealed class StackerEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }

  /// <summary>Byte offset into the host file for opaque/fallback extraction.</summary>
  public int DataOffset { get; init; }

  /// <summary>First inner-FAT cluster of the file (0 when not FAT-resolved).</summary>
  public int FirstCluster { get; init; }
}
