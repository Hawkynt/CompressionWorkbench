#pragma warning disable CS1591
namespace FileSystem.AmigaPfs;

/// <summary>Directory entry from an AmigaPFS volume.</summary>
public sealed class AmigaPfsEntry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
  public long Size { get; init; }
  /// <summary>
  /// Gets or sets the anode number.
  /// </summary>
  public uint AnodeNumber { get; init; }
  /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
  public bool IsDirectory { get; init; }
}
