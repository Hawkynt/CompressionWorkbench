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

  private void OnDragOver(object sender, DragEventArgs e) {
    if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
      e.Effects = DragDropEffects.Copy;
      DropOverlay.Visibility = Visibility.Visible;
      DropText.Text = ViewModel.HasArchive ? "Drop to add files to archive" : "Drop archive to open";
    }
    else {
      e.Effects = DragDropEffects.None;
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
    if (EntryList.SelectedItem is not ArchiveEntryViewModel entry) return;
    if (entry.IsParentEntry || entry.IsDirectory)
      ViewModel.NavigateIntoCommand.Execute(entry);
    else
      ViewModel.ViewSelectedAs(hex: false);
  }

  private void OnBreadcrumbClick(object sender, RoutedEventArgs e) {
    if (sender is System.Windows.Controls.Button btn && btn.Tag is string path)
      ViewModel.NavigateToBreadcrumbCommand.Execute(path);
  }

  private void OnExit(object sender, RoutedEventArgs e) => Close();

  private void OnAbout(object sender, RoutedEventArgs e) {
    MessageBox.Show(
      "CompressionWorkbench\n\nA universal archive tool supporting 50+ formats.\n\nAll compression algorithms implemented from scratch in C#.",
      "About CompressionWorkbench",
      MessageBoxButton.OK, MessageBoxImage.Information);
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
