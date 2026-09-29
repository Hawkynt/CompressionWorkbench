using System.Drawing;
using Compression.Core.DiskImage;
using Compression.Registry;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Views;

/// <summary>
/// Gathers the four parameters a new primary or logical partition needs: start sector, length in
/// sectors, partition type and label.
/// </summary>
internal sealed class AddPartitionDialog : Form {
  private const int LabelWidth = 120;

  private readonly long _diskBytes;
  private readonly Label _heading = new();
  private readonly TextBox _startLba = new();
  private readonly TextBox _lengthSectors = new();
  private readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly TextBox _label = new();
  private readonly Label _hint = new() { ForeColor = Color.DimGray };
  private readonly Button _ok = new() { Text = "OK", DialogResult = DialogResult.OK };
  private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
  private readonly ToolTip _toolTips = new();

  public AddPartitionDialog(long diskBytes, bool isLogical) {
    this._diskBytes = diskBytes;

    this.Text = "Add Partition";
    this.ClientSize = new(460, 340);
    this.StartPosition = FormStartPosition.CenterParent;
    this.FormBorderStyle = FormBorderStyle.FixedDialog;
    this.MaximizeBox = false;
    this.MinimizeBox = false;

    this._heading.Font = new(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold);
    this._heading.Text = isLogical
      ? "Add a new logical partition (inside the MBR extended container)."
      : "Add a new primary/GPT partition.";

    // An extended container cannot sit inside an extended container.
    foreach (var t in Enum.GetValues<PartitionType>()) {
      if (isLogical && t == PartitionType.ExtendedLba) continue;
      this._type.Items.Add(t);
    }
    this._type.SelectedItem = PartitionType.Linux;

    // Start at sector 2048 (one mebibyte in), leaving room for the MBR, and suggest either the
    // first 64 MiB or whatever is left if the disk is smaller.
    const int DefaultStartLba = 2048;
    var totalLba = Math.Max(0, diskBytes / PartitionEditor.SectorSize);
    var suggestedLength = Math.Max(1, Math.Min(64L * 1024 * 1024 / PartitionEditor.SectorSize, Math.Max(1, totalLba - DefaultStartLba)));

    this._startLba.Text = DefaultStartLba.ToString();
    this._lengthSectors.Text = suggestedLength.ToString();
    this._hint.Text = $"Disk size: {diskBytes:N0} bytes ({totalLba:N0} sectors of 512 B).";

    this._toolTips.SetToolTip(this._startLba, "Sector index from the start of the disk. Each sector is 512 bytes.");
    this._toolTips.SetToolTip(this._lengthSectors, "Number of 512-byte sectors. Multiply by 512 to get bytes.");

    this._ok.Click += (_, _) => this.OnOk();
    this._cancel.Click += (_, _) => this.Close();
    this.AcceptButton = this._ok;
    this.CancelButton = this._cancel;

    this.Controls.AddRange(
      this._heading,
      new Label { Text = "Start sector (LBA):", Bounds = new(12, 50, LabelWidth, 20) }, this._startLba,
      new Label { Text = "Length (sectors):", Bounds = new(12, 82, LabelWidth, 20) }, this._lengthSectors,
      new Label { Text = "Type:", Bounds = new(12, 114, LabelWidth, 20) }, this._type,
      new Label { Text = "Label:", Bounds = new(12, 146, LabelWidth, 20) }, this._label,
      this._hint, this._ok, this._cancel);

    this._heading.Bounds = new(12, 12, 436, 32);
    this._startLba.Bounds = new(12 + LabelWidth, 48, 300, 24);
    this._lengthSectors.Bounds = new(12 + LabelWidth, 80, 300, 24);
    this._type.Bounds = new(12 + LabelWidth, 112, 300, 24);
    this._label.Bounds = new(12 + LabelWidth, 144, 300, 24);
    this._hint.Bounds = new(12, 182, 436, 34);
    this._ok.Bounds = new(268, 296, 80, 28);
    this._cancel.Bounds = new(356, 296, 80, 28);
  }

  /// <summary>Start of the new partition in bytes.</summary>
  public long StartOffsetBytes { get; private set; }

  /// <summary>Length of the new partition in bytes.</summary>
  public long LengthBytes { get; private set; }

  /// <summary>The chosen partition type.</summary>
  public PartitionType SelectedType { get; private set; }

  /// <summary>Optional label; GPT records it, MBR ignores it.</summary>
  public string? Label { get; private set; }

  private void OnOk() {
    if (!long.TryParse(this._startLba.Text.Trim(), out var startLba) || startLba < 0) {
      Warn("Start sector must be a non-negative integer.");
      return;
    }

    if (!long.TryParse(this._lengthSectors.Text.Trim(), out var lengthLba) || lengthLba <= 0) {
      Warn("Length must be a positive integer (sectors).");
      return;
    }

    if (this._type.SelectedItem is not PartitionType type) {
      Warn("Pick a partition type.");
      return;
    }

    var startBytes = startLba * PartitionEditor.SectorSize;
    var lengthBytes = lengthLba * PartitionEditor.SectorSize;
    if (startBytes + lengthBytes > this._diskBytes) {
      MessageBox.Show(this,
        $"Partition end ({startBytes + lengthBytes:N0}) exceeds disk size ({this._diskBytes:N0}).",
        "Out of range", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return;
    }

    this.StartOffsetBytes = startBytes;
    this.LengthBytes = lengthBytes;
    this.SelectedType = type;
    this.Label = string.IsNullOrWhiteSpace(this._label.Text) ? null : this._label.Text.Trim();
    this.DialogResult = DialogResult.OK;
    this.Close();

    void Warn(string message)
      => MessageBox.Show(this, message, "Invalid input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
  }
}

/// <summary>
/// Picks the filesystem to write into a partition's byte range. Lists every descriptor that can be
/// created from scratch.
/// </summary>
internal sealed class FormatPartitionDialog : Form {
  private readonly Label _heading = new();
  private readonly ComboBox _filesystem = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly Button _ok = new() { Text = "OK", DialogResult = DialogResult.OK };
  private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };

  public FormatPartitionDialog(int partitionIndex, string partitionSizeDisplay) {
    this.Text = "Format Partition";
    this.ClientSize = new(460, 180);
    this.StartPosition = FormStartPosition.CenterParent;
    this.FormBorderStyle = FormBorderStyle.FixedDialog;
    this.MaximizeBox = false;
    this.MinimizeBox = false;

    this._heading.Font = new(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold);
    this._heading.Text = $"Write a fresh filesystem image into partition #{partitionIndex} ({partitionSizeDisplay}).";

    // Everything creatable is offered. The category enum does not model "filesystem-like" cleanly,
    // so narrowing the list here would guess on the user's behalf.
    var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var descriptor in FormatRegistry.All)
      if (descriptor is IArchiveCreatable and IArchiveFormatOperations)
        ids.Add(descriptor.Id);

    foreach (var id in ids) this._filesystem.Items.Add(id);
    if (this._filesystem.Items.Count > 0) {
      var fat = ids.ToList().FindIndex(id => string.Equals(id, "Fat", StringComparison.OrdinalIgnoreCase));
      this._filesystem.SelectedIndex = fat >= 0 ? fat : 0;
    }

    this._ok.Click += (_, _) => {
      if (this._filesystem.SelectedItem is not string id || string.IsNullOrEmpty(id)) {
        MessageBox.Show(this, "Pick a filesystem format.", "Invalid input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }

      this.SelectedFormatId = id;
      this.DialogResult = DialogResult.OK;
      this.Close();
    };
    this._cancel.Click += (_, _) => this.Close();
    this.AcceptButton = this._ok;
    this.CancelButton = this._cancel;

    this.Controls.AddRange(
      this._heading,
      new Label {
        Text = "Only filesystems whose descriptors implement IArchiveCreatable are listed.",
        ForeColor = Color.DimGray,
        Bounds = new(12, 36, 420, 20),
      },
      new Label { Text = "Filesystem:", Bounds = new(12, 70, 90, 20) },
      this._filesystem, this._ok, this._cancel);

    this._heading.Bounds = new(12, 12, 436, 44);
    this._filesystem.Bounds = new(106, 68, 340, 24);
    this._ok.Bounds = new(268, 132, 80, 28);
    this._cancel.Bounds = new(356, 132, 80, 28);
  }

  /// <summary>The chosen format id, or null when cancelled.</summary>
  public string? SelectedFormatId { get; private set; }
}
