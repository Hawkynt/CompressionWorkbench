namespace Compression.NativeUI.Navigation;

/// <summary>
/// Somewhere the shell can be: a folder on the host, or a folder inside an archive that lives on
/// the host.
/// </summary>
/// <remarks>
/// Inside an archive, the folder is kept in the archive's own form — forward slashes, a trailing
/// slash, empty for the root — so two spellings of the same place compare equal and history does
/// not record a step that went nowhere.
/// </remarks>
internal sealed record Location {
  private Location(string hostPath, string? archiveFolder) {
    this.HostPath = hostPath;
    this.ArchiveFolder = archiveFolder;
  }

  /// <summary>The host folder, or the archive file when <see cref="IsInArchive"/>.</summary>
  public string HostPath { get; }

  /// <summary>The folder inside the archive, or null for a host folder. Empty is the archive root.</summary>
  public string? ArchiveFolder { get; }

  /// <summary>True when this points inside an archive rather than at a host folder.</summary>
  public bool IsInArchive => this.ArchiveFolder is not null;

  /// <summary>A folder on the host.</summary>
  public static Location Folder(string path) => new(TrimHostPath(path), null);

  /// <summary>A folder inside <paramref name="archivePath"/>; empty for its root.</summary>
  public static Location InArchive(string archivePath, string folder) => new(TrimHostPath(archivePath), NormalizeArchiveFolder(folder));

  /// <summary>An archive folder as the archive stores it: "a/b/", or "" for the root.</summary>
  public static string NormalizeArchiveFolder(string? folder) {
    var trimmed = (folder ?? "").Replace('\\', '/').Trim('/');
    return trimmed.Length == 0 ? "" : trimmed + "/";
  }

  /// <summary>Drops a trailing separator, but never from a root, where it is the path.</summary>
  private static string TrimHostPath(string path) => Path.TrimEndingDirectorySeparator(path);

  /// <inheritdoc />
  public override string ToString() => this.IsInArchive
    ? $"{this.HostPath}{Path.DirectorySeparatorChar}{this.ArchiveFolder}"
    : this.HostPath;
}
