using Compression.NativeUI.Editing;
using Compression.NativeUI.Navigation;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Compression.Lib;
using Compression.NativeUI.Maintenance;
using Compression.NativeUI.Views;
using Compression.Registry;
using Hawkynt.NativeForms;

namespace Compression.NativeUI.ViewModels;

/// <summary>Where the entries on show are read from: the disk, or the archive at a path.</summary>
internal readonly record struct PreviewSource(bool BrowsingDisk, string ArchivePath);

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

  // Back/Forward. Fed from the one place every navigation ends, RefreshVisibleEntries, rather
  // than from each of the half-dozen routes that get there.
  private readonly NavigationHistory _history = new();
  private Location? _lastReported;

  // Some navigations pass through a place on the way somewhere else: opening an archive lands on its
  // root before the target folder is set. Those stops are not arrivals, and recording them would
  // make Back step to a root the user never chose to visit.
  private int _arrivalsSuppressed;

  /// <summary>Raised whenever the shell arrives somewhere new, however it got there.</summary>
  public event EventHandler<Location>? LocationChanged;

  /// <summary>Where the shell is now: a host folder, a folder inside the open archive, or nowhere yet.</summary>
  public Location? CurrentLocation => _osBrowserPath is not null
    ? Location.Folder(_osBrowserPath)
    : HasArchive ? Location.InArchive(ArchivePath, CurrentFolder) : null;

  /// <summary>
  /// True inside an archive that was itself opened from inside another. Its file is a temporary
  /// extraction, so its host path means nothing to the user and is not offered as a breadcrumb.
  /// </summary>
  public bool IsNestedArchive => _archiveStack.Count > 0;

  public EntryCollection Entries { get; } = [];
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
  public ICommand BackCommand { get; }
  public ICommand ForwardCommand { get; }
  public ICommand ViewAsTextCommand { get; }
  public ICommand ViewAsHexCommand { get; }
  public ICommand ViewAsImageCommand { get; }
  public ICommand AddFilesCommand { get; }
  public ICommand PropertiesCommand { get; }
  public ICommand AnalyzeCommand { get; }
  public ICommand AnalyzeFileCommand { get; }
  public ICommand BenchmarkCommand { get; }
  public ICommand FileAssociationsCommand { get; }
  // The five canonical maintenance verbs (docs/ARCHIVE-MODEL.md). Each is gated
  // by the target descriptor implementing the matching capability interface and
  // resolves its target via ResolveMaintenanceTarget — so the verbs work on a
  // standalone archive file, the currently-open archive, OR an archive entry
  // nested inside the open archive (materialised + written back via Replace).
  public ICommand OptimizeEntryCommand { get; }
  public ICommand ShrinkEntryCommand { get; }
  public ICommand DefragmentEntryCommand { get; }
  public ICommand PurgeEntryCommand { get; }
  public ICommand WipeEntryCommand { get; }
  public ICommand CompactEntryCommand { get; }
  public ICommand ScrambleEntryCommand { get; }

  /// <summary>Opens the Defragment tab on the resolved maintenance target, with no operation preselected.</summary>
  public ICommand MaintenanceCommand { get; }
  public ICommand ReconfigureEntryCommand { get; }
  public ICommand DeleteSelectedCommand { get; }
  /// <summary>Asks the view to start editing the selected entry's name; see <see cref="RenameRequested"/>.</summary>
  public ICommand RenameCommand { get; }
  public ICommand CopyCommand { get; }
  public ICommand CutCommand { get; }
  public ICommand PasteCommand { get; }
  public ICommand NewFolderCommand { get; }
  /// <summary>Rereads the folder or archive on show, staying where the user is.</summary>
  public ICommand RefreshCommand { get; }

  // The shell's own clipboard. The desktop clipboard carries only text across every backend, so
  // files copied here paste here — between folders, archives and the two, in any direction.
  private IReadOnlyList<TransferItem>? _clipboardItems;
  private bool _clipboardIsCut;

  /// <summary>True while something copied or cut is waiting to be pasted.</summary>
  public bool HasClipboard => _clipboardItems is { Count: > 0 };

  /// <summary>
  /// Raised by <see cref="RenameCommand"/>. Typing the new name is the view's business — in place,
  /// in the list — so the view-model only says which entry, and <see cref="Rename"/> does the rest.
  /// </summary>
  public event EventHandler<ArchiveEntryViewModel>? RenameRequested;

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

  /// <summary>
  /// The shell window, used as the owner of modal dialogs. The view sets it once; nothing here
  /// reaches for a global "current window", which does not exist in NativeForms.
  /// </summary>
  public Form? Owner { get; set; }

  /// <summary>
  /// Runs an action on the UI thread. Long operations report from worker threads, and only the view
  /// knows which control owns that thread, so it supplies the marshaller.
  /// </summary>
  public Action<Action>? UiMarshaller { get; set; }

  private void Marshal(Action action) {
    if (this.UiMarshaller is { } marshaller) marshaller(action);
    else action();
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
    NavigateToBreadcrumbCommand = new RelayCommand(p => {
      if (p is Location target) NavigateTo(target);
      else NavigateToBreadcrumb(p as string);
    });
    // A dead entry is simply stepped over: the history has already moved past it.
    BackCommand = new RelayCommand(_ => { if (_history.Back() is { } target) NavigateTo(target); }, _ => _history.CanGoBack);
    ForwardCommand = new RelayCommand(_ => { if (_history.Forward() is { } target) NavigateTo(target); }, _ => _history.CanGoForward);
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
    OptimizeEntryCommand = new RelayCommand(_ => OpenMaintenance(MaintenanceVerb.Optimize), _ => CanMaintain(MaintenanceVerb.Optimize));
    ShrinkEntryCommand = new RelayCommand(_ => OpenMaintenance(MaintenanceVerb.Shrink), _ => CanMaintain(MaintenanceVerb.Shrink));
    DefragmentEntryCommand = new RelayCommand(_ => OpenMaintenance(MaintenanceVerb.Defragment), _ => CanMaintain(MaintenanceVerb.Defragment));
    PurgeEntryCommand = new RelayCommand(_ => OpenMaintenance(MaintenanceVerb.Purge), _ => CanMaintain(MaintenanceVerb.Purge));
    WipeEntryCommand = new RelayCommand(_ => OpenMaintenance(MaintenanceVerb.WipeEmpty), _ => CanMaintain(MaintenanceVerb.WipeEmpty));
    CompactEntryCommand = new RelayCommand(_ => OpenMaintenance(MaintenanceVerb.Compact), _ => CanMaintain(MaintenanceVerb.Compact));
    ScrambleEntryCommand = new RelayCommand(_ => OpenMaintenance(MaintenanceVerb.Scramble), _ => CanMaintain(MaintenanceVerb.Scramble));
    MaintenanceCommand = new RelayCommand(_ => MaintenanceRequested?.Invoke(this, null), _ => HasMaintenanceTarget);
    ReconfigureEntryCommand = new RelayCommand(_ => Reconfigure(), _ => CanReconfigure());
    DeleteSelectedCommand = new RelayCommand(_ => DeleteSelectedEntries(), _ => CanDeleteSelected);
    CopyCommand = new RelayCommand(_ => PutSelectionOnClipboard(cut: false), _ => CanCopySelection());
    CutCommand = new RelayCommand(_ => PutSelectionOnClipboard(cut: true), _ => CanCopySelection() && CanChangeHere());
    PasteCommand = new AsyncRelayCommand(_ => PasteAsync(), _ => HasClipboard && CanChangeHere());
    // On disk only: several writers - zip's among them - keep files, not bare folders, so an empty
    // folder made inside an archive would vanish on the next rebuild.
    RefreshCommand = new RelayCommand(_ => {
      if (HasArchive && !IsBrowsingOsFolder) ReloadArchiveInPlace();
      else RefreshVisibleEntries();
    }, _ => CurrentLocation is not null);
    NewFolderCommand = new RelayCommand(_ => CreateNewFolder(), _ => _osBrowserPath is not null);
    RenameCommand = new RelayCommand(
      _ => { if (SingleSelection() is { } entry) RenameRequested?.Invoke(this, entry); },
      _ => SingleSelection() is { } entry && CanRename(entry));
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
  /// <summary>
  /// Makes a folder called "New folder" - or "New folder (2)" and so on when that is taken - in the
  /// folder being browsed, and asks the view to let the user name it straight away. Returns its name,
  /// or null when it could not be made (the reason is in the status line).
  /// </summary>
  internal string? CreateNewFolder() {
    if (_osBrowserPath is not { } parent) return null;
    if (IsChanging) {
      StatusText = "Another change is still running.";
      return null;
    }

    try {
      var taken = new HashSet<string>(Directory.EnumerateFileSystemEntries(parent).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
      var name = Transfer.FreeName("New folder", taken);
      Directory.CreateDirectory(Path.Combine(parent, name));
      RefreshVisibleEntries();
      StatusText = $"Created {name}.";
      if (CurrentLocation is { } here) RaiseChanged([here]);

      if (Entries.FirstOrDefault(e => e.Name == name) is { } created) {
        SelectedEntries.Clear();
        SelectedEntries.Add(created);
        RenameRequested?.Invoke(this, created);
      }

      return name;
    } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
      StatusText = $"Could not create a folder: {ex.Message}";
      return null;
    }
  }

  private bool CanCopySelection()
    => CurrentLocation is not null
       && SelectedEntries.Any(e => !e.IsParentEntry)
       && !SelectedEntries.Any(e => e.IsEncrypted);

  /// <summary>
  /// Whether what is shown here may be changed. A nested archive is a temporary extraction: taking
  /// from it is fine, but cutting from or pasting into it would edit a copy nobody sees.
  /// </summary>
  private bool CanChangeHere()
    => CurrentLocation is not null && !IsNestedArchive && !IsChanging
       && (IsBrowsingOsFolder || ArchiveIsModifiable());

  // Whether the open archive accepts changes is a property of its format; asked once per archive.
  private (string Path, bool Answer)? _modifiable;

  private bool ArchiveIsModifiable() {
    if (_modifiable is { } known && known.Path == ArchivePath) return known.Answer;

    var answer = DeleteCapability.Evaluate(false, ArchivePath, 1) == DeleteMode.ModifiableArchive;
    _modifiable = (ArchivePath, answer);
    return answer;
  }

  // ── one change at a time ────────────────────────────────────────────────────────────────────
  //
  // A transfer, a drop, a delete, a rename: each rewrites a folder or an archive, and two of them at
  // once can both pick the same free name and have the second overwrite the first. Background reads
  // (preview, thumbnails) hold the archive open for reading, which makes an exclusive write fail
  // with "in use"; a change therefore waits for them to finish and turns new ones away meanwhile.

  private readonly object _activity = new();
  private int _backgroundReads;

  /// <summary>True while a change to a folder or an archive is running.</summary>
  public bool IsChanging { get; private set; }

  /// <summary>Registers a background read; false while a change is running, and the read should skip.</summary>
  internal bool TryBeginBackgroundRead() {
    lock (_activity) {
      if (IsChanging) return false;
      ++_backgroundReads;
      return true;
    }
  }

  internal void EndBackgroundRead() {
    lock (_activity) --_backgroundReads;
  }

  /// <summary>
  /// Claims the right to change something, waiting (up to <paramref name="patience"/>) for background
  /// reads to finish. Returns the reason when another change is already running.
  /// </summary>
  private string? BeginChange(TimeSpan patience) {
    lock (_activity) {
      if (IsChanging) return "Another change is still running.";
      IsChanging = true;
    }

    var until = DateTime.UtcNow + patience;
    while (Volatile.Read(ref _backgroundReads) > 0 && DateTime.UtcNow < until)
      Thread.Sleep(15);

    CommandManager.InvalidateRequerySuggested();
    return null;
  }

  private void EndChange() {
    lock (_activity) IsChanging = false;
    CommandManager.InvalidateRequerySuggested();
  }

  /// <summary>Raised after a change, with the places whose contents it altered, so views can reread them.</summary>
  public event EventHandler<IReadOnlyList<Location>>? Changed;

  private void RaiseChanged(IEnumerable<Location> places)
    => Changed?.Invoke(this, [.. places.Distinct()]);

  /// <summary>Puts <paramref name="items"/> on the clipboard, as copy or cut — from the list or from the tree.</summary>
  internal void PutOnClipboard(IReadOnlyList<TransferItem> items, bool cut) {
    if (items.Count == 0) return;

    _clipboardItems = items;
    _clipboardIsCut = cut;
    StatusText = $"{(cut ? "Cut" : "Copied")} {items.Count} item(s). Paste to put {(items.Count == 1 ? "it" : "them")} somewhere.";
    OnPropertyChanged(nameof(HasClipboard));
    CommandManager.InvalidateRequerySuggested();
  }

  private void PutSelectionOnClipboard(bool cut) {
    if (CurrentLocation is null) return;
    PutOnClipboard(SelectionAsTransferItems(), cut);
  }

  /// <summary>
  /// Pastes what was copied or cut into the folder the shell is showing. Returns the reason when it
  /// cannot, having touched nothing; a cut is consumed once it has been pasted.
  /// </summary>
  internal async Task<string?> PasteAsync() {
    if (_clipboardItems is not { Count: > 0 } items || CurrentLocation is not { } target) return "Nothing to paste.";

    var move = _clipboardIsCut;
    var error = await TransferAsync(items, target, move);
    if (error is null && move) {
      _clipboardItems = null;
      OnPropertyChanged(nameof(HasClipboard));
    }

    return error;
  }

  /// <summary>The selected entries, as things that can be copied or moved from where the shell is.</summary>
  internal IReadOnlyList<TransferItem> SelectionAsTransferItems()
    => CurrentLocation is { } here
      ? [.. SelectedEntries.Where(e => !e.IsParentEntry).Select(e => new TransferItem(here, e.Name, e.IsDirectory))]
      : [];

  /// <summary>
  /// Why <paramref name="items"/> cannot go to <paramref name="target"/>, or null when they can — the
  /// transfer's own rules, plus the shell's: nothing changes inside a nested archive's temporary copy.
  /// </summary>
  internal string? WhyNotTransfer(IReadOnlyList<TransferItem> items, Location target, bool move) {
    if (IsChanging) return "Another change is still running.";
    if (IsNestedCopy(target)) return "Nothing can be put inside a nested archive.";
    if (items.FirstOrDefault(IsEncryptedHere) is { } locked)
      return $"{locked.Name} is encrypted, and the shell has no password to read it with.";
    if (move && items.Any(i => IsNestedCopy(i.From))) return "Nothing can be moved out of a nested archive.";
    return Transfer.WhyNot(items, target, move);
  }

  /// <summary>Whether <paramref name="item"/> lies in the open archive and is, or holds, an encrypted entry.</summary>
  private bool IsEncryptedHere(TransferItem item)
    => item.From.IsInArchive && HasArchive && Transfer.SamePath(item.From.HostPath, ArchivePath)
       && _allEntries.Any(e => e.IsEncrypted && (e.Path.TrimEnd('/') == item.EntryPath || e.Path.StartsWith(item.EntryPath + "/", StringComparison.Ordinal)));

  private bool IsNestedCopy(Location place)
    => IsNestedArchive && place.IsInArchive && string.Equals(place.HostPath, ArchivePath, StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Copies or moves <paramref name="items"/> into <paramref name="target"/> — a paste, or a drop on a
  /// folder in the list or the tree — then shows the result. Returns the reason on failure, having
  /// touched nothing when the transfer was refused.
  /// </summary>
  internal async Task<string?> TransferAsync(IReadOnlyList<TransferItem> items, Location target, bool move) {
    if (WhyNotTransfer(items, target, move) is { } refusal) return Fail(refusal);
    if (BeginChange(TimeSpan.FromSeconds(10)) is { } busy) return Fail(busy);

    IsBusy = true;
    StatusText = $"{(move ? "Moving" : "Copying")} {items.Count} item(s)...";
    try {
      var result = await Task.Run(() => Transfer.Run(items, target, move));

      // What the list shows may have changed from either end: what arrived, or what left.
      if (HasArchive && !IsBrowsingOsFolder) ReloadArchiveInPlace();
      else RefreshVisibleEntries();

      StatusText = $"{(move ? "Moved" : "Copied")} {result.Created.Count} item(s) to {target}.";
      RaiseChanged(move ? [target, .. items.Select(i => i.From)] : [target]);
      return null;
    } catch (Exception ex) {
      // Whatever failed - the file system, a refused name, a codec on a damaged archive - sources are
      // removed only after every copy has landed and been read back, so the user is told and
      // nothing is lost; the failure must not escape a fire-and-forget drop or an async handler.
      return Fail($"{(move ? "Move" : "Copy")} failed: {ex.Message}");
    } finally {
      IsBusy = false;
      EndChange();
    }

    string Fail(string reason) {
      StatusText = reason;
      return reason;
    }
  }

  /// <summary>The largest entry the preview pane reads for a glance; the preview window has no such limit.</summary>
  internal const long PreviewPaneLimit = 32L * 1024 * 1024;

  /// <summary>
  /// The bytes the preview pane shows for <paramref name="entry"/>, or null and the caption to show
  /// instead: a folder, an encrypted entry, one too large to read for a glance, one that cannot be
  /// read. Safe to call off the UI thread; it changes nothing.
  /// </summary>
  internal (byte[]? Data, string Caption) ReadForPreview(ArchiveEntryViewModel entry)
    => ReadForPreview(entry, CapturePreviewSource());

  /// <summary>Where entries are read from right now — captured on the UI thread for a background read.</summary>
  internal PreviewSource CapturePreviewSource() => new(IsBrowsingOsFolder, ArchivePath);

  /// <summary>
  /// <see cref="ReadForPreview(ArchiveEntryViewModel)"/> against a source captured earlier, so a read
  /// that runs after the user has moved on reads from where the entry was, not from where the shell
  /// is now. Registers as a background read: while a change is running it reads nothing.
  /// </summary>
  internal (byte[]? Data, string Caption) ReadForPreview(ArchiveEntryViewModel entry, PreviewSource source) {
    if (entry.IsParentEntry) return (null, "");
    if (entry.IsDirectory) return (null, $"{entry.Name}{Environment.NewLine}Folder");
    if (entry.IsEncrypted) return (null, $"{entry.Name}{Environment.NewLine}Encrypted");

    var size = source.BrowsingDisk ? SafeLength(entry.Path) : entry.OriginalSize;
    if (size > PreviewPaneLimit)
      return (null, $"{entry.Name}{Environment.NewLine}Too large to preview ({FormatSize(size)})");

    if (!TryBeginBackgroundRead()) return (null, $"{entry.Name}{Environment.NewLine}Waiting for a change to finish");
    try {
      var data = source.BrowsingDisk
        ? File.ReadAllBytes(entry.Path)
        : ArchiveOperations.ExtractEntry(source.ArchivePath, entry.Path, password: null);
      return (data, entry.Name);
    } catch (Exception ex) {
      // A preview is a glance: whatever stopped the read - a locked file, a damaged entry, a codec
      // throwing on bytes it did not expect - it is named in the pane and nothing else happens.
      return (null, $"{entry.Name}{Environment.NewLine}Cannot be read: {ex.Message}");
    } finally {
      EndBackgroundRead();
    }

    static long SafeLength(string path) {
      try {
        return new FileInfo(path).Length;
      } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
        return 0;
      }
    }
  }

  private ArchiveEntryViewModel? SingleSelection() {
    var selected = SelectedEntries.Where(e => !e.IsParentEntry).Take(2).ToList();
    return selected.Count == 1 ? selected[0] : null;
  }

  /// <summary>
  /// Whether <paramref name="entry"/> can be given a new name where the shell is: any file or folder
  /// on disk, and an entry of an archive whose format can be modified.
  /// </summary>
  /// <remarks>
  /// Two archive cases are refused outright. A nested archive is a temporary extraction, so renaming
  /// inside it would change a copy nobody sees. An encrypted one would be rebuilt without its
  /// password, which the rebuild does not have.
  /// </remarks>
  internal bool CanRename(ArchiveEntryViewModel entry) {
    if (entry.IsParentEntry) return false;
    if (IsBrowsingOsFolder) return true;
    if (!HasArchive || IsNestedArchive) return false;

    return ArchiveRenamesLosslessly();
  }

  // Whether the open archive can rename losslessly is a property of the file, and finding out may
  // probe it; the answer is kept for as long as the same file, unchanged, stays open.
  private (string Path, DateTime Stamp, bool Answer)? _renameSupport;

  private bool ArchiveRenamesLosslessly() {
    var stamp = File.Exists(ArchivePath) ? File.GetLastWriteTimeUtc(ArchivePath) : default;
    if (_renameSupport is { } known && known.Path == ArchivePath && known.Stamp == stamp) return known.Answer;

    var answer = ArchiveOperations.CanRename(ArchivePath);
    _renameSupport = (ArchivePath, stamp, answer);
    return answer;
  }

  /// <summary>
  /// Gives <paramref name="entry"/> the name <paramref name="typed"/>, on disk or inside the open
  /// archive. Completes with null on success — including when the name did not change — or the
  /// reason it could not be done, which is also shown in the status line. An archive is rewritten
  /// off the UI thread.
  /// </summary>
  internal async Task<string?> RenameAsync(ArchiveEntryViewModel entry, string typed) {
    var error = await RenameCoreAsync(entry, typed);
    if (error is not null) StatusText = error;
    return error;
  }

  private async Task<string?> RenameCoreAsync(ArchiveEntryViewModel entry, string typed) {
    if (!CanRename(entry)) return $"{entry.Name} cannot be renamed here.";

    var (name, invalid) = EntryName.Validate(typed);
    if (invalid is not null) return invalid;
    if (name == entry.Name) return null;

    // Two names differing only in case cannot both exist on Windows or macOS, and would not
    // survive extraction there even inside an archive, so a clash is judged without regard to case.
    var caseOnly = string.Equals(name, entry.Name, StringComparison.OrdinalIgnoreCase);
    if (!caseOnly && Entries.Any(e => !e.IsParentEntry && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
      return $"\"{name}\" already exists here.";

    try {
      IsBusy = true;
      StatusText = $"Renaming {entry.Name}...";
      if (IsBrowsingOsFolder) {
        RenameOnDisk(entry, name!, caseOnly);
        RefreshVisibleEntries();
      } else {
        var archive = ArchivePath;
        var rename = new ArchiveRename(entry.Path, Location.NormalizeArchiveFolder(CurrentFolder) + name);
        await Task.Run(() => ArchiveOperations.Rename(archive, [rename]));
        ReloadArchiveInPlace();
      }
    } catch (Exception ex) {
      // Whatever went wrong - a refused name, a locked file, a codec failing on a damaged archive -
      // the archive is intact (it is replaced only once the renamed copy is complete) and the user
      // is told, rather than the failure escaping an event handler.
      return $"Could not rename {entry.Name}: {ex.Message}";
    } finally {
      IsBusy = false;
    }

    StatusText = $"Renamed {entry.Name} to {name}.";
    return null;
  }

  private static void RenameOnDisk(ArchiveEntryViewModel entry, string name, bool caseOnly) {
    var source = entry.Path;
    var target = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source))!, name);
    var isFolder = Directory.Exists(source);

    if (!caseOnly) {
      Move(source, target);
      return;
    }

    // Not every platform moves a file onto a name that differs only in case; go through a free
    // one, and put it back if the second step fails rather than leaving it under the interim name.
    var interim = source + ".cwb-rename-" + Guid.NewGuid().ToString("N")[..8];
    Move(source, interim);
    try {
      Move(interim, target);
    } catch {
      Move(interim, source);
      throw;
    }

    void Move(string from, string to) {
      if (isFolder) Directory.Move(from, to);
      else File.Move(from, to);
    }
  }

  /// <summary>
  /// Rereads the open archive after something changed it — an edit, a defragment, a reconfigure —
  /// and stays where the user is. A plain <see cref="Open(string)"/> would drop them at the root,
  /// forget where <c>..</c> leads, and, inside a nested archive, clear the chain back to the archives
  /// it came from. The folder is kept only while something is still in it.
  /// </summary>
  internal void ReloadArchiveInPlace() {
    if (!HasArchive) return;

    var folder = CurrentFolder;
    var exitTo = _priorOsBrowserPath;
    var nested = IsNestedArchive;
    AsOneArrival(() => {
      Open(ArchivePath, fromNestedDescent: nested);
      _priorOsBrowserPath = exitTo;
      CurrentFolder = StillHasEntries(folder) ? folder : "";
      RefreshVisibleEntries();
      return true;
    });

    bool StillHasEntries(string candidate) {
      var prefix = Location.NormalizeArchiveFolder(candidate);
      return prefix.Length == 0 || _allEntries.Any(e => e.Path.StartsWith(prefix, StringComparison.Ordinal));
    }
  }

  private void DeleteSelectedEntries() {
    var selected = SelectedEntries.Where(e => !e.IsParentEntry).ToList();
    if (selected.Count == 0) return;

    var mode = Compression.Lib.DeleteCapability.Evaluate(
      IsBrowsingOsFolder, ArchivePath, selected.Count);

    if (mode == Compression.Lib.DeleteMode.ReadOnlyArchive) {
      MessageBox.Show(
        "This format is read-only — entries can't be removed without rebuilding the container.\n\nUse File → Create to write a new archive that excludes them.",
        "Cannot delete",
        MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                                  MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
    if (confirm != DialogResult.Yes) return;

    _ = DeleteAndReportAsync();

    async Task DeleteAndReportAsync() {
      // A rewritten archive is worth a dialog; a file that would not go on disk is named in the
      // status line, next to the ones that did.
      if (await DeleteAsync(selected) is { } error && mode == Compression.Lib.DeleteMode.ModifiableArchive)
        MessageBox.Show(error, "Delete", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
  }

  /// <summary>
  /// Deletes <paramref name="selection"/> — already confirmed — from disk or from the open archive.
  /// The files are removed and the archive rewritten off the UI thread. Completes with null on
  /// success or the reason it failed, which is also shown in the status line.
  /// </summary>
  internal async Task<string?> DeleteAsync(IReadOnlyList<ArchiveEntryViewModel> selection) {
    var selected = selection.Where(e => !e.IsParentEntry).ToList();
    if (selected.Count == 0) return null;

    var mode = Compression.Lib.DeleteCapability.Evaluate(IsBrowsingOsFolder, ArchivePath, selected.Count);
    if (mode is not (Compression.Lib.DeleteMode.RealFs or Compression.Lib.DeleteMode.ModifiableArchive))
      return StatusText = "Entries of this format cannot be removed in place.";
    if (BeginChange(TimeSpan.FromSeconds(10)) is { } busy) return StatusText = busy;

    try {
      IsBusy = true;
      return mode == Compression.Lib.DeleteMode.RealFs
        ? await DeleteOnDiskAsync(selected)
        : await DeleteInArchiveAsync(selected);
    } finally {
      IsBusy = false;
      EndChange();
      if (CurrentLocation is { } here) RaiseChanged([here]);
    }
  }

  private async Task<string?> DeleteOnDiskAsync(List<ArchiveEntryViewModel> selected) {
    StatusText = $"Deleting {selected.Count} item(s)...";
    var targets = selected.Select(e => (e.Name, e.Path)).ToList();
    var failures = await Task.Run(() => {
      var failed = new List<string>();
      foreach (var (name, path) in targets) {
        try {
          if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
          else if (File.Exists(path)) File.Delete(path);
        } catch (Exception ex) {
          failed.Add($"{name}: {ex.Message}");
        }
      }
      return failed;
    });

    RefreshVisibleEntries();
    if (failures.Count == 0) {
      StatusText = $"Deleted {selected.Count} item(s).";
      return null;
    }

    return StatusText = $"Deleted {selected.Count - failures.Count} of {selected.Count} item(s); {string.Join("; ", failures)}";
  }

  private async Task<string?> DeleteInArchiveAsync(List<ArchiveEntryViewModel> selected) {
    // Every entry beneath a folder goes too, so the modifier sees each one; folders carry the
    // trailing slash their entries are stored under.
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

    var archive = ArchivePath;
    try {
      StatusText = $"Deleting {names.Count} entry(ies) from archive...";
      await Task.Run(() => ArchiveOperations.Remove(archive, [.. names]));
    } catch (Exception ex) {
      // Whatever stopped it - a locked file, a codec failing on a damaged archive - the user is
      // told instead of the failure escaping a fire-and-forget command.
      return StatusText = $"Delete failed: {ex.Message}";
    }

    // The modifier changed the file in place; reread it. The reload clears the fragmentation hint,
    // which this delete has just made true.
    ReloadArchiveInPlace();
    HasPendingFragmentation = true;
    StatusText = $"Deleted {names.Count} entry(ies). Free space available — defragment to compact.";
    return null;
  }

  /// <summary>
  /// Resolves which archive a maintenance verb should target, <em>without</em>
  /// materialising anything (cheap enough for CanExecute). Two cases, in
  /// priority order:
  /// <list type="number">
  ///   <item>A single selected non-directory entry whose extension routes to a
  ///   recognized archive/filesystem format — either a real on-disk file
  ///   (OS-browser) or an archive nested inside the currently-open archive.
  ///   <paramref name="archiveEntry"/> is set to it.</item>
  ///   <item>Otherwise the currently-open archive itself (which is a temp file
  ///   when the user has descended into a nested archive — so the verbs remain
  ///   available "even when this is an archive within another one").</item>
  /// </list>
  /// Returns <c>false</c> (verb disabled) when neither applies or the registry
  /// is not yet warm.
  /// </summary>
  private bool TryResolveMaintenanceTarget(out string formatId, out ArchiveEntryViewModel? archiveEntry) {
    formatId = "";
    archiveEntry = null;
    // Registry not yet warm → disable rather than blocking the UI thread. App
    // fires CommandManager.InvalidateRequerySuggested when registration ends.
    if (!Compression.Lib.FormatRegistration.IsReady) return false;

    if (SelectedEntries.Count == 1) {
      var e = SelectedEntries[0];
      if (!e.IsDirectory && !e.IsParentEntry) {
        // A file on disk is judged by its content (cached, since this runs on every requery); an
        // entry inside an archive has no bytes at hand, so its name decides.
        var probeName = IsBrowsingOsFolder ? e.Path : e.Name;
        if (!string.IsNullOrEmpty(probeName)) {
          var f = IsBrowsingOsFolder && File.Exists(e.Path)
            ? FormatDetector.DetectCached(e.Path)
            : FormatDetector.DetectByExtension(probeName);
          if (f != FormatDetector.Format.Unknown && !FormatDetector.IsStreamFormat(f)) {
            // OS-browser candidate must actually exist on disk.
            if (!IsBrowsingOsFolder || File.Exists(e.Path)) {
              formatId = f.ToString();
              archiveEntry = e;
              return true;
            }
          }
        }
      }
    }

    // Fall back to the open archive itself — while the shell is in it; a folder on disk being
    // browsed is not the archive left behind.
    if (HasArchive && !IsBrowsingOsFolder && !string.IsNullOrEmpty(ArchivePath) && !string.IsNullOrEmpty(Format)
        && Format != FormatDetector.Format.Unknown.ToString()) {
      formatId = Format;
      return true;
    }
    return false;
  }

  /// <summary>
  /// True when the resolved maintenance target supports <paramref name="verb"/>. Asks the same
  /// capability adapter the Defragment tab enables its ribbon from, so a context-menu item is never
  /// offered for an operation the tab would then refuse.
  /// </summary>
  private bool CanMaintain(MaintenanceVerb verb)
    => TryResolveMaintenanceTarget(out var formatId, out _) && MaintenanceCapabilities.For(formatId).Verb(verb).Supported;

  /// <summary>Whether there is a maintenance target that supports at least one operation.</summary>
  internal bool HasMaintenanceTarget
    => TryResolveMaintenanceTarget(out var formatId, out _) && MaintenanceCapabilities.For(formatId).AnySupported;

  /// <summary>
  /// Identifies the resolved maintenance target — the selected archive file or entry, else the open
  /// archive — so the view can tell whether a session still belongs to what is selected. Null when
  /// there is none.
  /// </summary>
  internal string? MaintenanceTargetKey
    => TryResolveMaintenanceTarget(out var formatId, out var entry)
      ? $"{formatId}|{(entry is null ? ArchivePath : IsBrowsingOsFolder ? entry.Path : ArchivePath + "::" + entry.Path)}"
      : null;

  /// <summary>
  /// Raised when a maintenance command asks for the Defragment tab, with the operation to preselect
  /// (null for none). The view owns the tab, so it is the one that opens it.
  /// </summary>
  public event EventHandler<MaintenanceVerb?>? MaintenanceRequested;

  private void OpenMaintenance(MaintenanceVerb verb) => MaintenanceRequested?.Invoke(this, verb);

  /// <summary>
  /// Opens the resolved target for maintenance. A real file — on disk, or the open archive — is
  /// maintained in place. An archive nested inside the open archive is extracted to a temporary file,
  /// maintained there, and written back into the host after each change when the host is
  /// <see cref="IArchiveModifiable"/>; the session deletes the copy when disposed.
  /// </summary>
  internal MaintenanceSession? OpenMaintenanceSession() {
    if (!TryResolveMaintenanceTarget(out var formatId, out var entry) || MaintenanceTargetKey is not { } key) return null;

    if (entry != null && IsBrowsingOsFolder) {
      if (string.IsNullOrEmpty(entry.Path) || !File.Exists(entry.Path)) return null;
      return new(key, entry.Path, formatId, entry.Name, _ => RefreshVisibleEntries(), cleanup: null);
    }

    if (entry != null) {
      try {
        var bytes = ArchiveOperations.ExtractEntry(ArchivePath, entry.Path, password: null);
        var temp = Path.Combine(Path.GetTempPath(), $"cwb_maint_{Guid.NewGuid():N}_{Path.GetFileName(entry.Name)}");
        File.WriteAllBytes(temp, bytes);

        var hostPath = ArchivePath;
        var entryName = entry.Path;
        var entryLabel = entry.Name;
        var writable = FormatRegistry.GetArchiveOps(Format) is IArchiveModifiable;
        if (!writable)
          StatusText = $"'{Format}' is read-only — '{entryLabel}' is maintained in a copy that is not written back.";

        return new(key, temp, formatId, entryLabel, _ => {
          if (!writable) return;
          try {
            ArchiveOperations.Replace(hostPath, entryName, temp);
            StatusText = $"Wrote maintained '{entryLabel}' back into {Path.GetFileName(hostPath)}.";
          } catch (Exception ex) {
            StatusText = $"Could not write '{entryLabel}' back into host archive: {ex.Message}";
          }

          if (HasArchive) ReloadArchiveInPlace();
        }, cleanup: () => {
          try { File.Delete(temp); } catch { /* best effort */ }
        });
      } catch (Exception ex) {
        StatusText = $"Cannot open nested archive '{entry.Name}': {ex.Message}";
        return null;
      }
    }

    // The open archive itself: a top-level file, or a descended nested temp — the latter mutates the
    // temp only, matching the existing nested-edit limitation.
    if (string.IsNullOrEmpty(ArchivePath) || !File.Exists(ArchivePath)) return null;
    var archivePath = ArchivePath;
    return new(key, archivePath, formatId, Path.GetFileName(archivePath), mutated => {
      if (HasArchive && string.Equals(mutated, ArchivePath, StringComparison.OrdinalIgnoreCase)) ReloadArchiveInPlace();
    }, cleanup: null);
  }

  /// <summary>
  /// True when the resolved maintenance target's descriptor can be re-created
  /// (<see cref="IArchiveCreatable"/>) and publishes a tunable options schema
  /// (<see cref="IFormatOptionsSchema"/>) with at least one knob — the
  /// preconditions for offering an after-creation geometry/options change.
  /// </summary>
  private bool CanReconfigure() {
    if (!TryResolveMaintenanceTarget(out var formatId, out _)) return false;
    var ops = FormatRegistry.GetArchiveOps(formatId);
    return ops is IArchiveCreatable
        && ops is IFormatOptionsSchema schema
        && schema.OptionsSchema.Count > 0;
  }

  /// <summary>
  /// Opens the format's schema-driven options dialog pre-populated with the
  /// current format's knobs and, on OK, re-creates the container in place with
  /// the chosen geometry/options via
  /// <see cref="ReconfigureOperation.Reconfigure(string, IReadOnlyDictionary{string, string}, string?)"/>.
  /// Contents are preserved byte-for-byte; on any failure the original is left
  /// untouched. For a nested archive entry inside the open archive, the result
  /// is written back into the host when the host is <see cref="IArchiveModifiable"/>.
  /// </summary>
  private void Reconfigure() {
    if (!TryResolveMaintenanceTarget(out var formatId, out var entry)) return;
    if (!Enum.TryParse<FormatDetector.Format>(formatId, out var format)) return;

    string targetPath;
    Action? cleanup = null;
    Action? writeBack = null;

    if (entry != null && IsBrowsingOsFolder) {
      targetPath = entry.Path;
      if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath)) return;
    } else if (entry != null) {
      // Nested archive entry: materialise → reconfigure → write back into host.
      try {
        var bytes = ArchiveOperations.ExtractEntry(ArchivePath, entry.Path, password: null);
        var temp = Path.Combine(Path.GetTempPath(),
          $"cwb_reconfig_{Guid.NewGuid():N}_{Path.GetFileName(entry.Name)}");
        File.WriteAllBytes(temp, bytes);
        targetPath = temp;
        cleanup = () => { try { File.Delete(temp); } catch { /* best effort */ } };

        var hostPath = ArchivePath;
        var entryName = entry.Path;
        var entryLabel = entry.Name;
        if (FormatRegistry.GetArchiveOps(Format) is IArchiveModifiable) {
          writeBack = () => {
            try {
              ArchiveOperations.Replace(hostPath, entryName, temp);
              StatusText = $"Wrote reconfigured '{entryLabel}' back into {Path.GetFileName(hostPath)}.";
            } catch (Exception ex) {
              StatusText = $"Could not write '{entryLabel}' back into host archive: {ex.Message}";
            }
          };
        } else {
          StatusText = $"'{Format}' is read-only — '{entryLabel}' will be reconfigured in a copy that is not written back.";
        }
      } catch (Exception ex) {
        StatusText = $"Cannot open nested archive '{entry.Name}': {ex.Message}";
        return;
      }
    } else {
      if (string.IsNullOrEmpty(ArchivePath) || !File.Exists(ArchivePath)) return;
      targetPath = ArchivePath;
    }

    var optsDlg = new CreateOptionsWindow(format);
    optsDlg.Text = "Reconfigure — Geometry / Options";
    var ok = optsDlg.ShowDialog(this.Owner) == DialogResult.OK;
    if (!ok) { cleanup?.Invoke(); return; }

    var newOptions = optsDlg.Options.FormatSpecificOptions.Count > 0
      ? optsDlg.Options.FormatSpecificOptions.ToDictionary(o => o.Key, o => o.CurrentValue)
      : new Dictionary<string, string>();
    if (newOptions.Count == 0) {
      StatusText = $"'{format}' exposes no reconfigurable options.";
      cleanup?.Invoke();
      return;
    }

    try {
      var result = ReconfigureOperation.Reconfigure(targetPath, newOptions);
      writeBack?.Invoke();
      StatusText = $"Reconfigured {Path.GetFileName(targetPath)}: "
        + $"{result.FileCount} file(s) preserved, {result.OriginalSize:N0} → {result.NewSize:N0} bytes.";

      if (writeBack != null && HasArchive)
        ReloadArchiveInPlace();
      else if (HasArchive && string.Equals(targetPath, ArchivePath, StringComparison.OrdinalIgnoreCase))
        ReloadArchiveInPlace();
      else if (IsBrowsingOsFolder)
        RefreshVisibleEntries();
    } catch (Exception ex) {
      StatusText = $"Reconfigure failed: {ex.Message}";
      MessageBox.Show($"Reconfigure failed: {ex.Message}\n\nThe original file was left untouched.",
        "Reconfigure", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    } finally {
      cleanup?.Invoke();
    }
  }

  private bool HasSelectedFile => SelectedEntries.Any(e => !e.IsDirectory && !e.IsParentEntry);
  // Same wait-free guard as CanDefragmentSelected — see note there.
  private bool CanAddFiles
    => !string.IsNullOrEmpty(ArchivePath)
       && Compression.Lib.FormatRegistration.IsReady
       && _openFormat != FormatDetector.Format.Unknown
       && !FormatDetector.IsStreamFormat(_openFormat)
       && OpenArchiveTakesAdditions();

  /// <summary>
  /// Whether the open archive's format can take new files at all. ZIP / 7z / RAR don't implement
  /// IArchiveCreatable themselves — their create path is a switch inside ArchiveOperations — so the
  /// CanCreate / CanModify capability flags count as evidence too.
  /// </summary>
  private bool OpenArchiveTakesAdditions() {
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps(_openFormat.ToString());
    var caps = (ops as Compression.Registry.IFormatDescriptor)?.Capabilities ?? 0;
    return ops is Compression.Registry.IArchiveCreatable or Compression.Registry.IArchiveModifiable
           || caps.HasFlag(Compression.Registry.FormatCapabilities.CanCreate)
           || caps.HasFlag(Compression.Registry.FormatCapabilities.CanModify);
  }

  // What the open archive is, as Open detected it from its content: a .img holding a partition
  // table is a partitioned disk, not the FAT image its extension suggests.
  private FormatDetector.Format _openFormat;

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
      _openFormat = format;
      Format = format.ToString();
      // Leave folder browsing before setting the folder: that setter rebuilds the breadcrumbs, and
      // while a host folder is still being browsed the trail ends at the host path.
      _osBrowserPath = null;
      CurrentFolder = "";
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
    RefreshVisibleEntriesCore();
    ReportLocation();
  }

  /// <summary>Records and announces an arrival, once per distinct place.</summary>
  private void ReportLocation() {
    if (_arrivalsSuppressed > 0) return;
    if (CurrentLocation is not { } now || now == _lastReported) return;

    _lastReported = now;
    _history.Visit(now);
    OnPropertyChanged(nameof(IsNestedArchive));
    LocationChanged?.Invoke(this, now);
    CommandManager.InvalidateRequerySuggested();
  }

  /// <summary>Runs a navigation that passes through other places, reporting only where it ends.</summary>
  private T AsOneArrival<T>(Func<T> navigation) {
    ++_arrivalsSuppressed;
    try {
      return navigation();
    } finally {
      --_arrivalsSuppressed;
      ReportLocation();
    }
  }

  /// <summary>
  /// Goes to <paramref name="target"/>, opening its archive first when it lies inside one.
  /// Returns false, and says why in the status line, when the place no longer exists.
  /// </summary>
  internal bool NavigateTo(Location target) => AsOneArrival(() => NavigateToCore(target));

  private bool NavigateToCore(Location target) {
    if (!target.IsInArchive) {
      if (!Directory.Exists(target.HostPath)) {
        StatusText = $"No longer exists: {target.HostPath}";
        return false;
      }

      _archiveStack.Clear();
      _osBrowserPath = target.HostPath;
      CurrentFolder = "";
      RefreshVisibleEntries();
      OnPropertyChanged(nameof(IsBrowsingOsFolder));
      return true;
    }

    if (!File.Exists(target.HostPath)) {
      StatusText = $"No longer exists: {target.HostPath}";
      return false;
    }

    // Already open and being browsed: only the folder changes, so the listing is not reread.
    var alreadyOpen = _osBrowserPath is null && HasArchive
      && string.Equals(ArchivePath, target.HostPath, StringComparison.OrdinalIgnoreCase);
    if (!alreadyOpen) {
      Open(target.HostPath);
      if (!string.Equals(ArchivePath, target.HostPath, StringComparison.OrdinalIgnoreCase)) return false;
    }

    var folder = Location.NormalizeArchiveFolder(target.ArchiveFolder ?? "");
    if (folder.Length > 0 && !_allEntries.Any(e => e.Path.StartsWith(folder, StringComparison.Ordinal))) {
      StatusText = $"No longer exists: {target}";
      RefreshVisibleEntries();
      return false;
    }

    CurrentFolder = target.ArchiveFolder ?? "";
    RefreshVisibleEntries();
    return true;
  }

  /// <summary>Goes wherever a typed address leads; see <see cref="AddressResolver"/>.</summary>
  internal bool NavigateToAddress(string typed) {
    if (AddressResolver.Resolve(typed) is { } target) return NavigateTo(target);

    StatusText = $"Not found: {typed.Trim()}";
    return false;
  }

  /// <summary>
  /// The immediate subfolders of <paramref name="folder"/> inside the open archive, including the
  /// ones that exist only because deeper entries sit under them - many archives record no folder
  /// entries at all.
  /// </summary>
  internal IReadOnlyList<string> ArchiveSubfolders(string folder) {
    var prefix = Location.NormalizeArchiveFolder(folder);
    var children = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var entry in _allEntries) {
      if (!entry.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;

      var remainder = entry.Path[prefix.Length..].TrimStart('/');
      var slash = remainder.IndexOf('/');
      if (slash > 0) children.Add(remainder[..slash]);
      else if (slash < 0 && entry.IsDirectory && remainder.Length > 0) children.Add(remainder);
    }

    return [.. children];
  }

  private void RefreshVisibleEntriesCore() {
    // One notification for the whole listing: a per-row notification had every listener - the file
    // list, the thumbnail decoder - start over once per entry.
    using var batch = Entries.Defer();
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
    var children = new List<ArchiveEntryViewModel>();

    foreach (var e in _allEntries) {
      if (!e.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;
      var remainder = e.Path[prefix.Length..];
      if (string.IsNullOrEmpty(remainder)) continue;

      var slashIdx = remainder.IndexOf('/');

      if (slashIdx < 0 && !e.IsDirectory) {
        // Direct file child
        if (seen.Add(remainder))
          children.Add(e);
      }
      else if (slashIdx < 0 && e.IsDirectory) {
        // Directory entry without trailing slash — treat as folder
        var key = remainder + "/";
        if (seen.Add(key))
          children.Add(e);
      }
      else if (slashIdx == remainder.Length - 1) {
        // Direct directory child (path ends with /)
        var key = remainder; // already has trailing /
        if (seen.Add(key))
          children.Add(e);
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
          children.Add(new ArchiveEntryViewModel {
            Name = subDir.TrimEnd('/'),
            Path = dirPrefix,
            OriginalSize = origSum,
            CompressedSize = hasComp ? compSum : -1,
            IsDirectory = true,
          });
        }
      }
    }

    // Listed as a folder on disk is: folders first, then files, each by name ignoring case - not in
    // whatever order the archive happens to store them. The sort is stable, so names equal but for
    // case keep their stored order.
    foreach (var child in children.OrderBy(c => !c.IsDirectory).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
      Entries.Add(child);
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
        var preview = new PreviewWindow();
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
      // Re-open the parent — fromNestedDescent: true so the stack survives — and land in the folder
      // the descent started from, as one step rather than via the parent's root.
      AsOneArrival(() => {
        Open(parentPath, fromNestedDescent: true);
        CurrentFolder = parentFolder;
        RefreshVisibleEntries();
        return true;
      });
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

    // Host folders first - for a folder being browsed, or the folder an archive lives in - each an
    // absolute path, so every crumb leads somewhere on every platform.
    var hostFolder = _osBrowserPath ?? (HasArchive && !IsNestedArchive ? Path.GetDirectoryName(ArchivePath) : null);
    if (!string.IsNullOrEmpty(hostFolder))
      foreach (var segment in HostPathSegments.Split(hostFolder))
        Breadcrumbs.Add(new BreadcrumbSegment { Label = segment.Label, FolderPath = segment.Path, Target = Location.Folder(segment.Path) });

    if (_osBrowserPath is not null || !HasArchive) return;

    Breadcrumbs.Add(new BreadcrumbSegment {
      Label = Path.GetFileName(ArchivePath), FolderPath = "", Target = Location.InArchive(ArchivePath, ""),
    });

    if (string.IsNullOrEmpty(CurrentFolder)) return;

    var accumulated = "";
    foreach (var part in CurrentFolder.TrimEnd('/').Split('/')) {
      accumulated += part + "/";
      Breadcrumbs.Add(new BreadcrumbSegment {
        Label = part, FolderPath = accumulated, Target = Location.InArchive(ArchivePath, accumulated),
      });
    }
  }

  private void OpenDialog() {
    var dlg = new OpenFileDialog {
      Title = "Open Archive",
      Filter = BuildOpenFilter(),
    };
    if (dlg.ShowDialog() == DialogResult.OK)
      Open(dlg.FileName);
  }

  private async Task ExtractAll() {
    var dlg = new FolderBrowserDialog { Title = "Select output folder" };
    if (dlg.ShowDialog() != DialogResult.OK) return;

    await RunAsync($"Extracting to {dlg.SelectedPath}...", () => {
      ArchiveOperations.Extract(ArchivePath, dlg.SelectedPath, password: null, files: null);
    });
    StatusText = $"Extracted to {dlg.SelectedPath}";
  }

  /// <summary>
  /// What a drag of <paramref name="selection"/> carries to other applications, or null when it can
  /// only move within the shell. Files on disk travel as their paths. Archive entries travel as
  /// <see cref="VirtualFile"/>s whose content is decoded only when the drop target asks for it, so
  /// nothing is extracted up front and the bytes are written straight to the destination — a folder
  /// travels with every entry beneath it.
  /// </summary>
  internal object? DragPayload(IReadOnlyList<ArchiveEntryViewModel> selection) {
    var picked = selection.Where(e => !e.IsParentEntry).ToList();
    if (picked.Count == 0) return null;

    if (IsBrowsingOsFolder) {
      string[] paths = [.. picked.Select(e => e.Path).Where(p => File.Exists(p) || Directory.Exists(p))];
      return paths.Length > 0 ? paths : null;
    }

    if (!HasArchive || picked.Any(e => e.IsEncrypted)) return null;

    var archive = ArchivePath;
    var files = new List<VirtualFile>();
    foreach (var entry in picked) {
      if (!entry.IsDirectory) {
        files.Add(Lazy(entry.Name, entry));
        continue;
      }

      files.Add(VirtualFile.Directory(entry.Name));
      var prefix = entry.Path.EndsWith('/') ? entry.Path : entry.Path + "/";
      foreach (var inner in _allEntries.Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal) && e.Path.Length > prefix.Length)) {
        var relative = entry.Name + "/" + inner.Path[prefix.Length..].TrimEnd('/');
        files.Add(inner.IsDirectory ? VirtualFile.Directory(relative) : Lazy(relative, inner));
      }
    }

    return files.Count > 0 ? files.ToArray() : null;

    VirtualFile Lazy(string relative, ArchiveEntryViewModel entry)
      => new(relative, () => ArchiveOperations.OpenEntry(archive, entry.Path), entry.OriginalSize >= 0 ? entry.OriginalSize : null,
        entry.LastModified?.ToUniversalTime());
  }

  /// <summary>Why dropped <paramref name="files"/> cannot be received into <paramref name="target"/>, or null.</summary>
  internal string? WhyNotReceive(IReadOnlyList<IncomingFile> files, Location target)
    => IsNestedCopy(target) ? "Nothing can be put inside a nested archive." : Transfer.WhyNot(files, target);

  /// <summary>
  /// Receives files another application dropped with no path of their own — mail attachments and
  /// the like — into <paramref name="target"/>. Runs while the drop is being handled, because the
  /// content can only be read then. Returns the reason on failure.
  /// </summary>
  internal string? ReceiveDropped(IReadOnlyList<IncomingFile> files, Location target) {
    if (WhyNotReceive(files, target) is { } refusal) return Fail(refusal);
    if (BeginChange(TimeSpan.FromSeconds(2)) is { } busy) return Fail(busy);

    try {
      IsBusy = true;
      var result = Transfer.Receive(files, target);
      if (HasArchive && !IsBrowsingOsFolder) ReloadArchiveInPlace();
      else RefreshVisibleEntries();

      StatusText = $"Received {result.Created.Count} item(s) into {target}.";
      RaiseChanged([target]);
      return null;
    } catch (Exception ex) {
      return Fail($"Receiving the drop failed: {ex.Message}");
    } finally {
      IsBusy = false;
      EndChange();
    }

    string Fail(string reason) {
      StatusText = reason;
      return reason;
    }
  }

  private async Task ExtractSelected() {
    var dlg = new FolderBrowserDialog { Title = "Select output folder" };
    if (dlg.ShowDialog() != DialogResult.OK) return;

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
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);
    } else {
      StatusText = $"Integrity test: FAILED ({elapsed}ms)";
      var msg = $"Integrity test FAILED.\n\nArchive: {System.IO.Path.GetFileName(ArchivePath)}";
      if (errorDetail != null)
        msg += $"\n\nError: {errorDetail}";
      MessageBox.Show(msg, "Integrity Test",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
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

      var preview = new PreviewWindow();
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
      var archiveDlg = new PropertiesWindow();
      archiveDlg.ShowProperties(archiveEntry, _allEntries, archiveData);
      archiveDlg.ShowDialog(this.Owner);
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

    var dlg = new PropertiesWindow();
    dlg.ShowProperties(entry, _allEntries, data);
    dlg.ShowDialog(this.Owner);
  }

  internal void ShowBenchmark() {
    var win = new BenchmarkWindow();
    win.Show();
  }

  internal void ShowFileAssociations() {
    var win = new FileAssociationsWindow();
    win.ShowDialog(this.Owner);
  }

  internal void ShowAnalyzeFile() {
    var win = new AnalysisWindow();
    win.Show();
  }

  internal void ShowAnalysis() {
    var entry = SelectedEntries.FirstOrDefault(e => !e.IsDirectory && !e.IsParentEntry);
    if (entry == null) return;

    try {
      StatusText = $"Loading {entry.Name}...";
      var data = ArchiveOperations.ExtractEntry(ArchivePath, entry.Path, password: null);
      StatusText = "Ready";

      var win = new AnalysisWindow();
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

    var format = _openFormat;
    if (format == FormatDetector.Format.Unknown) return (true, "Drop to add files to archive");

    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());
    if (!OpenArchiveTakesAdditions())
      return (false, "This archive format is read-only (can't add files)");

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
    var dlg = new OpenFileDialog {
      Title = "Add files to archive",
      Multiselect = true,
      Filter = "All Files|*.*",
    };
    if (dlg.ShowDialog() != DialogResult.OK) return;
    AddFilesToArchiveImpl(dlg.FileNames);
  }

  private void AddFilesToArchiveImpl(string[] paths) {
    var format = _openFormat;
    if (format == FormatDetector.Format.Unknown || FormatDetector.IsStreamFormat(format)) {
      MessageBox.Show("Cannot add files to this archive format.", "Add Files",
        MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
      var optsDlg = new CreateOptionsWindow(format);
      optsDlg.Text = "Add Files — Compression Options";
      if (optsDlg.ShowDialog(this.Owner) != DialogResult.OK) return;
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
        "Add Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

      var dlg = new ReplaceConfirmationWindow(Path.GetFileName(archivePath), entryName);
      dlg.ShowDialog(this.Owner);
      switch (dlg.Decision) {
        case ReplaceDecision.Yes:      pathsToReplace.Add(p); break;
        case ReplaceDecision.YesToAll: pathsToReplace.Add(p); yesAll = true; break;
        case ReplaceDecision.Skip:     break;
        case ReplaceDecision.SkipAll:  skipAll = true; break;
        case ReplaceDecision.Cancel:   StatusText = "Add cancelled."; return;
      }
    }

    var totalChanges = pathsToAdd.Count + pathsToReplace.Count;
    if (totalChanges == 0) { StatusText = "Nothing to add (all collisions skipped)."; return; }

    Task.Run(() => {
      this.Marshal(() => {
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
        this.Marshal(() => {
          StatusText = $"Added {totalChanges} item(s) ({sw.ElapsedMilliseconds}ms)";
          Open(archivePath);
        });
      }
      catch (Exception ex) {
        this.Marshal(() => {
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
    var dlg = new SaveFileDialog {
      Title = "Create Archive",
      Filter = filter,
    };
    if (dlg.ShowDialog() != DialogResult.OK) return;

    // FilterIndex is 1-based and tells us which item in the dropdown the user
    // chose. Trust that over the file extension because many extensions (.img,
    // .bin, .dd) are claimed by multiple filesystems — the user's drop-down
    // selection is the authoritative answer.
    var idx = dlg.FilterIndex - 1;
    var format = idx >= 0 && idx < formats.Count
      ? formats[idx]
      : FormatDetector.DetectByExtensionForCreate(dlg.FileName);

    // Show options dialog
    var optsDlg = new CreateOptionsWindow(format);
    if (optsDlg.ShowDialog(this.Owner) != DialogResult.OK) return;

    // Pick input files/folder
    var inputDlg = new FolderBrowserDialog { Title = "Select folder to archive" };
    if (inputDlg.ShowDialog() != DialogResult.OK) return;

    var inputs = ArchiveInput.Resolve([inputDlg.SelectedPath]);
    var opts = optsDlg.Options.ToOptions();
    var makeSfx = optsDlg.Options.MakeSfx;
    var sfxStubType = optsDlg.Options.SfxIsGui ? SfxBuilder.StubType.Ui : SfxBuilder.StubType.Cli;
    var sfxTargetRid = optsDlg.Options.ResolvedSfxTargetRid;
    var outputPath = dlg.FileName;

    Task.Run(() => {
      this.Marshal(() => {
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

      this.Marshal(() => {
        IsBusy = false;
        if (err != null) {
          StatusText = $"Create failed: {err.Message}";
          MessageBox.Show(
            $"Failed to create {Path.GetFileName(outputPath)}:\n\n{err.GetType().Name}: {err.Message}",
            "Create Archive", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
  /// <summary>Where clicking this crumb goes.</summary>
  public Location? Target { get; init; }
}
