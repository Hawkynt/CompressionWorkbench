#pragma warning disable CS1591
namespace FileFormat.Ghost;

/// <summary>
/// One synthesised entry surfaced by <see cref="GhostReader"/> to the
/// registry: either metadata, a partition's raw decompressed bytes, or
/// (when extraction fails) the raw container payload.
/// </summary>
public sealed class GhostEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public long Offset { get; init; }
  public byte[] Data { get; init; } = [];
}
