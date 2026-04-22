#pragma warning disable CS1591
namespace FileSystem.Apfs;

public sealed class ApfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  internal ulong ObjectId { get; init; }
  /// <summary>First physical block of the file's data extent (0 = no extent).</summary>
  internal ulong FirstBlock { get; init; }
  /// <summary>Length in bytes of the file extent (0 = empty/none).</summary>
  internal long ExtentLength { get; init; }
}
