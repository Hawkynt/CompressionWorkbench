using System.Windows;
using Compression.Core.DiskImage;

namespace Compression.UI.Views;

/// <summary>
/// Modal dialog used by <see cref="PartitionsWindow"/> to gather the four
/// parameters needed to create a new primary or logical partition: start
/// sector, length in sectors, partition type, and label.
/// </summary>
public partial class AddPartitionDialog : Window {

  private readonly long _diskBytes;

  /// <summary>Start of the new partition in bytes (start LBA × 512).</summary>
  public long StartOffsetBytes { get; private set; }

  /// <summary>Length of the new partition in bytes (length sectors × 512).</summary>
  public long LengthBytes { get; private set; }

  /// <summary>The user-picked partition type.</summary>
  public PartitionType SelectedType { get; private set; }

  /// <summary>Optional partition label (GPT only; MBR ignores).</summary>
  public string? Label { get; private set; }

  public AddPartitionDialog(long diskBytes, bool isLogical) {
    InitializeComponent();
    this._diskBytes = diskBytes;

    HeadingLbl.Text = isLogical
      ? "Add a new logical partition (inside the MBR extended container)."
      : "Add a new primary/GPT partition.";

    // Populate the type dropdown.
    foreach (var t in Enum.GetValues<PartitionType>()) {
      // ExtendedLba only makes sense for non-logicals (you can't put an
      // extended container inside an extended container).
      if (isLogical && t == PartitionType.ExtendedLba) continue;
      TypeCombo.Items.Add(t);
    }
    TypeCombo.SelectedItem = isLogical ? PartitionType.Linux : PartitionType.Linux;

    // Default suggestion: start at sector 2048 (1 MiB) leaving room for MBR,
    // length = first 64 MiB or whole-disk-minus-1 MiB if smaller.
    const int defaultStartLba = 2048;
    var totalLba = Math.Max(0, diskBytes / PartitionEditor.SectorSize);
    var suggestedLengthLba = Math.Max(1, Math.Min(64L * 1024 * 1024 / PartitionEditor.SectorSize,
                                                  Math.Max(1, totalLba - defaultStartLba)));
    StartLbaBox.Text = defaultStartLba.ToString();
    LengthSectorsBox.Text = suggestedLengthLba.ToString();

    HintLbl.Text = $"Disk size: {diskBytes:N0} bytes ({totalLba:N0} sectors of 512 B).";
  }

  private void OnOk(object sender, RoutedEventArgs e) {
    if (!long.TryParse(StartLbaBox.Text.Trim(), out var startLba) || startLba < 0) {
      MessageBox.Show(this, "Start sector must be a non-negative integer.", "Invalid input",
        MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }
    if (!long.TryParse(LengthSectorsBox.Text.Trim(), out var lengthLba) || lengthLba <= 0) {
      MessageBox.Show(this, "Length must be a positive integer (sectors).", "Invalid input",
        MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }
    if (TypeCombo.SelectedItem is not PartitionType type) {
      MessageBox.Show(this, "Pick a partition type.", "Invalid input",
        MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    var startBytes = startLba * PartitionEditor.SectorSize;
    var lengthBytes = lengthLba * PartitionEditor.SectorSize;
    if (startBytes + lengthBytes > this._diskBytes) {
      MessageBox.Show(this,
        $"Partition end ({startBytes + lengthBytes:N0}) exceeds disk size ({this._diskBytes:N0}).",
        "Out of range", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    this.StartOffsetBytes = startBytes;
    this.LengthBytes = lengthBytes;
    this.SelectedType = type;
    this.Label = string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim();
    DialogResult = true;
    Close();
  }
}
