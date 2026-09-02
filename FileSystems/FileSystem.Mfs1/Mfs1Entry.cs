#pragma warning disable CS1591
namespace FileSystem.Mfs1;

/// <summary>
/// A single catalog entry parsed out of an Acorn MFS-1 disk image. MFS-1
/// inherits Acorn's DFS catalog layout: 7-character filename plus 1-character
/// directory letter (default <c>$</c>) packed into 8 bytes in sector 0, with
/// the matching metadata block in sector 1.
/// </summary>
public sealed class Mfs1Entry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public required string Name { get; init; }
  /// <summary>
  /// Gets or sets the directory.
  /// </summary>
  public char Directory { get; init; } = '$';
  /// <summary>
  /// Gets the full name.
  /// </summary>
  public string FullName => this.Directory == '$' ? this.Name : $"{this.Directory}.{this.Name}";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
  public required uint Size { get; init; }
  /// <summary>
  /// Gets or sets the load address.
  /// </summary>
  public uint LoadAddress { get; init; }
  /// <summary>
  /// Gets or sets the exec address.
  /// </summary>
  public uint ExecAddress { get; init; }
  /// <summary>
  /// Gets or sets the start sector.
  /// </summary>
  public int StartSector { get; init; }
  /// <summary>
  /// Gets a value indicating whether is locked.
  /// </summary>
  public bool IsLocked { get; init; }
}
