#pragma warning disable CS1591
namespace FileSystem.AdvFs;

/// <summary>
/// Logical entry surfaced by <see cref="AdvFsReader"/>. AdvFS images are not
/// walked into file granularity in this descriptor — see the class header for
/// scope. Entries surfaced here are the header/metadata files plus the raw
/// image, similar to the read-only HAMMER / VDFS descriptors.
/// </summary>
public sealed class AdvFsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
}
