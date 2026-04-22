#pragma warning disable CS1591
namespace FileSystem.Hpfs;

/// <summary>
/// Directory entry in an OS/2 HPFS volume.
/// </summary>
public sealed class HpfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  /// <summary>LBA of the file's fnode (0 if synthetic/root).</summary>
  internal uint FnodeLba { get; init; }
  /// <summary>First file-data LBA when resolvable from fnode's direct allocation list.</summary>
  internal uint DataLba { get; init; }
  /// <summary>True when the fnode stored a B-tree pointer rather than a direct block list
  /// (large files) — we surface the entry but cannot yet extract its bytes.</summary>
  internal bool IsBtreeFile { get; init; }
}
