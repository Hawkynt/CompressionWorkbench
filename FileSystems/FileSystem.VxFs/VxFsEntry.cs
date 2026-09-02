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
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
  public long Size { get; init; }
  /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
  public bool IsDirectory { get; init; }
  /// <summary>
  /// Gets or sets the last modified.
  /// </summary>
  public DateTime? LastModified { get; init; }
}
