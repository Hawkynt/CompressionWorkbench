using System.Drawing;
using Compression.Core.DiskImage;
using Compression.Lib;
using Compression.NativeUI.Theming;
using Compression.Registry;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Views;

/// <summary>
/// Opens a raw disk image or a virtual-disk container and edits its MBR or GPT partition table:
/// add, delete, purge, convert between the two schemes, format a partition, and verify the table.
/// </summary>
internal sealed class PartitionsWindow : Form {
  private readonly ToolStrip _toolbar = new();
  private readonly ToolStripButton _open = new("Open...") { ToolTipText = "Open a disk image / virtual disk" };
  private readonly ToolStripButton _refresh = new("Refresh") { Enabled = false, ToolTipText = "Re-read the partition table from disk" };

  private readonly Panel _infoPanel = new() { BorderStyle = BorderStyle.FixedSingle };
  private readonly Label _pathLabel = new() { Text = "(no image loaded)", ForeColor = Color.DimGray };
  private readonly Label _formatLabel = new() { Text = "—" };
  private readonly Label _backingLabel = new() { Text = "—", ForeColor = Color.DimGray };

  private readonly DataGridView _grid = new() { ReadOnly = true, ShowGridLines = true, AlternatingRows = true };

  private readonly Button _add = new() { Text = "Add...", Enabled = false };
  private readonly Button _addLogical = new() { Text = "Add Logical...", Enabled = false };
  private readonly Button _delete = new() { Text = "Delete", Enabled = false };
  private readonly Button _purge = new() { Text = "Purge", Enabled = false };
  private readonly Button _convertMbrGpt = new() { Text = "MBR → GPT", Enabled = false };
  private readonly Button _convertGptMbr = new() { Text = "GPT → MBR", Enabled = false };
  private readonly Button _format = new() { Text = "Format...", Enabled = false };
  private readonly Button _verify = new() { Text = "Verify", Enabled = false };

  private readonly StatusStrip _status = new();
  private readonly ToolStripStatusLabel _scheme = new("Scheme: —");
  private readonly ToolStripStatusLabel _diskSize = new("Disk size: —");
  private readonly ToolStripStatusLabel _count = new("Partitions: —");
  private readonly ToolStripStatusLabel _statusText = new();
  private readonly ToolTip _toolTips = new();

  private string? _imagePath;
  private FileStream? _hostStream;
  private Stream? _guestStream;
  private PartitionEditor? _editor;

  public PartitionsWindow() {
    this.Text = "Partition Editor";
    this.ClientSize = new(980, 640);
    this.MinimumSize = new(780, 480);
    this.StartPosition = FormStartPosition.CenterParent;

    this.BuildToolbar();
    this.BuildInfoPanel();
    this.BuildGrid();
    this.BuildActions();
    this.BuildStatusBar();

    this.FormClosed += (_, _) => this.DisposeStreams();
    this.Resize += (_, _) => this.LayoutChildren();
    this.LayoutChildren();
  }

  public PartitionsWindow(string preselectedImage) : this() {
    if (!string.IsNullOrEmpty(preselectedImage) && File.Exists(preselectedImage))
      this.LoadImage(preselectedImage);
  }

  private void BuildToolbar() {
    this._open.Image = Images.Icon(IconKeys.Open, 16);
    this._open.Click += (_, _) => this.OnOpen();
    this._refresh.Image = Images.Icon(IconKeys.NavigateUp, 16);
    this._refresh.Click += (_, _) => this.OnRefresh();

    this._toolbar.Items.AddRange([this._open, this._refresh]);
    this.Controls.Add(this._toolbar);
  }

  private void BuildInfoPanel() {
    var bold = new Font(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold);
    this._infoPanel.Controls.AddRange(
      new Label { Text = "Image:", Font = bold, Bounds = new(6, 6, 76, 20) }, this._pathLabel,
      new Label { Text = "Format:", Font = bold }, this._formatLabel,
      new Label { Text = "Backing:", Font = bold, Bounds = new(6, 28, 76, 20) }, this._backingLabel);

    this.Controls.Add(this._infoPanel);
  }

  /// <summary>
  /// Builds the table. The LBA columns are right-aligned rather than monospaced — a cell style
  /// carries colours and alignment but not a font — which keeps the digits lined up all the same.
  /// </summary>
  private void BuildGrid() {
    AddRight(this._grid, "#", static o => ((PartitionRow)o!).IndexDisplay, 40, (a, b) => ((PartitionRow)a!).Index.CompareTo(((PartitionRow)b!).Index));
    AddRight(this._grid, "Start LBA", static o => ((PartitionRow)o!).StartLbaDisplay, 110, (a, b) => ((PartitionRow)a!).StartLba.CompareTo(((PartitionRow)b!).StartLba));
    AddRight(this._grid, "End LBA", static o => ((PartitionRow)o!).EndLbaDisplay, 110, (a, b) => ((PartitionRow)a!).EndLba.CompareTo(((PartitionRow)b!).EndLba));
    AddRight(this._grid, "Size", static o => ((PartitionRow)o!).SizeDisplay, 90, (a, b) => ((PartitionRow)a!).Size.CompareTo(((PartitionRow)b!).Size));
    this._grid.Columns.Add(new DataGridViewColumn("Type", static o => ((PartitionRow)o!).TypeDisplay) { Width = 200 });
    this._grid.Columns.Add(new DataGridViewColumn("Label", static o => ((PartitionRow)o!).Label) { Width = 160 });
    this._grid.Columns.Add(new DataGridViewColumn("Source", static o => ((PartitionRow)o!).Source) { Width = 160 });

    this._grid.SelectionChanged += (_, _) => this.UpdateSelectionActions();
    this.Controls.Add(this._grid);

    static void AddRight(DataGridView grid, string header, Func<object?, object?> selector, int width, Comparison<object?> sort)
      => grid.Columns.Add(new DataGridViewColumn(header, selector) {
        Width = width,
        Alignment = ContentAlignment.MiddleRight,
        SortComparison = sort,
      });
  }

  private void BuildActions() {
    this._toolTips.SetToolTip(this._add, "Add a new primary (MBR) or GPT partition.");
    this._toolTips.SetToolTip(this._addLogical, "Add a logical partition inside the MBR extended container.");
    this._toolTips.SetToolTip(this._delete, "Drop the selected partition from the table (data not zeroed).");
    this._toolTips.SetToolTip(this._purge, "Drop the selected partition AND zero-fill its byte range.");
    this._toolTips.SetToolTip(this._convertMbrGpt, "Convert the current MBR partition table to GPT.");
    this._toolTips.SetToolTip(this._convertGptMbr, "Convert the current GPT partition table to MBR (only works when ≤4 entries).");
    this._toolTips.SetToolTip(this._format, "Write a fresh filesystem image into the selected partition's byte range.");
    this._toolTips.SetToolTip(this._verify, "Run on-disk integrity checks: signatures, CRCs, primary/backup divergence.");

    this._add.Click += (_, _) => this.OnAdd(isLogical: false);
    this._addLogical.Click += (_, _) => this.OnAdd(isLogical: true);
    this._delete.Click += (_, _) => this.OnDelete();
    this._purge.Click += (_, _) => this.OnPurge();
    this._convertMbrGpt.Click += (_, _) => this.OnConvert(toGpt: true);
    this._convertGptMbr.Click += (_, _) => this.OnConvert(toGpt: false);
    this._format.Click += (_, _) => this.OnFormat();
    this._verify.Click += (_, _) => this.OnVerify();

    this.Controls.AddRange(
      new Label { Text = "Actions", Font = new(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold) },
      this._add, this._addLogical, this._delete, this._purge,
      this._convertMbrGpt, this._convertGptMbr, this._format, this._verify);
  }

  private void BuildStatusBar() {
    this._status.Items.AddRange([
      this._scheme, new ToolStripSeparator(),
      this._diskSize, new ToolStripSeparator(),
      this._count, this._statusText,
    ]);
    this.Controls.Add(this._status);
  }

  private void LayoutChildren() {
    const int Gutter = 8;
    const int ActionWidth = 160;
    var width = this.ClientSize.Width - 2 * Gutter;

    this._toolbar.Bounds = new(0, 0, this.ClientSize.Width, 30);
    this._infoPanel.Bounds = new(Gutter, 36, width, 54);
    this._pathLabel.Bounds = new(88, 6, Math.Max(80, width - 300), 20);
    this._infoPanel.Controls[2].Bounds = new(width - 210, 6, 60, 20); // "Format:"
    this._formatLabel.Bounds = new(width - 146, 6, 140, 20);
    this._backingLabel.Bounds = new(88, 28, Math.Max(80, width - 100), 20);

    var gridTop = 96;
    var gridHeight = Math.Max(120, this.ClientSize.Height - gridTop - 34);
    this._grid.Bounds = new(Gutter, gridTop, Math.Max(200, width - ActionWidth - 8), gridHeight);

    var actionX = Gutter + width - ActionWidth;
    var y = gridTop;
    this.Controls[^9].Bounds = new(actionX, y, ActionWidth, 20); // "Actions"
    y += 24;

    foreach (var button in new[] { this._add, this._addLogical, this._delete, this._purge }) {
      button.Bounds = new(actionX, y, ActionWidth, 26);
      y += 30;
    }

    y += 8;
    foreach (var button in new[] { this._convertMbrGpt, this._convertGptMbr }) {
      button.Bounds = new(actionX, y, ActionWidth, 26);
      y += 30;
    }

    y += 8;
    foreach (var button in new[] { this._format, this._verify }) {
      button.Bounds = new(actionX, y, ActionWidth, 26);
      y += 30;
    }

    this._status.Bounds = new(0, this.ClientSize.Height - 26, this.ClientSize.Width, 26);
  }

  private void OnOpen() {
    var dialog = new OpenFileDialog {
      Title = "Open disk image / virtual disk",
      Filter = "Disk images & virtual disks|*.img;*.iso;*.bin;*.vhd;*.vhdx;*.vmdk;*.qcow2;*.qcow;*.vdi"
             + "|Raw disk images|*.img;*.iso;*.bin"
             + "|Virtual disk containers|*.vhd;*.vhdx;*.vmdk;*.qcow2;*.qcow;*.vdi"
             + "|All files|*.*",
    };
    if (dialog.ShowDialog() == DialogResult.OK) this.LoadImage(dialog.FileName);
  }

  private void OnRefresh() {
    if (this._imagePath is null) return;
    try {
      this._editor?.Reload();
      this.RefreshGrid();
      this.SetStatus($"Reloaded at {DateTime.Now:HH:mm:ss}");
    } catch (Exception ex) {
      this.ShowError("Refresh failed", ex);
    }
  }

  /// <summary>
  /// Opens an image, detects its format, takes the raw or guest-disk stream as appropriate, and
  /// fills the table.
  /// </summary>
  public void LoadImage(string path) {
    try {
      this.DisposeStreams();
      this._imagePath = path;
      this.Text = $"Partition Editor — {Path.GetFileName(path)}";
      this._pathLabel.Text = path;
      this._pathLabel.ForeColor = Color.Black;

      FormatRegistration.EnsureInitialized();
      var formatId = FormatDetector.Detect(path).ToString();
      this._formatLabel.Text = formatId;

      this._hostStream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);

      // A container that advertises an editable partition surface is routed through its guest-disk
      // stream; everything else treats the file itself as the raw disk.
      if (FormatRegistry.GetArchiveOps(formatId) is IPartitionEditable container) {
        try {
          this._guestStream = container.OpenGuestDiskStream(this._hostStream);
          this._backingLabel.Text = $"Virtual disk container ({formatId}) — guest disk stream";
        } catch (Exception ex) {
          // A sparse format with a non-trivial layout may refuse; the host bytes still work.
          this._guestStream = this._hostStream;
          this._backingLabel.Text = $"Container {formatId} refused guest stream — using host bytes ({ex.GetType().Name})";
        }
      } else {
        this._guestStream = this._hostStream;
        this._backingLabel.Text = "Raw disk image";
      }

      this._editor = new(this._guestStream);
      this.RefreshGrid();
      this._add.Enabled = true;
      this._addLogical.Enabled = true;
      this._verify.Enabled = true;
      this._refresh.Enabled = true;
      this.SetStatus($"Loaded {Path.GetFileName(path)} at {DateTime.Now:HH:mm:ss}");
    } catch (Exception ex) {
      this.DisposeStreams();
      foreach (var button in new[] { this._add, this._addLogical, this._verify }) button.Enabled = false;
      this._refresh.Enabled = false;
      this.ShowError("Could not open image", ex);
    }
  }

  private void RefreshGrid() {
    if (this._editor is not { } editor) {
      this._grid.DataSource = Array.Empty<object>();
      this._scheme.Text = "Scheme: —";
      this._diskSize.Text = "Disk size: —";
      this._count.Text = "Partitions: —";
      return;
    }

    var partitions = editor.ListPartitions();
    var rows = partitions.Select(p => {
      var startLba = p.StartOffset / PartitionEditor.SectorSize;
      var endLba = (p.StartOffset + p.Size) / PartitionEditor.SectorSize - 1;
      return new PartitionRow {
        Index = p.Index,
        IndexDisplay = p.Index.ToString(),
        StartLba = startLba,
        StartLbaDisplay = startLba.ToString("N0"),
        EndLba = endLba,
        EndLbaDisplay = endLba.ToString("N0"),
        Size = p.Size,
        SizeDisplay = FormatSize(p.Size),
        TypeDisplay = string.IsNullOrEmpty(p.TypeName) ? p.TypeCode : $"{p.TypeName} ({p.TypeCode})",
        Label = p.Name,
        Source = p.Source,
      };
    }).ToList();

    this._grid.DataSource = rows;

    this._scheme.Text = $"Scheme: {editor.Scheme}";
    var diskLength = this._guestStream?.Length ?? 0;
    this._diskSize.Text = $"Disk size: {FormatSize(diskLength)} ({diskLength:N0} bytes)";
    this._count.Text = $"Partitions: {partitions.Count}";

    this._convertMbrGpt.Enabled = editor.Scheme == PartitionScheme.Mbr;
    this._convertGptMbr.Enabled = editor.Scheme == PartitionScheme.Gpt;
    // A logical partition only exists inside an MBR extended container.
    this._addLogical.Enabled = editor.Scheme is PartitionScheme.Mbr or PartitionScheme.None;

    this.UpdateSelectionActions();
  }

  private void UpdateSelectionActions() {
    var hasSelection = this._grid.SelectedItem is PartitionRow && this._editor is not null;
    this._delete.Enabled = hasSelection;
    this._purge.Enabled = hasSelection;
    this._format.Enabled = hasSelection;
  }

  private void OnAdd(bool isLogical) {
    if (this._editor is not { } editor || this._guestStream is not { } guest) return;

    var dialog = new AddPartitionDialog(guest.Length, isLogical);
    if (dialog.ShowDialog(this) != DialogResult.OK) return;

    try {
      if (isLogical) editor.AddLogicalPartition(dialog.StartOffsetBytes, dialog.LengthBytes, dialog.SelectedType, dialog.Label);
      else editor.AddPartition(dialog.StartOffsetBytes, dialog.LengthBytes, dialog.SelectedType, dialog.Label);

      this.RefreshGrid();
      this.SetStatus($"Added {(isLogical ? "logical " : "")}partition (start {dialog.StartOffsetBytes:N0}, {FormatSize(dialog.LengthBytes)})");
    } catch (Exception ex) {
      this.ShowError(isLogical ? "Add Logical failed" : "Add failed", ex);
    }
  }

  private void OnDelete() {
    if (this._editor is not { } editor || this._grid.SelectedItem is not PartitionRow row) return;

    if (MessageBox.Show(this,
        $"Delete partition #{row.Index} ({row.TypeDisplay}, {row.SizeDisplay})?\n\n"
        + "The table entry will be removed; the byte range on disk is NOT zeroed.",
        "Confirm Delete", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

    try {
      editor.DeletePartition(row.Index);
      this.RefreshGrid();
      this.SetStatus($"Deleted partition #{row.Index}");
    } catch (Exception ex) {
      this.ShowError("Delete failed", ex);
    }
  }

  private void OnPurge() {
    if (this._editor is not { } editor || this._grid.SelectedItem is not PartitionRow row) return;

    if (MessageBox.Show(this,
        $"Purge partition #{row.Index} ({row.TypeDisplay}, {row.SizeDisplay})?\n\n"
        + "The byte range WILL be zero-filled. This is destructive and cannot be undone.",
        "Confirm Purge", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

    try {
      editor.PurgePartition(row.Index);
      this.RefreshGrid();
      this.SetStatus($"Purged partition #{row.Index}");
    } catch (Exception ex) {
      this.ShowError("Purge failed", ex);
    }
  }

  private void OnConvert(bool toGpt) {
    if (this._editor is not { } editor) return;

    var (prompt, title, icon) = toGpt
      ? ("Convert the MBR partition table to GPT?\n\nThe extended container (if any) will be dropped; its logical children are promoted to top-level GPT entries. A protective MBR will be written at LBA 0.",
         "Confirm MBR → GPT", MessageBoxIcon.Question)
      : ("Convert the GPT partition table to MBR?\n\nOnly the first 4 partitions can be preserved (MBR limit). Both GPT header areas will be zeroed.",
         "Confirm GPT → MBR", MessageBoxIcon.Warning);

    if (MessageBox.Show(this, prompt, title, MessageBoxButtons.OKCancel, icon) != DialogResult.OK) return;

    try {
      if (toGpt) editor.ConvertMbrToGpt();
      else editor.ConvertGptToMbr();

      this.RefreshGrid();
      this.SetStatus(toGpt ? "Converted MBR → GPT" : "Converted GPT → MBR");
    } catch (Exception ex) {
      this.ShowError(toGpt ? "MBR → GPT failed" : "GPT → MBR failed", ex);
    }
  }

  private void OnFormat() {
    if (this._editor is not { } editor || this._grid.SelectedItem is not PartitionRow row) return;

    var dialog = new FormatPartitionDialog(row.Index, row.SizeDisplay);
    if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedFormatId is not { } formatId) return;

    try {
      editor.FormatPartition(row.Index, formatId, new FormatCreateOptions());
      this.RefreshGrid();
      this.SetStatus($"Formatted partition #{row.Index} as {formatId}");
    } catch (Exception ex) {
      this.ShowError("Format failed", ex);
    }
  }

  private void OnVerify() {
    if (this._editor is not { } editor) return;

    try {
      var result = editor.Verify();
      var heading = result.IsValid
        ? $"Partition table OK ({result.Scheme})."
        : $"Partition table has {result.Issues.Count} issue(s) ({result.Scheme}):";
      var body = result.Issues.Count == 0
        ? heading
        : heading + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, result.Issues);

      MessageBox.Show(this, body, "Verify", MessageBoxButtons.OK,
        result.IsValid ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
      this.SetStatus(result.IsValid ? "Verify: OK" : $"Verify: {result.Issues.Count} issue(s)");
    } catch (Exception ex) {
      this.ShowError("Verify failed", ex);
    }
  }

  private void DisposeStreams() {
    try {
      this._guestStream?.Dispose();
    } catch {
      // A container stream that fails to close cannot block the window.
    }

    if (!ReferenceEquals(this._guestStream, this._hostStream))
      try {
        this._hostStream?.Dispose();
      } catch {
        // Same.
      }

    this._guestStream = null;
    this._hostStream = null;
    this._editor = null;
  }

  private void SetStatus(string message) => this._statusText.Text = message;

  private void ShowError(string title, Exception ex) {
    this.SetStatus($"{title}: {ex.GetType().Name}");
    MessageBox.Show(this, $"{title}:\n\n{ex.GetType().Name}: {ex.Message}", title, MessageBoxButtons.OK, MessageBoxIcon.Error);
  }

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };

  /// <summary>
  /// One partition row. Raw numbers sit beside their formatted text so the grid sorts by value
  /// rather than by what is displayed.
  /// </summary>
  private sealed class PartitionRow {
    public int Index { get; init; }
    public string IndexDisplay { get; init; } = "";
    public long StartLba { get; init; }
    public string StartLbaDisplay { get; init; } = "";
    public long EndLba { get; init; }
    public string EndLbaDisplay { get; init; } = "";
    public long Size { get; init; }
    public string SizeDisplay { get; init; } = "";
    public string TypeDisplay { get; init; } = "";
    public string Label { get; init; } = "";
    public string Source { get; init; } = "";
  }
}
