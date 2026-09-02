#pragma warning disable CS1591
namespace FileFormat.Rarc;

/// <summary>
/// Specifies rarc entry attributes values.
/// </summary>
[Flags]
public enum RarcEntryAttributes : byte {
    /// <summary>
  /// Specifies that no option is selected.
  /// </summary>
None = 0x00,
    /// <summary>
  /// Specifies the file option.
  /// </summary>
File = 0x01,
    /// <summary>
  /// Specifies the directory option.
  /// </summary>
Directory = 0x02,
    /// <summary>
  /// Specifies the compressed option.
  /// </summary>
Compressed = 0x04,
    /// <summary>
  /// Specifies the preload to mram option.
  /// </summary>
PreloadToMram = 0x10,
    /// <summary>
  /// Specifies the preload to aram option.
  /// </summary>
PreloadToAram = 0x20,
    /// <summary>
  /// Specifies the load from dvd option.
  /// </summary>
LoadFromDvd = 0x40,
    /// <summary>
  /// Specifies the yaz 0 compressed option.
  /// </summary>
Yaz0Compressed = 0x80,
}

/// <summary>
/// Represents a rarc entry.
/// </summary>
public sealed class RarcEntry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public required string Name { get; init; }
    /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public required bool IsDirectory { get; init; }
    /// <summary>
  /// Gets or sets the id.
  /// </summary>
public required ushort Id { get; init; }
    /// <summary>
  /// Gets or sets the attributes.
  /// </summary>
public required RarcEntryAttributes Attributes { get; init; }
    /// <summary>
  /// Gets or sets the offset.
  /// </summary>
public required long Offset { get; init; }
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public required long Size { get; init; }
}
