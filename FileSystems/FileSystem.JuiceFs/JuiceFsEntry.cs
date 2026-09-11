#pragma warning disable CS1591
namespace FileSystem.JuiceFs;

/// <summary>
/// Represents one inspectable part of a JuiceFS metadata backup.
/// </summary>
public sealed class JuiceFsEntry {
  /// <summary>Gets the entry name.</summary>
  public string Name { get; init; } = "";
  /// <summary>Gets the logical entry size.</summary>
  public long Size { get; init; }
  /// <summary>Gets a value indicating whether the entry is a directory.</summary>
  public bool IsDirectory { get; init; }
  /// <summary>Gets the byte offset inside the original backup when <see cref="UsesSourceData"/> is true.</summary>
  public long Offset { get; init; }
  /// <summary>Gets generated entry data. Source-backed entries leave this empty.</summary>
  public byte[] Data { get; init; } = [];
  /// <summary>Whether extraction should slice the original backup rather than <see cref="Data"/>.</summary>
  internal bool UsesSourceData { get; init; }
}
