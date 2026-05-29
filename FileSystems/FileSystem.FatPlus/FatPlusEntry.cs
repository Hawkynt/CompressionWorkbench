#pragma warning disable CS1591
namespace FileSystem.FatPlus;

/// <summary>
/// A directory entry from a FAT+ volume. Identical shape to
/// <see cref="FileSystem.Fat.FatEntry"/> but with an extended (up to 8-byte)
/// file size to represent files larger than 4 GiB.
/// </summary>
public sealed class FatPlusEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  internal int StartCluster { get; init; }
  public DateTime? LastModified { get; init; }
}
