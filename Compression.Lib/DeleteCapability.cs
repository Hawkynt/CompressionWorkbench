using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// What kind of delete operation applies to a current UI selection. Used by the explorer's
/// Delete context-menu item to decide between real-filesystem deletion, archive-modifier
/// deletion, and the disabled / read-only-warning state.
/// </summary>
public enum DeleteMode {
  /// <summary>Nothing actionable selected — the Delete menu item must be disabled.</summary>
  None,
  /// <summary>The view is browsing the real OS filesystem; the selected entry's
  /// <c>Path</c> is an absolute disk path and the deletion goes through <see cref="File"/>
  /// or <see cref="Directory"/>.</summary>
  RealFs,
  /// <summary>An archive/filesystem image is open and its descriptor implements
  /// <see cref="IArchiveModifiable"/>; deletion routes through
  /// <see cref="ArchiveOperations.Remove(string, string[], CompressionOptions?)"/> and
  /// runs the modifier path (O(touched bytes), no rebuild).</summary>
  ModifiableArchive,
  /// <summary>An archive is open but its descriptor does NOT implement
  /// <see cref="IArchiveModifiable"/>; the menu item should still light up so the user
  /// can click it and be told the format is read-only — but no destructive op runs.</summary>
  ReadOnlyArchive,
}

/// <summary>
/// Pure decision helper for the explorer's Delete command — no WPF / file-system / dialog
/// dependencies, so it is unit-testable in isolation. The view-model wires its
/// <c>CanExecute</c> + branching against this.
/// </summary>
public static class DeleteCapability {
  /// <summary>
  /// Classifies a delete request given the current UI state. The view-model's
  /// <c>CanExecute</c> is true exactly when this returns anything other than
  /// <see cref="DeleteMode.None"/>.
  /// </summary>
  /// <param name="isBrowsingOsFolder">True when the entry list is showing real-FS children
  /// (the user navigated up out of an archive into OS-browser mode).</param>
  /// <param name="archivePath">Path to the currently open archive, or <c>null</c>/empty
  /// when no archive is open.</param>
  /// <param name="selectedCount">Number of currently selected (non-parent) entries.</param>
  public static DeleteMode Evaluate(bool isBrowsingOsFolder, string? archivePath, int selectedCount) {
    if (selectedCount <= 0) return DeleteMode.None;
    if (isBrowsingOsFolder) return DeleteMode.RealFs;
    if (string.IsNullOrEmpty(archivePath)) return DeleteMode.None;

    FormatRegistration.EnsureInitialized();
    var format = FormatDetector.DetectByExtension(archivePath);
    if (format == FormatDetector.Format.Unknown) return DeleteMode.None;
    if (FormatDetector.IsStreamFormat(format)) return DeleteMode.ReadOnlyArchive;

    var ops = FormatRegistry.GetArchiveOps(format.ToString());
    return ops is IArchiveModifiable ? DeleteMode.ModifiableArchive : DeleteMode.ReadOnlyArchive;
  }
}
