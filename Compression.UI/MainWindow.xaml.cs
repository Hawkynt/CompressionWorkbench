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
