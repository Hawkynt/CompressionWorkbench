#pragma warning disable CS1591
namespace FileFormat.Acronis;

/// <summary>
/// Synthetic Stage-0 entry surfaced from an Acronis True Image (.tib / .tibx)
/// container. We never decode the proprietary chunk stream, so each entry is
/// either the synthetic <c>metadata.ini</c> or the raw passthrough image bytes
/// keyed by <see cref="Data"/>.
/// </summary>
public sealed class AcronisEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public long Offset { get; init; }
  public byte[] Data { get; init; } = [];
}
