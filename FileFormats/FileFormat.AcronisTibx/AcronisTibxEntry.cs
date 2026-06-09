#pragma warning disable CS1591
namespace FileFormat.AcronisTibx;

/// <summary>
/// One synthetic entry surfaced by <see cref="AcronisTibxReader"/>: either the parsed
/// <c>metadata.ini</c> describing the header fields recovered from the archive3 page-zero
/// structure, or the verbatim container bytes for downstream tooling.
/// </summary>
public sealed class AcronisTibxEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public long Offset { get; init; }
  public byte[] Data { get; init; } = [];
}
