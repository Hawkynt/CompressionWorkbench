using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Compression.UI.ViewModels;

namespace Compression.UI;

public partial class MainWindow : Window {
  private MainViewModel ViewModel => (MainViewModel)DataContext;

  public MainWindow() {
    InitializeComponent();

    // Sync ListView selection to ViewModel
    EntryList.SelectionChanged += (_, _) => {
      ViewModel.SelectedEntries.Clear();
      foreach (ArchiveEntryViewModel item in EntryList.SelectedItems)
        ViewModel.SelectedEntries.Add(item);
    };
  }

  // ── First-popup latency mitigation ─────────────────────────────────────
  // WPF defers materialization of a ContextMenu's visual tree until the menu
  // is first opened — so the FIRST right-click on the EntryList paid for
  // JIT-ing 10 MenuItems × DrawingImage icons + first-time CanExecute calls
  // that route through FormatRegistration.EnsureInitialized() (~180
  // descriptors). Subsequent popups were instant because the tree + caches
  // were warm. We pre-warm both:
  //   1. App.OnStartup kicks FormatRegistration.EnsureInitialized() on a
  //      background thread (see App.xaml.cs).
  //   2. Window Loaded forces the ContextMenu to materialize off the
  //      user-visible critical path by opening + immediately closing it at
  //      ApplicationIdle priority.
  // The first real right-click then hits an already-built visual tree and
  // a warm registry.
  private bool _contextMenuPrewarmed;

  private void OnWindowLoaded(object sender, RoutedEventArgs e) {
    if (_contextMenuPrewarmed) return;
    _contextMenuPrewarmed = true;

    Dispatcher.BeginInvoke(new Action(() => {
      // Touch each icon resource so its DrawingImage is materialized on the UI
      // thread before the popup needs it. TryFindResource walks the merged-
      // dictionary tree; first hit is the expensive one.
      foreach (var key in new[] {
        "ViewTextIcon", "ViewHexIcon", "ViewImageIcon",
        "ExtractSelectedIcon", "ExtractIcon", "AddIcon", "RemoveIcon",
        "DefragmentIcon", "AnalyzeIcon", "PropertiesIcon",
      })
        _ = TryFindResource(key);

      // Materialize the ContextMenu visual tree. Opening + closing within the
      // same dispatcher frame makes the popup invisible to the user but forces
      // WPF to build the popup HWND, JIT the 10 MenuItem templates, and run
      // each command's first CanExecute. Wrapped in try so any binding hiccup
      // doesn't crash the window.
      try {
        if (EntryList?.ContextMenu is { } cm) {
          cm.PlacementTarget = EntryList;
          cm.IsOpen = true;
          cm.IsOpen = false;
        }
      } catch { /* best effort — pre-warm is opportunistic */ }
    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
  }

  // Diagnostic: time the first right-click to validate the pre-warm worked.
  // Only logs in Debug builds — release users never see this overhead.
  [System.Diagnostics.Conditional("DEBUG")]
  private void LogFirstContextMenuOpen() {
    if (_firstContextMenuLogged) return;
    _firstContextMenuLogged = true;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    Dispatcher.BeginInvoke(new Action(() => {
      System.Diagnostics.Debug.WriteLine(
        $"[ContextMenu] first user-triggered popup ready in {sw.ElapsedMilliseconds} ms");
    }), System.Windows.Threading.DispatcherPriority.ContextIdle);
  }

  private bool _firstContextMenuLogged;

  private void OnEntryContextMenuOpening(object sender, ContextMenuEventArgs e) {
    LogFirstContextMenuOpen();
  }

  public void OpenArchive(string path) => ViewModel.Open(path);

  public void StartInOsBrowserAtLastFolder() => ViewModel.StartInOsBrowserAtLastFolder();

  private void OnDragOver(object sender, DragEventArgs e) {
    if (!e.Data.GetDataPresent(DataFormats.FileDrop)) {
      e.Effects = DragDropEffects.None;
      e.Handled = true;
      return;
    }

    DropOverlay.Visibility = Visibility.Visible;

    // No archive open: any drop opens the first file as an archive.
    if (!ViewModel.HasArchive) {
      e.Effects = DragDropEffects.Copy;
      DropText.Text = "Drop archive to open";
      e.Handled = true;
      return;
    }

    // Archive open: decide by capability + constraints.
    var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
    var (allowed, message) = ViewModel.EvaluateDropAgainstCurrentArchive(files);
    if (allowed) {
      e.Effects = DragDropEffects.Copy;
      DropText.Text = message ?? "Drop to add files to archive";
    } else {
      e.Effects = DragDropEffects.None;
      DropText.Text = message ?? "This archive doesn't accept those inputs";
    }
    e.Handled = true;
  }

  private void OnDragLeave(object sender, DragEventArgs e) {
    DropOverlay.Visibility = Visibility.Collapsed;
  }

  private void OnDrop(object sender, DragEventArgs e) {
    DropOverlay.Visibility = Visibility.Collapsed;
    if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
      var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
      if (files.Length > 0)
        ViewModel.HandleFileDrop(files);
    }
  }

  private void OnEntryDoubleClick(object sender, MouseButtonEventArgs e) {
    ActivateSelectedEntry();
  }

  // ── Drag-out: let users drag entries from the archive list to Explorer / any drop target.
  // WPF drag starts only once the pointer has moved past SystemParameters.MinimumHorizontal/
  // VerticalDragDistance from the initial mouse-down position; we record that point here and
  // compare in OnEntryMouseMove so single-click selection doesn't trigger a drag.
  private System.Windows.Point? _dragStart;

  private void OnEntryMouseDown(object sender, MouseButtonEventArgs e) {
    // Record the origin only when the click landed on an actual row (not the header or empty area).
    var hit = e.OriginalSource as DependencyObject;
    while (hit != null && hit is not System.Windows.Controls.ListViewItem)
      hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
    this._dragStart = hit is System.Windows.Controls.ListViewItem ? e.GetPosition(null) : null;
  }

  private void OnEntryMouseMove(object sender, System.Windows.Input.MouseEventArgs e) {
    if (this._dragStart == null || e.LeftButton != MouseButtonState.Pressed) return;

    var diff = e.GetPosition(null) - this._dragStart.Value;
    if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
        Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
      return;

    this._dragStart = null;  // consume — a single drag per gesture
    this.StartDragOutOfArchive();
  }

  private void StartDragOutOfArchive() {
    if (!ViewModel.HasArchive) return;
    var selectedEntries = ViewModel.SelectedEntries
      .Where(e => !e.IsParentEntry)
      .ToList();
    if (selectedEntries.Count == 0) return;

    // Drag-out needs concrete file paths to feed DataFormats.FileDrop. Materialise the
    // selection into a per-session temp dir the user can drop anywhere in Explorer. The
    // temp dir self-cleans on the next successful Save / archive-close, or on process
    // exit; files the drop target moved have already left, so no orphaned copies remain.
    string[] paths;
    try {
      paths = ViewModel.MaterializeForDragOut(selectedEntries);
    } catch (Exception ex) {
      System.Windows.MessageBox.Show(
        $"Couldn't prepare files for drag-out:\n{ex.Message}",
        "Drag-out", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }
    if (paths.Length == 0) return;

    var data = new System.Windows.DataObject(DataFormats.FileDrop, paths);
    DragDrop.DoDragDrop(EntryList, data, DragDropEffects.Copy);
  }

  private void OnEntryKeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
    if (e.Key == Key.Enter && e.KeyboardDevice.Modifiers == ModifierKeys.None) {
      ActivateSelectedEntry();
      e.Handled = true;
    }
  }

  private void ActivateSelectedEntry() {
    if (EntryList.SelectedItem is not ArchiveEntryViewModel entry) return;
    if (entry.IsParentEntry || entry.IsDirectory) {
      ViewModel.NavigateIntoCommand.Execute(entry);
      return;
    }

    // OS-browser mode: file double-click should match the File → Open flow —
    // delegate to NavigateInto, which detects format and either Open()s the
    // file as archive (showing colorspace tree etc.) or falls back to byte
    // preview when the format is unknown. Without this branch ViewSelectedAs
    // would call File.ReadAllBytes on a multi-MB JPEG and route it through
    // the preview window, freezing the UI.
    if (ViewModel.IsBrowsingOsFolder) {
      ViewModel.NavigateIntoCommand.Execute(entry);
      return;
    }

    // Inside-archive entry: extract + preview (handles nested-archive descent
    // for non-image formats and visual rendering for known image bytes).
    ViewModel.ViewSelectedAs(hex: false);
  }

  private void OnBreadcrumbClick(object sender, RoutedEventArgs e) {
    if (sender is System.Windows.Controls.Button btn && btn.Tag is string path)
      ViewModel.NavigateToBreadcrumbCommand.Execute(path);
  }

  private void OnExit(object sender, RoutedEventArgs e) => Close();

  private void OnReverseEngineer(object sender, RoutedEventArgs e) {
    var wizard = new Views.ReverseEngineerWindow { Owner = this };
    wizard.Show();
  }

  private void OnDefragment(object sender, RoutedEventArgs e) {
    var preselected = ViewModel.HasArchive ? ViewModel.ArchivePath : null;
    var dlg = preselected != null
      ? new Views.DefragmentWindow(preselected) { Owner = this }
      : new Views.DefragmentWindow { Owner = this };
    // Re-list the explorer whenever the defragment window mutates the
    // archive we currently have open (defrag, shrink, wipe-empty, optimize).
    dlg.ArchiveMutated += path => {
      if (ViewModel.HasArchive
          && string.Equals(path, ViewModel.ArchivePath, StringComparison.OrdinalIgnoreCase))
        ViewModel.Open(ViewModel.ArchivePath);
    };
    dlg.Show();
  }

  private void OnPartitionEditor(object sender, RoutedEventArgs e) {
    // If an archive is already open and it's a partitionable image, pre-load it.
    // Otherwise the window's Open... button (or the file dialog we show below)
    // lets the user pick a file.
    var preselected = ViewModel.HasArchive ? ViewModel.ArchivePath : null;
    if (preselected != null && System.IO.File.Exists(preselected)) {
      var dlg = new Views.PartitionsWindow(preselected) { Owner = this };
      dlg.Show();
      return;
    }

    var openDlg = new Microsoft.Win32.OpenFileDialog {
      Title = "Open disk image / virtual disk",
      Filter = "Disk images & virtual disks|*.img;*.iso;*.bin;*.vhd;*.vhdx;*.vmdk;*.qcow2;*.qcow;*.vdi"
             + "|All files|*.*",
    };
    if (openDlg.ShowDialog(this) == true) {
      var window = new Views.PartitionsWindow(openDlg.FileName) { Owner = this };
      window.Show();
    } else {
      // User cancelled the file dialog — still show the empty window so they
      // can use the in-window Open... toolbar button.
      var window = new Views.PartitionsWindow { Owner = this };
      window.Show();
    }
  }

  private void OnConvertArchive(object sender, RoutedEventArgs e) {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    // Source = the currently open archive from the view model.
    var sourcePath = ViewModel.ArchivePath;
    if (string.IsNullOrEmpty(sourcePath) || !System.IO.File.Exists(sourcePath)) {
      MessageBox.Show(this, "Open an archive or image first, then use Convert Archive to write it out in another format.",
        "Convert Archive", MessageBoxButton.OK, MessageBoxImage.Information);
      return;
    }

    // Build a SaveFileDialog filter listing every IArchiveCreatable descriptor,
    // grouped by FormatCategory (Archive | Stream | Audio | Video | ...).
    var creatable = Compression.Registry.FormatRegistry.All
      .Where(d => d is Compression.Registry.IArchiveCreatable)
      .OrderBy(d => d.Category.ToString())
      .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
      .ToList();

    if (creatable.Count == 0) {
      MessageBox.Show(this, "No creatable formats are registered.",
        "Convert Archive", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    // Filter format: "Name (*.ext;*.ext2)|*.ext;*.ext2|...|All files (*.*)|*.*"
    var parts = new List<string>();
    var orderedDescriptors = new List<Compression.Registry.IFormatDescriptor>();
    foreach (var group in creatable.GroupBy(d => d.Category)) {
      foreach (var d in group) {
        var exts = d.Extensions.Count > 0
          ? string.Join(";", d.Extensions.Select(x => "*" + x))
          : "*" + d.DefaultExtension;
        var label = $"{group.Key}: {d.DisplayName} ({exts})";
        parts.Add($"{label}|{exts}");
        orderedDescriptors.Add(d);
      }
    }
    parts.Add("All files (*.*)|*.*");

    var saveDlg = new Microsoft.Win32.SaveFileDialog {
      Title = "Convert archive to...",
      Filter = string.Join("|", parts),
      FileName = System.IO.Path.GetFileNameWithoutExtension(sourcePath) + "_converted",
    };
    if (saveDlg.ShowDialog(this) != true) return;

    // FilterIndex is 1-based; the last slot is "All files" which falls back
    // to extension-based detection. Otherwise we have an explicit pick.
    string? targetFormatId = null;
    var idx = saveDlg.FilterIndex - 1;
    if (idx >= 0 && idx < orderedDescriptors.Count)
      targetFormatId = orderedDescriptors[idx].Id;

    // If the target descriptor publishes a tunable schema, prompt the user
    // for those knobs before we kick off the (potentially long) conversion.
    // Cancelling the schema dialog aborts the conversion silently — no
    // popup, just like cancelling the SaveFileDialog above.
    Compression.Registry.FormatCreateOptions? createOptions = null;
    if (!string.IsNullOrEmpty(targetFormatId)) {
      var dstDescriptor = Compression.Registry.FormatRegistry.GetById(targetFormatId);
      if (dstDescriptor is Compression.Registry.IFormatOptionsSchema schemaSrc
          && schemaSrc.OptionsSchema is { Count: > 0 } schema) {
        var optDlg = new Views.TargetOptionsDialog(schema, dstDescriptor.DisplayName) { Owner = this };
        if (optDlg.ShowDialog() != true) return;
        createOptions = new Compression.Registry.FormatCreateOptions { FormatSpecific = optDlg.Result };
      }
    }

    try {
      var warnings = Compression.Lib.ArchiveOperations.ConvertArchive(
        sourcePath, saveDlg.FileName, targetFormatId, createOptions);
      var msg = $"Conversion complete.\nOutput: {saveDlg.FileName}";
      if (warnings.Count > 0)
        msg += "\n\nWarnings:\n" + string.Join("\n", warnings);
      MessageBox.Show(this, msg, "Convert Archive", MessageBoxButton.OK, MessageBoxImage.Information);
    } catch (Exception ex) {
      MessageBox.Show(this, $"Conversion failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
  }

  private void OnAbout(object sender, RoutedEventArgs e) {
    var about = new Views.AboutWindow { Owner = this };
    about.ShowDialog();
  }

  // Column sorting
  private GridViewColumnHeader? _lastHeaderClicked;
  private ListSortDirection _lastDirection = ListSortDirection.Ascending;

  private void OnColumnHeaderClick(object sender, RoutedEventArgs e) {
    if (e.OriginalSource is not GridViewColumnHeader header || header.Role == GridViewColumnHeaderRole.Padding)
      return;

    var direction = header == _lastHeaderClicked && _lastDirection == ListSortDirection.Ascending
      ? ListSortDirection.Descending
      : ListSortDirection.Ascending;

    var sortBy = header.Column.DisplayMemberBinding is System.Windows.Data.Binding binding
      ? binding.Path.Path
      : header.Column.Header?.ToString() ?? "";

    if (string.IsNullOrEmpty(sortBy)) return;

    var view = System.Windows.Data.CollectionViewSource.GetDefaultView(EntryList.ItemsSource);
    view.SortDescriptions.Clear();
    view.SortDescriptions.Add(new SortDescription(sortBy, direction));
    view.Refresh();

    _lastHeaderClicked = header;
    _lastDirection = direction;
  }
}
