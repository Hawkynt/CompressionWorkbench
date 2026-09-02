#pragma warning disable CS1591
namespace FileSystem.MooseFs;

/// <summary>
/// One synthetic surface in the MooseFS master-metadata image: either the
/// <c>metadata.ini</c> human-readable summary, the raw <c>moosefs-master.bin</c>
/// pass-through, or a per-section raw payload (<c>section_NODE.bin</c>,
/// <c>section_EDGE.bin</c>, etc.). MooseFS file content lives on chunk
/// servers — not in this image — so no path-tree entries are surfaced.
/// </summary>
public sealed class MooseFsEntry {
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
