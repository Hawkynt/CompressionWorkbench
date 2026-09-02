#pragma warning disable CS1591
namespace FileSystem.D64;

/// <summary>
/// Represents a d 64 entry.
/// </summary>
public sealed class D64Entry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
  public long Size { get; init; }
  /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
  public bool IsDirectory => false;
  /// <summary>
  /// Gets or sets the file type.
  /// </summary>
  public byte FileType { get; init; } // 0x82=PRG, 0x81=SEQ, 0x83=USR, 0x84=REL
  internal int StartTrack { get; init; }
  internal int StartSector { get; init; }
}
