using System.IO;
using System.Windows;
using System.Windows.Controls;
using Compression.Core.DiskImage;
using Compression.Lib;
using Compression.Registry;

namespace Compression.UI.Views;

/// <summary>
/// Partition Editor window. Lets the user open a raw disk image or a virtual
/// disk container (VHD/VHDX/VMDK/QCOW2/VDI) and edit its MBR/GPT partition
/// table interactively: add, delete, purge, convert MBR↔GPT, format a
/// partition with a fresh filesystem, verify on-disk integrity.
/// </summary>
public partial class PartitionsWindow : Window {

  private string? _imagePath;
  private FileStream? _hostStream;
  private Stream? _guestStream; // for IPartitionEditable; same as _hostStream if backing is raw
  private PartitionEditor? _editor;

  public PartitionsWindow() {
    InitializeComponent();
    Closed += (_, _) => DisposeStreams();
  }

  public PartitionsWindow(string preselectedImage) : this() {
    if (!string.IsNullOrEmpty(preselectedImage) && File.Exists(preselectedImage))
      LoadImage(preselectedImage);
  }

  // ── Open / Refresh ──────────────────────────────────────────────────

  private void OnOpen(object sender, RoutedEventArgs e) {
    var dlg = new Microsoft.Win32.OpenFileDialog {
      Title = "Open disk image / virtual disk",
      Filter = "Disk images & virtual disks|*.img;*.iso;*.bin;*.vhd;*.vhdx;*.vmdk;*.qcow2;*.qcow;*.vdi"
             + "|Raw disk images|*.img;*.iso;*.bin"
             + "|Virtual disk containers|*.vhd;*.vhdx;*.vmdk;*.qcow2;*.qcow;*.vdi"
             + "|All files|*.*",
    };
    if (dlg.ShowDialog(this) != true) return;
    LoadImage(dlg.FileName);
  }

  private void OnRefresh(object sender, RoutedEventArgs e) {
    if (this._imagePath == null) return;
    try {
      this._editor?.Reload();
      RefreshGrid();
      SetStatus($"Reloaded at {DateTime.Now:HH:mm:ss}");
    } catch (Exception ex) {
      ShowError("Refresh failed", ex);
    }
  }

  /// <summary>
  /// Opens an image, detects format, opens the appropriate stream (raw or
  /// virtual-disk guest), constructs a <see cref="PartitionEditor"/>, and
  /// fills the grid.
  /// </summary>
  public void LoadImage(string path) {
    try {
      DisposeStreams();
      this._imagePath = path;
      Title = $"Partition Editor — {Path.GetFileName(path)}";
      PathLbl.Text = path;
      PathLbl.Foreground = System.Windows.Media.Brushes.Black;

      FormatRegistration.EnsureInitialized();
      var format = FormatDetector.Detect(path);
      var formatId = format.ToString();
      FormatLbl.Text = formatId;

      this._hostStream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);

      // If the descriptor advertises IPartitionEditable, route through the
      // guest-disk stream. Otherwise treat the host file itself as the raw
      // disk image (most .img/.iso/.bin files).
      var ops = FormatRegistry.GetArchiveOps(formatId);
      if (ops is IPartitionEditable container) {
        try {
          this._guestStream = container.OpenGuestDiskStream(this._hostStream);
          BackingLbl.Text = $"Virtual disk container ({formatId}) — guest disk stream";
        } catch (Exception ex) {
          // Fall back to raw mode if the container refuses to expose a guest
          // stream (e.g. sparse formats with non-trivial layouts).
          this._guestStream = this._hostStream;
          BackingLbl.Text = $"Container {formatId} refused guest stream — using host bytes ({ex.GetType().Name})";
        }
      } else {
        this._guestStream = this._hostStream;
        BackingLbl.Text = "Raw disk image";
      }

      this._editor = new PartitionEditor(this._guestStream);
      RefreshGrid();
      EnableActions(true);
      RefreshBtn.IsEnabled = true;
      SetStatus($"Loaded {Path.GetFileName(path)} at {DateTime.Now:HH:mm:ss}");
    } catch (Exception ex) {
      DisposeStreams();
      EnableActions(false);
      RefreshBtn.IsEnabled = false;
      ShowError("Could not open image", ex);
    }
  }

  // ── Grid population ─────────────────────────────────────────────────

  private void RefreshGrid() {
    if (this._editor == null) {
      PartGrid.ItemsSource = null;
      SchemeLbl.Text = "Scheme: —";
      DiskSizeLbl.Text = "Disk size: —";
      CountLbl.Text = "Partitions: —";
      return;
    }

    var parts = this._editor.ListPartitions();
    var rows = new List<PartitionRow>(parts.Count);
    foreach (var p in parts)
      rows.Add(new PartitionRow {
        Index = p.Index,
        IndexDisplay = p.Index.ToString(),
        StartLba = p.StartOffset / PartitionEditor.SectorSize,
        StartLbaDisplay = (p.StartOffset / PartitionEditor.SectorSize).ToString("N0"),
        EndLba = (p.StartOffset + p.Size) / PartitionEditor.SectorSize - 1,
        EndLbaDisplay = ((p.StartOffset + p.Size) / PartitionEditor.SectorSize - 1).ToString("N0"),
        Size = p.Size,
        SizeDisplay = FormatSize(p.Size),
        TypeDisplay = string.IsNullOrEmpty(p.TypeName)
          ? p.TypeCode
          : $"{p.TypeName} ({p.TypeCode})",
        Label = p.Name,
        Source = p.Source,
      });
    PartGrid.ItemsSource = rows;

    SchemeLbl.Text = $"Scheme: {this._editor.Scheme}";
    var diskLen = this._guestStream?.Length ?? 0;
    DiskSizeLbl.Text = $"Disk size: {FormatSize(diskLen)} ({diskLen:N0} bytes)";
    CountLbl.Text = $"Partitions: {parts.Count}";

    // Toggle MBR/GPT conversion buttons.
    ConvertMbrGptBtn.IsEnabled = this._editor.Scheme == PartitionScheme.Mbr;
    ConvertGptMbrBtn.IsEnabled = this._editor.Scheme == PartitionScheme.Gpt;
    // Logical add only makes sense when scheme is MBR.
    AddLogicalBtn.IsEnabled = this._editor.Scheme is PartitionScheme.Mbr or PartitionScheme.None;

    OnSelectionChanged(this, null!);
  }

  private void OnSelectionChanged(object sender, SelectionChangedEventArgs? e) {
    var hasSelection = PartGrid.SelectedItem is PartitionRow;
    DeleteBtn.IsEnabled = hasSelection && this._editor != null;
    PurgeBtn.IsEnabled = hasSelection && this._editor != null;
    FormatBtn.IsEnabled = hasSelection && this._editor != null;
  }

  private void EnableActions(bool on) {
    AddBtn.IsEnabled = on;
    AddLogicalBtn.IsEnabled = on;
    VerifyBtn.IsEnabled = on;
    // Selection-driven actions toggled in OnSelectionChanged.
    OnSelectionChanged(this, null!);
  }

  // ── Action handlers ─────────────────────────────────────────────────

  private void OnAdd(object sender, RoutedEventArgs e) {
    if (this._editor == null || this._guestStream == null) return;
    var dlg = new AddPartitionDialog(this._guestStream.Length, isLogical: false) { Owner = this };
    if (dlg.ShowDialog() != true) return;
    try {
      this._editor.AddPartition(dlg.StartOffsetBytes, dlg.LengthBytes, dlg.SelectedType, dlg.Label);
      RefreshGrid();
      SetStatus($"Added partition (start {dlg.StartOffsetBytes:N0}, {FormatSize(dlg.LengthBytes)})");
    } catch (Exception ex) {
      ShowError("Add failed", ex);
    }
  }

  private void OnAddLogical(object sender, RoutedEventArgs e) {
    if (this._editor == null || this._guestStream == null) return;
    var dlg = new AddPartitionDialog(this._guestStream.Length, isLogical: true) { Owner = this };
    if (dlg.ShowDialog() != true) return;
    try {
      this._editor.AddLogicalPartition(dlg.StartOffsetBytes, dlg.LengthBytes, dlg.SelectedType, dlg.Label);
      RefreshGrid();
      SetStatus($"Added logical partition (start {dlg.StartOffsetBytes:N0}, {FormatSize(dlg.LengthBytes)})");
    } catch (Exception ex) {
      ShowError("Add Logical failed", ex);
    }
  }

  private void OnDelete(object sender, RoutedEventArgs e) {
    if (this._editor == null) return;
    if (PartGrid.SelectedItem is not PartitionRow row) return;
    if (MessageBox.Show(this,
          $"Delete partition #{row.Index} ({row.TypeDisplay}, {row.SizeDisplay})?\n\nThe table entry will be removed; the byte range on disk is NOT zeroed.",
          "Confirm Delete", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
      return;
    try {
      this._editor.DeletePartition(row.Index);
      RefreshGrid();
      SetStatus($"Deleted partition #{row.Index}");
    } catch (Exception ex) {
      ShowError("Delete failed", ex);
    }
  }

  private void OnPurge(object sender, RoutedEventArgs e) {
    if (this._editor == null) return;
    if (PartGrid.SelectedItem is not PartitionRow row) return;
    if (MessageBox.Show(this,
          $"Purge partition #{row.Index} ({row.TypeDisplay}, {row.SizeDisplay})?\n\nThe byte range WILL be zero-filled. This is destructive and cannot be undone.",
          "Confirm Purge", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
      return;
    try {
      this._editor.PurgePartition(row.Index);
      RefreshGrid();
      SetStatus($"Purged partition #{row.Index}");
    } catch (Exception ex) {
      ShowError("Purge failed", ex);
    }
  }

  private void OnConvertMbrToGpt(object sender, RoutedEventArgs e) {
    if (this._editor == null) return;
    if (MessageBox.Show(this,
          "Convert the MBR partition table to GPT?\n\nThe extended container (if any) will be dropped; its logical children are promoted to top-level GPT entries. A protective MBR will be written at LBA 0.",
          "Confirm MBR → GPT", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
      return;
    try {
      this._editor.ConvertMbrToGpt();
      RefreshGrid();
      SetStatus("Converted MBR → GPT");
    } catch (Exception ex) {
      ShowError("MBR → GPT failed", ex);
    }
  }

  private void OnConvertGptToMbr(object sender, RoutedEventArgs e) {
    if (this._editor == null) return;
    if (MessageBox.Show(this,
          "Convert the GPT partition table to MBR?\n\nOnly the first 4 partitions can be preserved (MBR limit). Both GPT header areas will be zeroed.",
          "Confirm GPT → MBR", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
      return;
    try {
      this._editor.ConvertGptToMbr();
      RefreshGrid();
      SetStatus("Converted GPT → MBR");
    } catch (Exception ex) {
      ShowError("GPT → MBR failed", ex);
    }
  }

  private void OnFormat(object sender, RoutedEventArgs e) {
    if (this._editor == null) return;
    if (PartGrid.SelectedItem is not PartitionRow row) return;
    var dlg = new FormatPartitionDialog(row.Index, row.SizeDisplay) { Owner = this };
    if (dlg.ShowDialog() != true) return;
    try {
      this._editor.FormatPartition(row.Index, dlg.SelectedFormatId!, new FormatCreateOptions());
      RefreshGrid();
      SetStatus($"Formatted partition #{row.Index} as {dlg.SelectedFormatId}");
    } catch (Exception ex) {
      ShowError("Format failed", ex);
    }
  }

  private void OnVerify(object sender, RoutedEventArgs e) {
    if (this._editor == null) return;
    try {
      var result = this._editor.Verify();
      var icon = result.IsValid ? MessageBoxImage.Information : MessageBoxImage.Warning;
      var heading = result.IsValid
        ? $"Partition table OK ({result.Scheme})."
        : $"Partition table has {result.Issues.Count} issue(s) ({result.Scheme}):";
      var body = result.Issues.Count == 0
        ? heading
        : heading + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, result.Issues);
      MessageBox.Show(this, body, "Verify", MessageBoxButton.OK, icon);
      SetStatus(result.IsValid ? "Verify: OK" : $"Verify: {result.Issues.Count} issue(s)");
    } catch (Exception ex) {
      ShowError("Verify failed", ex);
    }
  }

  // ── Helpers ─────────────────────────────────────────────────────────

  private void DisposeStreams() {
    try { this._guestStream?.Dispose(); } catch { /* swallow */ }
    if (!ReferenceEquals(this._guestStream, this._hostStream)) {
      try { this._hostStream?.Dispose(); } catch { /* swallow */ }
    }
    this._guestStream = null;
    this._hostStream = null;
    this._editor = null;
  }

  private void SetStatus(string message) {
    StatusLbl.Text = message;
  }

  private void ShowError(string title, Exception ex) {
    SetStatus($"{title}: {ex.GetType().Name}");
    MessageBox.Show(this,
      $"{title}:\n\n{ex.GetType().Name}: {ex.Message}",
      title, MessageBoxButton.OK, MessageBoxImage.Error);
  }

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };

  /// <summary>
  /// Row view-model for the partition DataGrid. Stores both raw numeric and
  /// pre-formatted display strings so the grid can sort by Start/End/Size
  /// without having to parse formatted output.
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
