#pragma warning disable CS1591
namespace FileFormat.Rpa;

/// <summary>A single entry parsed from an RPA index.</summary>
public sealed class RpaEntry {
  /// <summary>
  /// Gets or sets the path.
  /// </summary>
public string Path { get; init; } = "";
  /// <summary>
  /// Gets or sets the offset.
  /// </summary>
public long Offset { get; init; }
  /// <summary>
  /// Gets or sets the length.
  /// </summary>
public long Length { get; init; }
  /// <summary>
  /// Gets or sets the prefix.
  /// </summary>
public byte[] Prefix { get; init; } = [];
}
