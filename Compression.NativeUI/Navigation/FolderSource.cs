namespace Compression.NativeUI.Navigation;

/// <summary>A place the shell can show as a folder, with the name to show it under.</summary>
internal readonly record struct FolderNode(string Label, Location Location);

/// <summary>
/// Answers "what folders are under this place?" for the tree pane, the breadcrumb drop-downs and
/// address completion alike, so the three can never disagree about what exists.
/// </summary>
/// <param name="archiveSubfolders">The open archive's subfolders of a given folder.</param>
/// <param name="openArchive">The archive currently open, or null.</param>
/// <remarks>
/// Host folders list their subfolders, plus the open archive when it lives there — detecting every
/// file in a large folder to find the other archives would make expanding a node visibly slow, so
/// only the one the user is inside appears as a folder. Hidden folders are left out, as every file
/// manager does by default.
/// </remarks>
internal sealed class FolderSource(Func<string, IReadOnlyList<string>> archiveSubfolders, Func<string?> openArchive) {
  private const int CompletionLimit = 30;

  /// <summary>The tops of the tree: home first, then every drive or the filesystem root.</summary>
  public IReadOnlyList<FolderNode> Roots() {
    var roots = new List<FolderNode>();

    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
      roots.Add(new("Home", Location.Folder(home)));

    if (OperatingSystem.IsWindows()) {
      foreach (var drive in SafeDrives()) {
        var name = drive.Name.TrimEnd('\\');
        var volume = VolumeLabelOf(drive);
        roots.Add(new(string.IsNullOrWhiteSpace(volume) ? name : $"{volume} ({name})", Location.Folder(drive.Name)));
      }
    } else {
      roots.Add(new("/", Location.Folder("/")));
    }

    return roots;
  }

  /// <summary>The folders directly under <paramref name="parent"/>, sorted by name.</summary>
  public IReadOnlyList<FolderNode> Children(Location parent) {
    if (parent.IsInArchive)
      return [.. archiveSubfolders(parent.ArchiveFolder!)
        .Select(name => new FolderNode(name, Location.InArchive(parent.HostPath, parent.ArchiveFolder + name + "/")))];

    var nodes = new List<FolderNode>();
    try {
      foreach (var dir in new DirectoryInfo(parent.HostPath).EnumerateDirectories()
                 .Where(d => !IsHidden(d))
                 .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        nodes.Add(new(dir.Name, Location.Folder(dir.FullName)));
    } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) {
      // A folder the user cannot read simply has no children to show.
    }

    if (openArchive() is { } archive
        && string.Equals(Path.GetDirectoryName(archive), Path.TrimEndingDirectorySeparator(parent.HostPath), StringComparison.OrdinalIgnoreCase))
      nodes.Add(new(Path.GetFileName(archive), Location.InArchive(archive, "")));

    return nodes;
  }

  /// <summary>
  /// Completions for a partly typed host path: the subfolders of the folder typed so far whose names
  /// start with what follows the last separator.
  /// </summary>
  public IReadOnlyList<string> Complete(string typed) {
    if (string.IsNullOrWhiteSpace(typed)) return [];

    var endsWithSeparator = typed.EndsWith(Path.DirectorySeparatorChar) || typed.EndsWith(Path.AltDirectorySeparatorChar);
    var folder = endsWithSeparator ? typed : Path.GetDirectoryName(typed);
    var stem = endsWithSeparator ? "" : Path.GetFileName(typed);
    if (string.IsNullOrEmpty(folder)) return [];

    try {
      return [.. new DirectoryInfo(folder).EnumerateDirectories()
        .Where(d => !IsHidden(d) && d.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
        .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
        .Take(CompletionLimit)
        .Select(d => d.FullName)];
    } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or System.Security.SecurityException) {
      return [];
    }
  }

  /// <summary>A drive's volume label, or null where asking for it throws — some network drives do.</summary>
  private static string? VolumeLabelOf(DriveInfo drive) {
    try {
      return drive.VolumeLabel;
    } catch {
      return null;
    }
  }

  private static bool IsHidden(DirectoryInfo dir)
    => dir.Name.StartsWith('.') || (OperatingSystem.IsWindows() && (dir.Attributes & FileAttributes.Hidden) != 0);

  /// <summary>Drives that are ready. A removable drive with no medium throws on every question.</summary>
  private static IEnumerable<DriveInfo> SafeDrives() {
    DriveInfo[] drives;
    try {
      drives = DriveInfo.GetDrives();
    } catch {
      yield break;
    }

    foreach (var drive in drives) {
      bool ready;
      try {
        ready = drive.IsReady;
      } catch {
        ready = false;
      }

      if (ready) yield return drive;
    }
  }
}
