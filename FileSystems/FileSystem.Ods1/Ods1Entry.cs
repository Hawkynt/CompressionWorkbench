#pragma warning disable CS1591
namespace FileSystem.Ods1;

/// <summary>Directory entry from an ODS-1 (Files-11 Level 1) volume.</summary>
public sealed class Ods1Entry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
  public long Size { get; init; }
  /// <summary>
  /// Gets or sets the start lbn.
  /// </summary>
  public uint StartLbn { get; init; }
  /// <summary>
  /// Gets or sets the block count.
  /// </summary>
  public uint BlockCount { get; init; }
  /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
  public bool IsDirectory { get; init; }

  /// <summary>
  /// Every retrieval pointer the header carries, in order. A pointer's block count
  /// is 16-bit, so a long file is described by several of them.
  /// </summary>
  public IReadOnlyList<(uint Lbn, uint Blocks)>? Extents { get; init; }
}
