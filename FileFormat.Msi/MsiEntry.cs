#pragma warning disable CS1591
namespace FileFormat.Msi;

/// <summary>
/// Represents an entry (stream or storage) in an MSI/OLE Compound File.
/// </summary>
public sealed class MsiEntry {
  public string Name { get; init; } = "";
  public string FullPath { get; init; } = "";
  public bool IsDirectory { get; init; }
  public long Size { get; init; }
  internal int DirectoryIndex { get; init; }
}
