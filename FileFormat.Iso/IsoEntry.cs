#pragma warning disable CS1591
namespace FileFormat.Iso;

/// <summary>
/// Represents an entry (file or directory) in an ISO 9660 image.
/// </summary>
public sealed class IsoEntry {
  /// <summary>Path of the entry within the image.</summary>
  public string Name { get; init; } = "";
  /// <summary>Size in bytes (0 for directories).</summary>
  public long Size { get; init; }
  /// <summary>Whether the entry is a directory.</summary>
  public bool IsDirectory { get; init; }
  /// <summary>Last modification timestamp.</summary>
  public DateTime? LastModified { get; init; }
  internal long DataOffset { get; init; }
}
