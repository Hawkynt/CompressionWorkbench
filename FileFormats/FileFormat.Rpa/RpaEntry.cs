#pragma warning disable CS1591
namespace FileFormat.Rpa;

/// <summary>A single entry parsed from an RPA index.</summary>
public sealed class RpaEntry {
  public string Path { get; init; } = "";
  public long Offset { get; init; }
  public long Length { get; init; }
  public byte[] Prefix { get; init; } = [];
}
