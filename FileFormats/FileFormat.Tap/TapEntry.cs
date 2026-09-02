#pragma warning disable CS1591
namespace FileFormat.Tap;

/// <summary>
/// Represents a tap entry.
/// </summary>
public sealed class TapEntry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
  public int Size { get; init; }
  /// <summary>
  /// Gets or sets the data offset.
  /// </summary>
  public long DataOffset { get; init; }
  /// <summary>0=Program, 1=NumArray, 2=CharArray, 3=Code</summary>
  public byte FileType { get; init; }
}
