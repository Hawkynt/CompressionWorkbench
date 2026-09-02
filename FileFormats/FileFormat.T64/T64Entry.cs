#pragma warning disable CS1591
namespace FileFormat.T64;

/// <summary>
/// Represents a t 64 entry.
/// </summary>
public sealed class T64Entry {
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
  /// Gets or sets the entry type.
  /// </summary>
public byte EntryType { get; init; } // 1=normal, 3=snapshot
  /// <summary>
  /// Gets or sets the start address.
  /// </summary>
public ushort StartAddress { get; init; }
  /// <summary>
  /// Gets or sets the end address.
  /// </summary>
public ushort EndAddress { get; init; }
  /// <summary>
  /// Gets or sets the data offset.
  /// </summary>
public int DataOffset { get; init; }
}
