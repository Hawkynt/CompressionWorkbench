using System.Windows;
using Compression.Registry;

namespace Compression.UI.Views;

/// <summary>
/// Modal dialog used by <see cref="PartitionsWindow"/> to pick a filesystem
/// format to write into a partition's byte range. Lists only descriptors that
/// implement <see cref="IArchiveCreatable"/> (i.e. can be created from
/// scratch).
/// </summary>
public partial class FormatPartitionDialog : Window {

  /// <summary>The chosen format ID (e.g. "Fat", "Ext"), or null if cancelled.</summary>
  public string? SelectedFormatId { get; private set; }

  public FormatPartitionDialog(int partitionIndex, string partitionSizeDisplay) {
    InitializeComponent();
    HeadingLbl.Text = $"Write a fresh filesystem image into partition #{partitionIndex} ({partitionSizeDisplay}).";

    // Enumerate creatable formats from the registry. We rely on
    // FormatRegistry.All being populated by FormatRegistration.EnsureInitialized()
    // (already called by PartitionsWindow.LoadImage before this dialog opens).
    var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var desc in FormatRegistry.All) {
      if (desc is IArchiveCreatable && desc is IArchiveFormatOperations) {
        // We deliberately list everything that's creatable; we don't try to
        // narrow it down to "filesystem-y" formats by category because the
        // category enum doesn't model that cleanly. The user is responsible
        // for picking something sensible.
        ids.Add(desc.Id);
      }
    }

    foreach (var id in ids)
      FsCombo.Items.Add(id);

    if (FsCombo.Items.Count > 0) {
      // Default to FAT if available, otherwise the first entry.
      var fatIdx = FsCombo.Items.IndexOf("Fat");
      FsCombo.SelectedIndex = fatIdx >= 0 ? fatIdx : 0;
    }
  }

  private void OnOk(object sender, RoutedEventArgs e) {
    if (FsCombo.SelectedItem is not string id || string.IsNullOrEmpty(id)) {
      MessageBox.Show(this, "Pick a filesystem format.", "Invalid input",
        MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }
    this.SelectedFormatId = id;
    DialogResult = true;
    Close();
  }
}
