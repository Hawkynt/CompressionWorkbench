#pragma warning disable CS1591
namespace FileSystem.Mfs1;

/// <summary>
/// A single catalog entry parsed out of an Acorn MFS-1 disk image. MFS-1
/// inherits Acorn's DFS catalog layout: 7-character filename plus 1-character
/// directory letter (default <c>$</c>) packed into 8 bytes in sector 0, with
/// the matching metadata block in sector 1.
/// </summary>
public sealed class Mfs1Entry {
  public required string Name { get; init; }
  public char Directory { get; init; } = '$';
  public string FullName => this.Directory == '$' ? this.Name : $"{this.Directory}.{this.Name}";
  public required uint Size { get; init; }
  public uint LoadAddress { get; init; }
  public uint ExecAddress { get; init; }
  public int StartSector { get; init; }
  public bool IsLocked { get; init; }
}
