#pragma warning disable CS1591
namespace FileFormat.AcronisTibx;

/// <summary>
/// One synthetic entry surfaced by <see cref="AcronisTibxReader"/>: either the parsed
/// <c>metadata.ini</c> describing the header fields recovered from the archive3 page-zero
/// structure, or the verbatim container bytes for downstream tooling.
/// </summary>
public sealed class AcronisTibxEntry {
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
public bool IsDirectory { get; init; }
    /// <summary>
  /// Gets or sets the offset.
  /// </summary>
public long Offset { get; init; }
    /// <summary>
  /// Gets or sets the data.
  /// </summary>
public byte[] Data { get; init; } = [];
}
