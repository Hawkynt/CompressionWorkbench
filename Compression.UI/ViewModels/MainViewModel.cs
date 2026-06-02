using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Compression.Lib;
using Compression.Registry;

namespace Compression.UI.ViewModels;

internal sealed class MainViewModel : ViewModelBase {
  private string _archivePath = "";
  private string _format = "";
  private string _statusText = "Ready";
  private bool _isBusy;
  private double _progress;
  private string _currentFolder = "";
  // When non-null: the user has navigated OUT of an archive via the ".." entry
  // and is currently browsing an OS filesystem folder. The Entries list contains
  // OS files/folders rather than archive entries. Double-clicking a file in this
  // mode tries to open it as an archive (recursive descent, like 7z).
  private string? _osBrowserPath;

  // Captured when a user opens an archive from OS-browser mode. NavigateUp at
  // the top archive root prefers this over Path.GetDirectoryName(ArchivePath)
  // — without it, archives that live in %TEMP% (nested-descent leftovers,
  // download-manager hand-offs, drag-drop temps) drop the user into LocalAppData
  // instead of the folder they came from.
  private string? _priorOsBrowserPath;

  // Stack of ancestor archives the user descended through. Each entry is
  // (path, currentFolder, contentHash) of a parent archive. NavigateUp at
  // archive root pops the stack and restores the parent rather than exiting
  // to OS browser when there's a parent archive context.
  // ContentHash is the SHA-256 of the FIRST 64 KB of the archive bytes —
  // used to short-circuit nested-descent loops where a malformed file
  // detects as an archive containing itself.
  private readonly Stack<(string Path, string Folder, string ContentHash)> _archiveStack = new();
  private const int MaxNestedDescentDepth = 16;
  // Temp files created for nested-archive descent — cleaned on archive close
  // and on app exit.
  private readonly List<string> _nestedTempFiles = [];

  public ObservableCollection<ArchiveEntryViewModel> Entries { get; } = [];
  public ObservableCollection<ArchiveEntryViewModel> SelectedEntries { get; } = [];
  public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = [];

  public string ArchivePath { get => _archivePath; set => SetField(ref _archivePath, value); }
  public string Format { get => _format; set => SetField(ref _format, value); }
  public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }
  public bool IsBusy { get => _isBusy; set { SetField(ref _isBusy, value); OnPropertyChanged(nameof(IsNotBusy)); } }
  public bool IsNotBusy => !_isBusy;
  public double Progress { get => _progress; set => SetField(ref _progress, value); }
  public string CurrentFolder { get => _currentFolder; set { SetField(ref _currentFolder, value); RefreshBreadcrumbs(); } }

  public bool HasArchive => !string.IsNullOrEmpty(ArchivePath);
  public bool IsBrowsingOsFolder => _osBrowserPath is not null;
  public string Title => HasArchive
    ? $"CompressionWorkbench \u2014 {Path.GetFileName(ArchivePath)}"
    : "CompressionWorkbench";

  // Commands
  public ICommand OpenCommand { get; }
  public ICommand ExtractAllCommand { get; }
  public ICommand ExtractSelectedCommand { get; }
  public ICommand TestCommand { get; }
  public ICommand CreateCommand { get; }
  public ICommand NavigateUpCommand { get; }
  public ICommand NavigateIntoCommand { get; }
  public ICommand NavigateToBreadcrumbCommand { get; }
  public ICommand ViewAsTextCommand { get; }
  public ICommand ViewAsHexCommand { get; }
  public ICommand ViewAsImageCommand { get; }
  public ICommand AddFilesCommand { get; }
  public ICommand PropertiesCommand { get; }
  public ICommand AnalyzeCommand { get; }
  public ICommand AnalyzeFileCommand { get; }
  public ICommand BenchmarkCommand { get; }
  public ICommand FileAssociationsCommand { get; }
  public ICommand DefragmentEntryCommand { get; }
  public ICommand DeleteSelectedCommand { get; }

  // True after a successful in-archive delete on a format whose container leaves
  // freed slots behind. The Defragment menu / status hint surfaces this so the
  // user knows a compact pass will reclaim cluster-tip slack and tidy the
  // directory. Real-FS deletion does NOT set this — the host filesystem handles
  // its own free-space bookkeeping.
  private bool _hasPendingFragmentation;
  public bool HasPendingFragmentation {
    get => _hasPendingFragmentation;
    set => SetField(ref _hasPendingFragmentation, value);
  }

  // Tooltip surfaced over the context-menu Delete item. Switches between the
  // descriptive enabled message and the "read-only" hint depending on whether
  // the current container can actually accept a delete. Bound from the menu item.
  public string DeleteCommandTooltip {
    get {
      var mode = Compression.Lib.DeleteCapability.Evaluate(
        IsBrowsingOsFolder, ArchivePath, SelectedEntries.Count(e => !e.IsParentEntry));
      return mode switch {
        Compression.Lib.DeleteMode.RealFs => "Delete selected file(s) from disk",
        Compression.Lib.DeleteMode.ModifiableArchive => "Delete selected entry from archive",
        Compression.Lib.DeleteMode.ReadOnlyArchive => "This format is read-only.",
        _ => "Select one or more entries to delete",
      };
    }
  }

  public MainViewModel() {
    OpenCommand = new RelayCommand(_ => OpenDialog());
    ExtractAllCommand = new AsyncRelayCommand(_ => ExtractAll(), _ => HasArchive);
    ExtractSelectedCommand = new AsyncRelayCommand(_ => ExtractSelected(), _ => HasArchive && SelectedEntries.Count > 0);
    TestCommand = new AsyncRelayCommand(_ => TestArchive(), _ => HasArchive);
    CreateCommand = new RelayCommand(_ => CreateDialog());
    // NavigateUp is always available — at archive root it transitions to OS-browser mode.
    NavigateUpCommand = new RelayCommand(_ => NavigateUp(), _ => HasArchive || _osBrowserPath is not null);
    NavigateIntoCommand = new RelayCommand(p => NavigateInto(p as ArchiveEntryViewModel));
    NavigateToBreadcrumbCommand = new RelayCommand(p => NavigateToBreadcrumb(p as string));
    ViewAsTextCommand = new RelayCommand(_ => ViewSelectedAs(hex: false), _ => HasArchive && HasSelectedFile);
    ViewAsHexCommand = new RelayCommand(_ => ViewSelectedAs(hex: true), _ => HasArchive && HasSelectedFile);
    ViewAsImageCommand = new RelayCommand(_ => ViewSelectedAsImage(), _ => (HasArchive || IsBrowsingOsFolder) && HasSelectedFile);
    AddFilesCommand = new RelayCommand(_ => AddFilesToArchive(), _ => HasArchive && CanAddFiles);
    // When nothing is selected, Properties shows archive-level stats; when a
    // single non-parent entry is selected, it shows that entry's stats.
    PropertiesCommand = new RelayCommand(_ => ShowProperties(),
      _ => HasArchive && (SelectedEntries.Count == 0
                          || (SelectedEntries.Count == 1 && !SelectedEntries[0].IsParentEntry)));
    AnalyzeCommand = new RelayCommand(_ => ShowAnalysis(), _ => HasArchive && HasSelectedFile);
    AnalyzeFileCommand = new RelayCommand(_ => ShowAnalyzeFile());
    BenchmarkCommand = new RelayCommand(_ => ShowBenchmark());
    FileAssociationsCommand = new RelayCommand(_ => ShowFileAssociations());
    DefragmentEntryCommand = new RelayCommand(_ => DefragmentSelected(), _ => CanDefragmentSelected);
    DeleteSelectedCommand = new RelayCommand(_ => DeleteSelectedEntries(), _ => CanDeleteSelected);
  }

  /// <summary>
  /// True when the Delete menu / Del keypress should be live. Always true while there is
  /// at least one non-parent entry selected — even for read-only archives, where the
  /// click surfaces a "this format is read-only" info dialog instead of doing nothing.
  /// Only the RealFs and ModifiableArchive paths actually mutate state.
  /// </summary>
  internal bool CanDeleteSelected
    => Compression.Lib.DeleteCapability.Evaluate(
         IsBrowsingOsFolder, ArchivePath, SelectedEntries.Count(e => !e.IsParentEntry))
       != Compression.Lib.DeleteMode.None;

  /// <summary>
  /// Branches between OS-filesystem delete (real <c>File.Delete</c> / recursive
  /// <c>Directory.Delete</c>), modifiable-archive delete (routes through
  /// <see cref="ArchiveOperations.Remove(string, string[], CompressionOptions?)"/> which
  /// prefers <see cref="IArchiveModifiable"/>), and the read-only fallback (info
  /// MessageBox, no destructive op). After a successful archive delete
  /// <see cref="HasPendingFragmentation"/> is raised so the user can run Defragment
  /// to compact freed slots.
  /// </summary>
  private void DeleteSelectedEntries() {
    var selected = SelectedEntries.Where(e => !e.IsParentEntry).ToList();
    if (selected.Count == 0) return;

    var mode = Compression.Lib.DeleteCapability.Evaluate(
      IsBrowsingOsFolder, ArchivePath, selected.Count);

    if (mode == Compression.Lib.DeleteMode.ReadOnlyArchive) {
      MessageBox.Show(
        "This format is read-only — entries can't be removed without rebuilding the container.\n\nUse File → Create to write a new archive that excludes them.",
        "Cannot delete",
        MessageBoxButton.OK, MessageBoxImage.Information);
      return;
    }

    if (mode == Compression.Lib.DeleteMode.None) return;

    // Recursive count for directories so the confirmation prompt is honest.
    long fileCount = 0;
    long dirCount = 0;
    if (mode == Compression.Lib.DeleteMode.RealFs) {
      foreach (var entry in selected) {
        var p = entry.Path;
        if (Directory.Exists(p)) {
          dirCount++;
          try {
            fileCount += Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).LongCount();
          } catch {
            // Best-effort count — denied subdirs still get the parent listed.
          }
        } else if (File.Exists(p)) {
          fileCount++;
        }
      }
    } else {
      foreach (var entry in selected) {
        if (entry.IsDirectory) {
          var dirPrefix = entry.Path.EndsWith('/') ? entry.Path : entry.Path + "/";
          dirCount++;
          foreach (var e in _allEntries)
            if (!e.IsDirectory && e.Path.StartsWith(dirPrefix, StringComparison.Ordinal))
              fileCount++;
        } else {
          fileCount++;
        }
      }
    }

    var summary = dirCount switch {
      0 => $"Delete {fileCount} file(s)?",
      _ => $"Delete {fileCount} file(s) and {dirCount} folder(s)?",
    };
    var confirm = MessageBox.Show(summary, "Confirm Delete",
                                  MessageBoxButton.YesNo, MessageBoxImage.Warning);
    if (confirm != MessageBoxResult.Yes) return;

    if (mode == Compression.Lib.DeleteMode.RealFs) {
      var failed = 0;
      foreach (var entry in selected) {
        var path = entry.Path;
        try {
          if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
          else if (File.Exists(path)) File.Delete(path);
        } catch (Exception ex) {
          failed++;
          StatusText = $"Failed to delete {entry.Name}: {ex.Message}";
        }
      }
      RefreshVisibleEntries();
      StatusText = failed == 0
        ? $"Deleted {selected.Count} item(s)."
        : $"Deleted {selected.Count - failed} of {selected.Count} item(s) ({failed} failed).";
      return;
    }

    // ModifiableArchive — collect entry names (recurse into directories so the
    // modifier sees every file). The trailing slash on directories matches how
    // ArchiveEntry.Name is stored in _allEntries.
    var names = new List<string>();
    foreach (var entry in selected) {
      if (entry.IsDirectory) {
        var dirPrefix = entry.Path.EndsWith('/') ? entry.Path : entry.Path + "/";
        foreach (var e in _allEntries)
          if (e.Path.StartsWith(dirPrefix, StringComparison.Ordinal) && e.Path != dirPrefix)
            names.Add(e.Path);
        names.Add(dirPrefix);
      } else {
        names.Add(entry.Path);
      }
    }
    if (names.Count == 0) return;

    try {
      IsBusy = true;
      StatusText = $"Deleting {names.Count} entry(ies) from archive...";
      ArchiveOperations.Remove(ArchivePath, [.. names]);
      HasPendingFragmentation = true;
      StatusText = $"Deleted {names.Count} entry(ies). Free space available — defragment to compact.";
      // Reload so the EntryList reflects the new on-disk state. The modifier
      // mutated the file in place; List() re-reads the directory.
      Open(ArchivePath);
      // Open() resets HasPendingFragmentation; re-raise it so the banner survives
      // the reload triggered by our own delete.
      HasPendingFragmentation = true;
    } catch (Exception ex) {
      StatusText = $"Delete failed: {ex.Message}";
      MessageBox.Show($"Delete failed:\n\n{ex.GetType().Name}: {ex.Message}",
                      "Delete", MessageBoxButton.OK, MessageBoxImage.Error);
    } finally {
      IsBusy = false;
    }
  }

  /// <summary>
  /// True when the selected entry is a single, non-directory file whose
  /// extension routes to a descriptor that implements
  /// <see cref="IArchiveDefragmentable"/>. The right-click "Defragment..."
  /// menu item is enabled exactly when this returns true.
  /// </summary>
  private bool CanDefragmentSelected {
    get {
      if (SelectedEntries.Count != 1) return false;
      var entry = SelectedEntries[0];
      if (entry.IsDirectory || entry.IsParentEntry) return false;
      // OS-browser entries hold the absolute path in `Path`; archive entries
      // hold the in-archive name. We can only defragment OS-browser files
      // because the descriptor needs a real on-disk image, not a slice of a
      // host archive.
      if (!IsBrowsingOsFolder) return false;
      var p = entry.Path;
      if (string.IsNullOrEmpty(p) || !File.Exists(p)) return false;
      Compression.Lib.FormatRegistration.EnsureInitialized();
      var format = FormatDetector.DetectByExtension(p);
      var ops = FormatRegistry.GetArchiveOps(format.ToString());
      return ops is IArchiveDefragmentable;
    }
  }

  private void DefragmentSelected() {
    if (!CanDefragmentSelected) return;
    var path = SelectedEntries[0].Path;
    var dlg = new Views.DefragmentWindow(path) { Owner = Application.Current.MainWindow };
    // Re-list when the dialog mutates the currently-open archive.
    dlg.ArchiveMutated += mutated => {
      if (HasArchive && string.Equals(mutated, ArchivePath, StringComparison.OrdinalIgnoreCase))
        Open(ArchivePath);
    };
    dlg.Show();
  }

  private bool HasSelectedFile => SelectedEntries.Any(e => !e.IsDirectory && !e.IsParentEntry);
  private bool CanAddFiles => !string.IsNullOrEmpty(ArchivePath) && FormatDetector.DetectByExtension(ArchivePath) is var f
    && f != FormatDetector.Format.Unknown && !FormatDetector.IsStreamFormat(f);

  public void Open(string path) => Open(path, fromNestedDescent: false);

  internal void Open(string path, bool fromNestedDescent) {
    try {
      IsBusy = true;
      StatusText = $"Opening {Path.GetFileName(path)}...";

      var format = FormatDetector.Detect(path);
      var entries = ArchiveOperations.List(path, password: null);

      // Top-level Open clears the parent-archive stack and any leftover nested
      // temp files. Nested descents skip this cleanup so the back-stack survives.
      if (!fromNestedDescent) {
        _archiveStack.Clear();
        CleanupNestedTempFiles();
        // Fresh archive — assume no pending fragmentation until the user deletes
        // something. The previous archive's flag would otherwise leak across
        // reopens. The delete handler re-raises this flag after its own Open()
        // refresh, so an in-archive delete still surfaces the hint.
        HasPendingFragmentation = false;
        // Remember where the user was browsing so NavigateUp can return them
        // there. Only captured on top-level Open: nested descents preserve the
        // first capture so a multi-level descent still exits to the original
        // OS folder, not the temp dir of an intermediate frame.
        _priorOsBrowserPath = _osBrowserPath;
      }

      ArchivePath = path;
      Format = format.ToString();
      CurrentFolder = "";
      // Re-entering archive mode: clear the OS-browser breadcrumb.
      _osBrowserPath = null;
      OnPropertyChanged(nameof(IsBrowsingOsFolder));

      _allEntries.Clear();
      foreach (var e in entries) {
        _allEntries.Add(new ArchiveEntryViewModel {
          Index = e.Index,
          Name = Path.GetFileName(e.Name.TrimEnd('/')),
          Path = e.Name,
          OriginalSize = e.OriginalSize,
          CompressedSize = e.CompressedSize,
          Method = e.Method,
          IsDirectory = e.IsDirectory,
          IsEncrypted = e.IsEncrypted,
          LastModified = e.LastModified,
        });
      }

      RefreshVisibleEntries();

      var totalOrig = entries.Sum(e => e.OriginalSize);
      var totalComp = entries.Where(e => e.CompressedSize >= 0).Sum(e => e.CompressedSize);
      var ratio = totalOrig > 0 ? $" ({100.0 * totalComp / totalOrig:F1}%)" : "";
      StatusText = $"{entries.Count} entries, {FormatSize(totalOrig)}{ratio} \u2014 {format}";

      OnPropertyChanged(nameof(HasArchive));
      OnPropertyChanged(nameof(Title));

      // Persist the parent folder so the next launch can restore browsing
      // there (parent-walk fallback handles deletion between sessions).
      // Skipped for nested-descent temp files since those live under %TEMP%
      // and aren't a meaningful "last folder" to remember.
      if (!fromNestedDescent) {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
          new UserSettings { LastFolder = parent }.Save();
      }
    }
    catch (Exception ex) {
      StatusText = $"Error: {ex.Message}";
    }
    finally {
      IsBusy = false;
    }
  }

  /// <summary>
  /// Restores OS-browser mode at the last-used folder (or the deepest
  /// surviving ancestor if it has been deleted). Called from
  /// <c>App.OnStartup</c> when no archive argument is supplied.
  /// </summary>
  public void StartInOsBrowserAtLastFolder() {
    var settings = UserSettings.Load();
    var folder = UserSettings.ResolveExistingAncestor(settings.LastFolder);
    _osBrowserPath = folder;
    CurrentFolder = "";
    RefreshVisibleEntries();
    OnPropertyChanged(nameof(IsBrowsingOsFolder));
    StatusText = $"Browsing: {folder}";
  }

  private readonly List<ArchiveEntryViewModel> _allEntries = [];

  private void RefreshVisibleEntries() {
    Entries.Clear();

    // OS-browser mode: list filesystem children instead of archive entries.
    // Reached when the user navigates UP from an archive's root via the ".." entry.
    if (_osBrowserPath is not null) {
      RefreshOsBrowserEntries();
      return;
    }

    var prefix = string.IsNullOrEmpty(CurrentFolder) ? "" : CurrentFolder;

    // Always emit ".." — even at archive root. At root, ".." exits the archive
    // and switches to OS-browser mode rooted at the archive's containing folder
    // (matches 7-Zip behaviour).
    Entries.Add(new ArchiveEntryViewModel {
      Name = "..",
      Path = "",
      IsDirectory = true,
      IsParentEntry = true,
    });

    // Collect immediate children, deduplicating folders
    // Key: normalized folder name with trailing slash, or file name
    var seen = new HashSet<string>(StringComparer.Ordinal);

    foreach (var e in _allEntries) {
      if (!e.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;
      var remainder = e.Path[prefix.Length..];
      if (string.IsNullOrEmpty(remainder)) continue;

      var slashIdx = remainder.IndexOf('/');

      if (slashIdx < 0 && !e.IsDirectory) {
        // Direct file child
        if (seen.Add(remainder))
          Entries.Add(e);
      }
      else if (slashIdx < 0 && e.IsDirectory) {
        // Directory entry without trailing slash — treat as folder
        var key = remainder + "/";
        if (seen.Add(key))
          Entries.Add(e);
      }
      else if (slashIdx == remainder.Length - 1) {
        // Direct directory child (path ends with /)
        var key = remainder; // already has trailing /
        if (seen.Add(key))
          Entries.Add(e);
      }
      else {
        // Deeper entry — show the immediate subfolder as a virtual directory
        var subDir = remainder[..(slashIdx + 1)]; // e.g. "folder/"
        if (seen.Add(subDir)) {
          // Aggregate sizes for this virtual directory
          var dirPrefix = prefix + subDir;
          long origSum = 0, compSum = 0;
          var hasComp = false;
          foreach (var x in _allEntries) {
            if (!x.Path.StartsWith(dirPrefix, StringComparison.Ordinal)) continue;
            origSum += x.OriginalSize;
            if (x.CompressedSize >= 0) { compSum += x.CompressedSize; hasComp = true; }
          }
          Entries.Add(new ArchiveEntryViewModel {
            Name = subDir.TrimEnd('/'),
            Path = dirPrefix,
            OriginalSize = origSum,
            CompressedSize = hasComp ? compSum : -1,
            IsDirectory = true,
          });
        }
      }
    }
  }

  private void NavigateInto(ArchiveEntryViewModel? entry) {
    if (entry == null) return;
    if (entry.IsParentEntry) { NavigateUp(); return; }

    // OS-browser mode: directories CD; files probe-open as archive (fall
    // back to byte preview when the format isn't a recognized archive).
    if (_osBrowserPath is not null) {
      if (entry.IsDirectory) {
        _osBrowserPath = entry.Path;
        CurrentFolder = "";
        RefreshVisibleEntries();
        return;
      }

      if (string.IsNullOrEmpty(entry.Path) || !File.Exists(entry.Path)) {
        StatusText = $"Cannot open: {entry.Name} not readable.";
        return;
      }

      var format = FormatDetector.Format.Unknown;
      try { format = FormatDetector.Detect(entry.Path); } catch { /* fall through */ }
      if (format != FormatDetector.Format.Unknown && !FormatDetector.IsStreamFormat(format)) {
        Open(entry.Path);
        return;
      }

      // Not an archive — show as bytes.
      try {
        var data = File.ReadAllBytes(entry.Path);
        if (data.Length == 0) {
          StatusText = $"{entry.Name} is empty.";
          return;
        }
        var preview = new Views.PreviewWindow { Owner = Application.Current.MainWindow };
        preview.ShowData(entry.Name, data, hex: false);
        preview.Show();
      }
      catch (Exception ex) {
        StatusText = $"Cannot preview: {ex.Message}";
      }
      return;
    }

    if (!entry.IsDirectory) return;
    CurrentFolder = entry.Path.EndsWith('/') ? entry.Path : entry.Path + "/";
    RefreshVisibleEntries();
  }

  private void NavigateUp() {
    // OS-browser mode: ".." goes to OS parent; at filesystem root we stay put.
    if (_osBrowserPath is not null) {
      var parent = Directory.GetParent(_osBrowserPath)?.FullName;
      if (!string.IsNullOrEmpty(parent)) {
        _osBrowserPath = parent;
        RefreshVisibleEntries();
      }
      return;
    }

    if (!string.IsNullOrEmpty(CurrentFolder)) {
      var trimmed = CurrentFolder.TrimEnd('/');
      var lastSlash = trimmed.LastIndexOf('/');
      CurrentFolder = lastSlash >= 0 ? trimmed[..(lastSlash + 1)] : "";
      RefreshVisibleEntries();
      return;
    }

    // At archive root — first try to pop a parent archive off the stack
    // (we're in a nested archive descended via TryEnterAsNestedArchive).
    if (_archiveStack.Count > 0) {
      var (parentPath, parentFolder, _) = _archiveStack.Pop();
      // Re-open the parent — fromNestedDescent: true so the stack survives.
      Open(parentPath, fromNestedDescent: true);
      CurrentFolder = parentFolder;
      RefreshVisibleEntries();
      return;
    }

    // No parent archive — exit to OS-browser. Prefer the folder the user was
    // browsing when they opened the archive (captured on top-level Open). Only
    // fall back to the archive's containing dir when no prior path is known —
    // archives opened from %TEMP% (nested-descent residue, drag-drop temps,
    // download hand-offs) would otherwise dump the user into LocalAppData.
    var dir = !string.IsNullOrEmpty(_priorOsBrowserPath) && Directory.Exists(_priorOsBrowserPath)
      ? _priorOsBrowserPath
      : null;
    if (dir is null && !string.IsNullOrEmpty(ArchivePath)) {
      var archiveDir = Path.GetDirectoryName(ArchivePath);
      if (!string.IsNullOrEmpty(archiveDir) && Directory.Exists(archiveDir) && !IsTempPath(archiveDir))
        dir = archiveDir;
    }
    dir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    _osBrowserPath = dir;
    _priorOsBrowserPath = null;
    RefreshVisibleEntries();
    OnPropertyChanged(nameof(IsBrowsingOsFolder));
  }

  /// <summary>
  /// True when <paramref name="path"/> is the per-user TEMP directory or a
  /// child of it. Used to suppress NavigateUp landings inside %LOCALAPPDATA%
  /// when the open archive happens to live in temp (nested-descent extraction,
  /// drag-drop staging, browser download buffers).
  /// </summary>
  private static bool IsTempPath(string path) {
    var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return p.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Populates Entries with the OS folder's immediate children (subdirs + files).
  /// File entries are clickable — double-clicking attempts to open them as an
  /// archive via <see cref="Open"/>.
  /// </summary>
  private void RefreshOsBrowserEntries() {
    if (_osBrowserPath is null) return;

    // Always show ".." — let the user keep walking up the FS tree.
    Entries.Add(new ArchiveEntryViewModel {
      Name = "..",
      Path = "",
      IsDirectory = true,
      IsParentEntry = true,
    });

    try {
      var dirInfo = new DirectoryInfo(_osBrowserPath);
      foreach (var sub in dirInfo.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)) {
        Entries.Add(new ArchiveEntryViewModel {
          Name = sub.Name,
          Path = sub.FullName,
          IsDirectory = true,
          LastModified = sub.LastWriteTime,
        });
      }
      foreach (var file in dirInfo.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)) {
        Entries.Add(new ArchiveEntryViewModel {
          Name = file.Name,
          Path = file.FullName,
          IsDirectory = false,
          OriginalSize = file.Length,
          CompressedSize = -1,
          LastModified = file.LastWriteTime,
          Method = "",
        });
      }
    }
    catch (UnauthorizedAccessException) {
      StatusText = $"Access denied: {_osBrowserPath}";
    }
    catch (DirectoryNotFoundException) {
      StatusText = $"Folder vanished: {_osBrowserPath}";
    }

    StatusText = $"Browsing: {_osBrowserPath}";
    RefreshBreadcrumbs();
  }

  private void NavigateToBreadcrumb(string? path) {
    // OS-browser mode: the breadcrumb path is an absolute FS path → CD there.
    if (_osBrowserPath is not null) {
      if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) {
        _osBrowserPath = path;
        RefreshVisibleEntries();
      }
      return;
    }
    CurrentFolder = path ?? "";
    RefreshVisibleEntries();
  }

  private void RefreshBreadcrumbs() {
    Breadcrumbs.Clear();

    // OS-browser mode: surface the FS path as breadcrumb segments. Each segment
    // is clickable → navigates back up to that level. (Click handler is the same
    // NavigateToBreadcrumbCommand; in OS mode we route to OS-folder navigation.)
    if (_osBrowserPath is not null) {
      var parts = _osBrowserPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
      var accumulated = "";
      for (var i = 0; i < parts.Length; i++) {
        accumulated = i == 0 ? parts[i] + Path.DirectorySeparatorChar : Path.Combine(accumulated, parts[i]);
        Breadcrumbs.Add(new BreadcrumbSegment { Label = parts[i], FolderPath = accumulated });
      }
      return;
    }

    // Root segment
    Breadcrumbs.Add(new BreadcrumbSegment { Label = Path.GetFileName(ArchivePath), FolderPath = "" });

    if (!string.IsNullOrEmpty(CurrentFolder)) {
      var parts = CurrentFolder.TrimEnd('/').Split('/');
      var accumulated = "";
      foreach (var part in parts) {
        accumulated += part + "/";
        Breadcrumbs.Add(new BreadcrumbSegment { Label = part, FolderPath = accumulated });
      }
    }
  }

  private void OpenDialog() {
    var dlg = new Microsoft.Win32.OpenFileDialog {
      Title = "Open Archive",
      Filter = BuildOpenFilter(),
    };
    if (dlg.ShowDialog() == true)
      Open(dlg.FileName);
  }

  private async Task ExtractAll() {
    var dlg = new System.Windows.Forms.FolderBrowserDialog {
      Description = "Select output folder",
      UseDescriptionForTitle = true,
    };
    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

    await RunAsync($"Extracting to {dlg.SelectedPath}...", () => {
      ArchiveOperations.Extract(ArchivePath, dlg.SelectedPath, password: null, files: null);
    });
    StatusText = $"Extracted to {dlg.SelectedPath}";
  }

  /// <summary>
  /// Extracts the given selection to a per-session staging folder and returns the
  /// top-level paths for <see cref="DataFormats.FileDrop"/>. Called by the entry-list
  /// drag-out handler when the user drags archive entries to Explorer.
  /// <para>
  /// Files stay in <see cref="_dragOutStagingRoot"/> (under %TEMP%) until the archive
  /// is closed or the process exits — copies Windows may make for the drop target are
  /// independent. Nested directory selections preserve their in-archive relative paths
  /// so dropped folders look right in the target.
  /// </para>
  /// </summary>
  internal string[] MaterializeForDragOut(IReadOnlyList<ArchiveEntryViewModel> selection) {
    if (selection.Count == 0 || !HasArchive) return [];

    // Expand directory selections to all contained files, same as ExtractSelected does.
    var filePaths = new List<string>();
    foreach (var entry in selection) {
      if (entry.IsParentEntry) continue;
      if (entry.IsDirectory) {
        var dirPrefix = entry.Path.EndsWith('/') ? entry.Path : entry.Path + "/";
        foreach (var e in _allEntries)
          if (!e.IsDirectory && e.Path.StartsWith(dirPrefix, StringComparison.Ordinal))
            filePaths.Add(e.Path);
      } else {
        filePaths.Add(entry.Path);
      }
    }
    var files = filePaths.Distinct().ToArray();
    if (files.Length == 0) return [];

    // Ensure a clean staging dir per drag gesture; previous drag's contents are left in
    // place (the user may still be completing that drop), but the session-level directory
    // is reused so repeated drags don't leak unbounded temp folders.
    var stagingDir = Path.Combine(this.DragOutStagingRoot, Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(stagingDir);
    ArchiveOperations.Extract(ArchivePath, stagingDir, password: null, files: files);

    // For each dragged top-level entry (not the expanded directory contents), surface
    // its path under the staging dir. Explorer copies directories recursively.
    var topLevel = new List<string>();
    foreach (var entry in selection.Where(e => !e.IsParentEntry)) {
      var candidate = Path.Combine(stagingDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
      if (entry.IsDirectory) {
        if (Directory.Exists(candidate)) topLevel.Add(candidate);
      } else if (File.Exists(candidate)) {
        topLevel.Add(candidate);
      }
    }
    return topLevel.ToArray();
  }

  private string? _dragOutStagingRoot;
  private string DragOutStagingRoot {
    get {
      if (this._dragOutStagingRoot == null || !Directory.Exists(this._dragOutStagingRoot)) {
        this._dragOutStagingRoot = Path.Combine(Path.GetTempPath(),
          "cwb-drag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(this._dragOutStagingRoot);
      }
      return this._dragOutStagingRoot;
    }
  }

  private async Task ExtractSelected() {
    var dlg = new System.Windows.Forms.FolderBrowserDialog {
      Description = "Select output folder",
      UseDescriptionForTitle = true,
    };
    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

    // Collect all file paths: for selected directories, include all children
    var filePaths = new List<string>();
    foreach (var entry in SelectedEntries) {
      if (entry.IsParentEntry) continue;
      if (entry.IsDirectory) {
        // Include all files under this directory
        var dirPrefix = entry.Path.EndsWith('/') ? entry.Path : entry.Path + "/";
        foreach (var e in _allEntries) {
          if (!e.IsDirectory && e.Path.StartsWith(dirPrefix, StringComparison.Ordinal))
            filePaths.Add(e.Path);
        }
      }
      else {
        filePaths.Add(entry.Path);
      }
    }

    var files = filePaths.Distinct().ToArray();
    await RunAsync($"Extracting {files.Length} file(s)...", () => {
      ArchiveOperations.Extract(ArchivePath, dlg.SelectedPath, password: null, files: files);
    });
    StatusText = $"Extracted {files.Length} file(s) to {dlg.SelectedPath}";
  }

  private async Task TestArchive() {
    var ok = false;
    string? errorDetail = null;
    var sw = Stopwatch.StartNew();

    try {
      await RunAsync("Testing archive integrity...", () => {
        ok = ArchiveOperations.Test(ArchivePath, password: null);
      });
    } catch (Exception ex) {
      ok = false;
      errorDetail = ex.Message;
    }

    sw.Stop();
    var elapsed = sw.ElapsedMilliseconds;

    if (ok) {
      StatusText = $"Integrity test: OK ({elapsed}ms)";
      MessageBox.Show(
        $"Integrity test passed.\n\nArchive: {System.IO.Path.GetFileName(ArchivePath)}\nEntries: {_allEntries.Count}\nTime: {elapsed}ms",
        "Integrity Test",
        System.Windows.MessageBoxButton.OK,
        System.Windows.MessageBoxImage.Information);
    } else {
      StatusText = $"Integrity test: FAILED ({elapsed}ms)";
      var msg = $"Integrity test FAILED.\n\nArchive: {System.IO.Path.GetFileName(ArchivePath)}";
      if (errorDetail != null)
        msg += $"\n\nError: {errorDetail}";
      MessageBox.Show(msg, "Integrity Test",
        System.Windows.MessageBoxButton.OK,
        System.Windows.MessageBoxImage.Error);
    }
  }

  internal void ViewSelectedAs(bool hex) => ViewSelectedAs(hex, allowDescend: true);

  /// <summary>
  /// Preview the selected entry, ALWAYS as an image — bypasses both nested-archive
  /// descent and the text/hex routing. Falls back to byte preview if the bytes
  /// don't sniff as a known image. Wired to the right-click "Preview as Image"
  /// menu item so users can force the image renderer for entries that would
  /// otherwise enter a colorspace tree (default behaviour for .png inside
  /// .zip etc.).
  /// </summary>
  internal void ViewSelectedAsImage() => ViewSelectedAs(hex: false, allowDescend: false);

  private void ViewSelectedAs(bool hex, bool allowDescend) {
    var entry = SelectedEntries.FirstOrDefault(e => !e.IsDirectory && !e.IsParentEntry);
    if (entry == null) return;

    try {
      StatusText = $"Loading {entry.Name}...";
      byte[] data;

      // OS-browser mode: read from disk directly. Otherwise extract from the
      // currently-open archive. Without this branch, ExtractEntry tries to
      // resolve an absolute FS path inside the (stale) archive and throws
      // "The value cannot be an empty string" or similar.
      if (_osBrowserPath is not null) {
        if (string.IsNullOrEmpty(entry.Path) || !File.Exists(entry.Path)) {
          StatusText = $"Cannot preview: {entry.Name} not readable.";
          return;
        }
        data = File.ReadAllBytes(entry.Path);
      } else {
        if (string.IsNullOrEmpty(ArchivePath) || string.IsNullOrEmpty(entry.Path)) {
          StatusText = $"Cannot preview: missing archive context.";
          return;
        }
        data = ArchiveOperations.ExtractEntry(ArchivePath, entry.Path, password: null);
      }

      // Before showing the preview, check if the extracted bytes are themselves
      // an archive — if so, descend into it (push current context onto the
      // archive stack and re-open). Matches 7-Zip's behaviour for nested
      // archives like a ZIP-inside-ZIP or a VHD-inside-tarball.
      // Skipped when the user explicitly chose "Preview as Image" so the image
      // renderer always wins regardless of the format being archive-listable.
      if (allowDescend && TryEnterAsNestedArchive(entry.Name, data))
        return;

      // Avoid empty-data preview throws.
      if (data.Length == 0) {
        StatusText = $"{entry.Name} is empty.";
        return;
      }

      var preview = new Views.PreviewWindow { Owner = Application.Current.MainWindow };
      preview.ShowData(entry.Name, data, hex);
      preview.Show();
      StatusText = "Ready";
    }
    catch (Exception ex) {
      StatusText = $"Preview error: {ex.Message}";
    }
  }

  /// <summary>
  /// If <paramref name="data"/> looks like a known archive/filesystem,
  /// materialise it to a temp file and open it as the new active archive,
  /// pushing the current archive onto <see cref="_archiveStack"/> so the user
  /// can navigate back via "..". Returns <c>true</c> on successful descent.
  /// Guards against infinite recursion via depth cap + content-hash check.
  /// </summary>
  private bool TryEnterAsNestedArchive(string entryName, byte[] data) {
    if (data.Length == 0) return false;

    // Depth cap — even legitimately nested archives don't usually exceed
    // 5-6 levels (e.g. .docx inside .zip inside .tar.gz inside .vhd). 16 is
    // generous; beyond that something has gone wrong.
    if (_archiveStack.Count >= MaxNestedDescentDepth) {
      StatusText = $"Maximum nesting depth ({MaxNestedDescentDepth}) reached — showing as bytes.";
      return false;
    }

    // Content-hash recursion guard: hash a prefix of the candidate's bytes
    // and reject if the same hash already exists in the descent chain.
    // Prevents pathological loops where a file "decodes" to itself or where
    // a descriptor's magic match is so loose that random bytes get re-entered.
    var candidateHash = HashPrefix(data);
    foreach (var frame in _archiveStack)
      if (string.Equals(frame.ContentHash, candidateHash, StringComparison.Ordinal)) {
        StatusText = $"Loop detected — '{entryName}' has the same content as a parent archive in the chain.";
        return false;
      }

    // Also check the currently-open archive (top of effective stack) so we
    // don't re-enter a file that's identical to the host archive.
    if (!string.IsNullOrEmpty(ArchivePath) && File.Exists(ArchivePath)) {
      try {
        using var fs = File.OpenRead(ArchivePath);
        var hostHash = HashPrefix(fs);
        if (string.Equals(hostHash, candidateHash, StringComparison.Ordinal)) {
          StatusText = $"Loop detected — '{entryName}' is identical to the host archive.";
          return false;
        }
      } catch { /* best effort */ }
    }

    // Quick header-byte sniff via FormatDetector. We need a file path for
    // detection-by-extension fall-through, so write to temp first.
    var tempPath = Path.Combine(Path.GetTempPath(),
                                $"cwb_nested_{Guid.NewGuid():N}_{Path.GetFileName(entryName)}");
    try {
      File.WriteAllBytes(tempPath, data);
    }
    catch {
      return false;
    }

    var format = FormatDetector.Format.Unknown;
    try { format = FormatDetector.Detect(tempPath); } catch { /* fall through */ }
    if (format == FormatDetector.Format.Unknown || FormatDetector.IsStreamFormat(format)) {
      // Not an archive (or a single-stream compressor like .gz that
      // contains exactly one payload — preview those as bytes instead).
      try { File.Delete(tempPath); } catch { /* best effort */ }
      return false;
    }

    // Image formats are registered as archives (colorspace planes via
    // MultiImageArchiveHelper). Decision rule: ENTER a child image ONLY when
    // the parent archive is NOT itself an image — that lets users descend into
    // a standalone PNG inside a ZIP/TAR to browse R/G/B/Y/Cb/Cr/etc planes.
    // Skip the descent when parent IS an image, because then the child image
    // is a frame extracted by the parent (e.g. frame_000.png inside a GIF) —
    // double-clicking it should show the picture, not recurse into another
    // colorspace tree (which would just yield the same R/G/B planes again).
    var desc = FormatRegistry.GetById(format.ToString());
    if (desc?.Category == FormatCategory.Image) {
      var parentDesc = FormatRegistry.GetById(Format);
      if (parentDesc?.Category == FormatCategory.Image) {
        try { File.Delete(tempPath); } catch { /* best effort */ }
        return false;
      }
    }

    // Save current archive context for "go back" navigation.
    if (!string.IsNullOrEmpty(ArchivePath)) {
      var parentHash = "";
      try { using var fs = File.OpenRead(ArchivePath); parentHash = HashPrefix(fs); } catch { /* best effort */ }
      _archiveStack.Push((ArchivePath, CurrentFolder, parentHash));
    }
    _nestedTempFiles.Add(tempPath);

    StatusText = $"Entering nested archive: {entryName} ({format}) — depth {_archiveStack.Count}";
    Open(tempPath, fromNestedDescent: true);
    return true;
  }

  private static string HashPrefix(byte[] data) {
    var len = Math.Min(data.Length, 64 * 1024);
    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.AsSpan(0, len)));
  }

  private static string HashPrefix(Stream stream) {
    var buf = new byte[64 * 1024];
    var read = stream.Read(buf, 0, buf.Length);
    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buf.AsSpan(0, read)));
  }

  private void CleanupNestedTempFiles() {
    foreach (var f in _nestedTempFiles) {
      try { File.Delete(f); } catch { /* best effort */ }
    }
    _nestedTempFiles.Clear();
  }

  internal IReadOnlyList<ArchiveEntryViewModel> AllEntries => _allEntries;

  internal void ShowProperties() {
    var entry = SelectedEntries.FirstOrDefault();

    // No selection → archive-level properties. Synthesise an entry that
    // represents the archive itself: name is the archive file name, sizes are
    // the on-disk size + sum of uncompressed entry sizes, modification time is
    // the file mtime. Statistics tab samples the archive bytes for histogram /
    // entropy display.
    if (entry == null || entry.IsParentEntry) {
      if (!HasArchive) return;
      var fi = new FileInfo(ArchivePath);
      var uncompressedTotal = _allEntries.Where(e => !e.IsDirectory && !e.IsParentEntry)
                                         .Sum(e => Math.Max(0, e.OriginalSize));
      var archiveEntry = new ArchiveEntryViewModel {
        Name = Path.GetFileName(ArchivePath),
        Path = ArchivePath,
        OriginalSize = uncompressedTotal > 0 ? uncompressedTotal : fi.Length,
        CompressedSize = fi.Length,
        Method = Format,
        IsDirectory = false,
        LastModified = fi.LastWriteTime,
      };
      byte[]? archiveData = null;
      try {
        // Sample up to 1 MiB so the statistics panel can show a histogram
        // without pulling a multi-GB archive into memory.
        const int Sample = 1 * 1024 * 1024;
        using var fs = File.OpenRead(ArchivePath);
        var len = (int)Math.Min(fs.Length, Sample);
        archiveData = new byte[len];
        fs.ReadExactly(archiveData, 0, len);
      } catch {
        // Best-effort sampling; properties still shown without statistics.
      }
      var archiveDlg = new Views.PropertiesWindow { Owner = Application.Current.MainWindow };
      archiveDlg.ShowProperties(archiveEntry, _allEntries, archiveData);
      archiveDlg.ShowDialog();
      return;
    }

    byte[]? data = null;
    if (!entry.IsDirectory) {
      try {
        StatusText = $"Loading {entry.Name}...";
        data = ArchiveOperations.ExtractEntry(ArchivePath, entry.Path, password: null);
        StatusText = "Ready";
      }
      catch {
        // Properties still shown without data statistics
      }
    }

    var dlg = new Views.PropertiesWindow { Owner = Application.Current.MainWindow };
    dlg.ShowProperties(entry, _allEntries, data);
    dlg.ShowDialog();
  }

  internal void ShowBenchmark() {
    var win = new Views.BenchmarkWindow { Owner = Application.Current.MainWindow };
    win.Show();
  }

  internal void ShowFileAssociations() {
    var win = new Views.FileAssociationsWindow { Owner = Application.Current.MainWindow };
    win.ShowDialog();
  }

  internal void ShowAnalyzeFile() {
    var win = new Views.AnalysisWindow { Owner = Application.Current.MainWindow };
    win.Show();
  }

  internal void ShowAnalysis() {
    var entry = SelectedEntries.FirstOrDefault(e => !e.IsDirectory && !e.IsParentEntry);
    if (entry == null) return;

    try {
      StatusText = $"Loading {entry.Name}...";
      var data = ArchiveOperations.ExtractEntry(ArchivePath, entry.Path, password: null);
      StatusText = "Ready";

      var win = new Views.AnalysisWindow { Owner = Application.Current.MainWindow };
      win.RunAnalysis(entry.Path, data);
      win.Show();
    }
    catch (Exception ex) {
      StatusText = $"Analysis error: {ex.Message}";
    }
  }

  internal void HandleFileDrop(string[] files) {
    if (!HasArchive) {
      // No archive open — open the first dropped file as an archive
      if (files.Length > 0) Open(files[0]);
      return;
    }

    // Archive is open — validate against the descriptor's constraints before adding.
    var (allowed, message) = EvaluateDropAgainstCurrentArchive(files);
    if (!allowed) {
      StatusText = message ?? "Some dropped files can't go in this archive.";
      return;
    }
    AddFilesToArchiveImpl(files);
  }

  /// <summary>
  /// Probes the current archive's descriptor: is the drop allowed by writability
  /// (IArchiveCreatable or IArchiveModifiable) and accepted by any declared
  /// IArchiveWriteConstraints? Returns the display message to use in the drop overlay.
  /// </summary>
  internal (bool Allowed, string? Message) EvaluateDropAgainstCurrentArchive(string[] files) {
    if (!HasArchive) return (true, "Drop archive to open");

    var format = FormatDetector.DetectByExtension(ArchivePath);
    if (format == FormatDetector.Format.Unknown) return (true, "Drop to add files to archive");

    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());
    // Block the drop only when the descriptor is genuinely read-only. ZIP / 7z /
    // RAR don't implement IArchiveCreatable themselves — their create path is a
    // hardcoded switch inside ArchiveOperations — so we also accept the
    // CanCreate / CanModify capability flags as evidence of write capability.
    var caps = (ops as Compression.Registry.IFormatDescriptor)?.Capabilities ?? 0;
    if (ops is not Compression.Registry.IArchiveCreatable &&
        ops is not Compression.Registry.IArchiveModifiable &&
        !caps.HasFlag(Compression.Registry.FormatCapabilities.CanCreate) &&
        !caps.HasFlag(Compression.Registry.FormatCapabilities.CanModify)) {
      return (false, "This archive format is read-only (can't add files)");
    }

    // Per-file + cumulative-size check via optional constraints.
    if (ops is Compression.Registry.IArchiveWriteConstraints constraints) {
      long cumulative = 0;
      foreach (var path in files) {
        var entryName = System.IO.Path.GetFileName(path);
        var size = new FileInfo(path).Exists ? new FileInfo(path).Length : 0;
        cumulative += size;
        var input = new Compression.Registry.ArchiveInputInfo(path, entryName, IsDirectory: false);
        if (!constraints.CanAccept(input, out var why))
          return (false, why ?? constraints.AcceptedInputsDescription);
      }
      if (constraints.MaxTotalArchiveSize is long cap && cumulative > cap)
        return (false, $"Total {cumulative} bytes exceeds this format's {cap}-byte ceiling");
    }

    return (true, "Drop to add files to archive");
  }

  private void AddFilesToArchive() {
    var dlg = new Microsoft.Win32.OpenFileDialog {
      Title = "Add files to archive",
      Multiselect = true,
      Filter = "All Files|*.*",
    };
    if (dlg.ShowDialog() != true) return;
    AddFilesToArchiveImpl(dlg.FileNames);
  }

  private void AddFilesToArchiveImpl(string[] paths) {
    var format = FormatDetector.DetectByExtension(ArchivePath);
    if (format == FormatDetector.Format.Unknown || FormatDetector.IsStreamFormat(format)) {
      MessageBox.Show("Cannot add files to this archive format.", "Add Files",
        MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    var archivePath = ArchivePath;
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());
    var prefersModifierPath = ops is Compression.Registry.IArchiveModifiable;

    // Compression options are only meaningful for the rebuild path. Skip the
    // dialog entirely when the descriptor implements IArchiveModifiable —
    // retro filesystems have no codec choices to make and we want the
    // O(touched bytes) modifier path to feel as fast as it actually is.
    Compression.Lib.CompressionOptions opts;
    if (prefersModifierPath) {
      opts = new Compression.Lib.CompressionOptions();
    } else {
      var optsDlg = new CreateOptionsWindow(format) { Owner = Application.Current.MainWindow };
      optsDlg.Title = "Add Files — Compression Options";
      if (optsDlg.ShowDialog() != true) return;
      opts = optsDlg.Options.ToOptions();
    }

    // ── Collision check ────────────────────────────────────────────────
    // Read existing entry names (cheap — just a directory walk for retro
    // FSes; for ZIP/7z this is also fast since List doesn't decompress).
    HashSet<string> existing;
    try {
      existing = new HashSet<string>(
        ArchiveOperations.List(archivePath, password: null).Select(e => e.Name),
        StringComparer.OrdinalIgnoreCase);
    } catch (Exception ex) {
      MessageBox.Show($"Failed to read archive contents: {ex.Message}",
        "Add Files", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    var pathsToAdd = new List<string>();
    var pathsToReplace = new List<string>();
    var skipAll = false;
    var yesAll = false;
    foreach (var p in paths) {
      var entryName = ResolveEntryName(p);
      if (!existing.Contains(entryName)) { pathsToAdd.Add(p); continue; }

      if (yesAll) { pathsToReplace.Add(p); continue; }
      if (skipAll) continue;

      var dlg = new Views.ReplaceConfirmationWindow(Path.GetFileName(archivePath), entryName) {
        Owner = Application.Current.MainWindow
      };
      dlg.ShowDialog();
      switch (dlg.Decision) {
        case Views.ReplaceDecision.Yes:      pathsToReplace.Add(p); break;
        case Views.ReplaceDecision.YesToAll: pathsToReplace.Add(p); yesAll = true; break;
        case Views.ReplaceDecision.Skip:     break;
        case Views.ReplaceDecision.SkipAll:  skipAll = true; break;
        case Views.ReplaceDecision.Cancel:   StatusText = "Add cancelled."; return;
      }
    }

    var totalChanges = pathsToAdd.Count + pathsToReplace.Count;
    if (totalChanges == 0) { StatusText = "Nothing to add (all collisions skipped)."; return; }

    Task.Run(() => {
      Application.Current.Dispatcher.Invoke(() => {
        IsBusy = true;
        StatusText = $"Adding {totalChanges} item(s) to archive...";
      });

      try {
        var sw = Stopwatch.StartNew();
        var folderPrefix = _currentFolder.Length > 0 ? _currentFolder.TrimEnd('/') + "/" : "";

        // Replacements — explicit Replace flow (Remove + Add) so the user's
        // intent is recorded as a single atomic op per file.
        foreach (var p in pathsToReplace) {
          if (Directory.Exists(p)) continue; // directories don't replace by name
          var entryName = folderPrefix + Path.GetFileName(p);
          ArchiveOperations.Replace(archivePath, entryName, p, opts);
        }

        // Adds — gather files (and recursively walk dropped directories) into
        // a single Add call so the modifier path can batch them.
        var addInputs = new List<ArchiveInput>();
        foreach (var p in pathsToAdd) {
          if (Directory.Exists(p)) CollectDirectory(p, folderPrefix, addInputs);
          else if (File.Exists(p)) addInputs.Add(new ArchiveInput(p, folderPrefix + Path.GetFileName(p)));
        }
        if (addInputs.Count > 0)
          ArchiveOperations.Add(archivePath, addInputs, opts);

        sw.Stop();
        Application.Current.Dispatcher.Invoke(() => {
          StatusText = $"Added {totalChanges} item(s) ({sw.ElapsedMilliseconds}ms)";
          Open(archivePath);
        });
      }
      catch (Exception ex) {
        Application.Current.Dispatcher.Invoke(() => {
          StatusText = $"Error adding files: {ex.Message}";
          IsBusy = false;
        });
      }
    });
  }

  /// <summary>Resolves the archive-relative entry name a dropped path will land at.</summary>
  private string ResolveEntryName(string path) {
    var prefix = _currentFolder.Length > 0 ? _currentFolder.TrimEnd('/') + "/" : "";
    return prefix + Path.GetFileName(path);
  }

  private static void CollectDirectory(string sourceDir, string entryPrefix, List<ArchiveInput> sink) {
    var rootName = Path.GetFileName(sourceDir);
    foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories)) {
      var rel = Path.GetRelativePath(sourceDir, dir).Replace('\\', '/');
      sink.Add(new ArchiveInput("", entryPrefix + rootName + "/" + rel + "/"));
    }
    foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)) {
      var rel = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
      sink.Add(new ArchiveInput(file, entryPrefix + rootName + "/" + rel));
    }
  }

  private static void CopyDirectory(string sourceDir, string destDir) {
    Directory.CreateDirectory(destDir);
    foreach (var file in Directory.GetFiles(sourceDir))
      File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
    foreach (var dir in Directory.GetDirectories(sourceDir))
      CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
  }

  private void CreateDialog() {
    var (filter, formats) = BuildCreateFilterWithFormats();
    var dlg = new Microsoft.Win32.SaveFileDialog {
      Title = "Create Archive",
      Filter = filter,
    };
    if (dlg.ShowDialog() != true) return;

    // FilterIndex is 1-based and tells us which item in the dropdown the user
    // chose. Trust that over the file extension because many extensions (.img,
    // .bin, .dd) are claimed by multiple filesystems — the user's drop-down
    // selection is the authoritative answer.
    var idx = dlg.FilterIndex - 1;
    var format = idx >= 0 && idx < formats.Count
      ? formats[idx]
      : FormatDetector.DetectByExtension(dlg.FileName);

    // Show options dialog
    var optsDlg = new CreateOptionsWindow(format) { Owner = Application.Current.MainWindow };
    if (optsDlg.ShowDialog() != true) return;

    // Pick input files/folder
    var inputDlg = new System.Windows.Forms.FolderBrowserDialog {
      Description = "Select folder to archive",
      UseDescriptionForTitle = true,
    };
    if (inputDlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

    var inputs = ArchiveInput.Resolve([inputDlg.SelectedPath]);
    var opts = optsDlg.Options.ToOptions();
    var makeSfx = optsDlg.Options.MakeSfx;
    var sfxStubType = optsDlg.Options.SfxIsGui ? SfxBuilder.StubType.Ui : SfxBuilder.StubType.Cli;
    var sfxTargetRid = optsDlg.Options.ResolvedSfxTargetRid;
    var outputPath = dlg.FileName;

    Task.Run(() => {
      Application.Current.Dispatcher.Invoke(() => {
        IsBusy = true;
        StatusText = $"Creating {Path.GetFileName(outputPath)}...";
      });

      var sw = Stopwatch.StartNew();
      Exception? err = null;
      try {
        ArchiveOperations.Create(outputPath, inputs, opts, format);

        if (makeSfx) {
          var sfxPath = Path.ChangeExtension(outputPath, ".exe");
          SfxBuilder.WrapExisting(outputPath, sfxPath, sfxStubType, sfxTargetRid);
          try { File.Delete(outputPath); } catch { /* leftover archive — non-fatal */ }
          outputPath = sfxPath;
        }
      } catch (Exception ex) {
        err = ex;
      }
      sw.Stop();

      Application.Current.Dispatcher.Invoke(() => {
        IsBusy = false;
        if (err != null) {
          StatusText = $"Create failed: {err.Message}";
          MessageBox.Show(
            $"Failed to create {Path.GetFileName(outputPath)}:\n\n{err.GetType().Name}: {err.Message}",
            "Create Archive", MessageBoxButton.OK, MessageBoxImage.Error);
          return;
        }
        StatusText = $"Created {Path.GetFileName(outputPath)} ({sw.ElapsedMilliseconds}ms)";
        Open(outputPath);
      });
    });
  }

  private async Task RunAsync(string status, Action work) {
    IsBusy = true;
    StatusText = status;
    var sw = Stopwatch.StartNew();
    await Task.Run(work);
    sw.Stop();
    IsBusy = false;
  }

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };

  private static string BuildOpenFilter() {
    FormatRegistration.EnsureInitialized();

    // Aggregate-pattern entry: "All Archives" with the union of every registered
    // descriptor's extensions. This is the default selection so users with
    // unknown formats still see everything.
    var allExts = new List<string>();
    foreach (var desc in FormatRegistry.All) {
      foreach (var ext in desc.CompoundExtensions) allExts.Add("*" + ext);
      foreach (var ext in desc.Extensions) allExts.Add("*" + ext);
    }
    var allUnion = string.Join(";", allExts.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e));

    // Per-descriptor entries: one filter line per registered format so the user
    // can pick e.g. "ZIP archive (*.zip)" or "TAR archive (*.tar;*.tgz)" from
    // the dropdown and have only matching files shown.
    var perFormat = new List<string>();
    foreach (var desc in FormatRegistry.All
                       .Where(d => d.Extensions.Count > 0 || d.CompoundExtensions.Count > 0)
                       .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)) {
      var descExts = new List<string>();
      foreach (var ext in desc.CompoundExtensions) descExts.Add("*" + ext);
      foreach (var ext in desc.Extensions) descExts.Add("*" + ext);
      var pattern = string.Join(";", descExts.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e));
      // Truncate the displayed pattern if too long for the dropdown.
      var shown = pattern.Length > 60 ? pattern[..57] + "..." : pattern;
      perFormat.Add($"{desc.DisplayName} ({shown})|{pattern}");
    }

    return $"All Archives|{allUnion}|{string.Join("|", perFormat)}|All Files|*.*";
  }

  private static string BuildCreateFilter() => BuildCreateFilterWithFormats().Filter;

  /// <summary>
  /// Builds the SaveFileDialog filter string AND a parallel list of format
  /// IDs, so the caller can map the dialog's 1-based FilterIndex back to the
  /// exact format the user picked. This is the only correct way to handle
  /// extensions claimed by multiple formats (e.g. <c>.img</c>).
  /// </summary>
  private static (string Filter, List<FormatDetector.Format> Formats) BuildCreateFilterWithFormats() {
    FormatRegistration.EnsureInitialized();
    var entries = new List<string>();
    var formats = new List<FormatDetector.Format>();
    foreach (var desc in FormatRegistry.All
        .Where(d => d.Capabilities.HasFlag(FormatCapabilities.CanCreate))
        .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)) {
      if (!Enum.TryParse<FormatDetector.Format>(desc.Id, out var f)) continue;
      var ext = desc.DefaultExtension;
      entries.Add($"{desc.DisplayName} (*{ext})|*{ext}");
      formats.Add(f);
    }
    return (string.Join("|", entries), formats);
  }
}

/// <summary>
/// Represents a clickable segment in the breadcrumb navigation bar.
/// </summary>
internal sealed class BreadcrumbSegment {
  public string Label { get; init; } = "";
  public string FolderPath { get; init; } = "";
}
