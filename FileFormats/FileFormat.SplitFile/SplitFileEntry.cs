#pragma warning disable CS1591
namespace FileFormat.SplitFile;

/// <summary>
/// Represents the single logical file assembled from split parts.
/// </summary>
public sealed class SplitFileEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public int PartCount { get; init; }
}
