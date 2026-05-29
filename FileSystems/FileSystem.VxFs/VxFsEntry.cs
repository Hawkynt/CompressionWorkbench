#pragma warning disable CS1591
namespace FileSystem.VxFs;

/// <summary>
/// Logical entry surfaced by <see cref="VxFsReader"/>. The descriptor does not
/// walk OLT (Object Location Table) / FSH (FileSet Header) / IAU (Inode
/// Allocation Unit) chains to extract user files — it surfaces the parsed
/// superblock as structured metadata plus the raw image, matching the
/// read-only pattern of HAMMER / AdvFS / VDFS descriptors.
/// </summary>
public sealed class VxFsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
}
