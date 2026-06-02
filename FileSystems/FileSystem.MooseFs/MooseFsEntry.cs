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
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public long Offset { get; init; }
  public byte[] Data { get; init; } = [];
}
