using System.IO;
using System.Windows;
using System.Windows.Input;
using Compression.Analysis.Visualization;
using static Compression.Analysis.Visualization.HierarchicalBlockMap;

namespace Compression.UI.ViewModels;

/// <summary>
/// ViewModel for the hierarchical heatmap file explorer.
/// Manages zoom stack, block selection, and extraction.
/// </summary>
internal sealed class HeatmapExplorerViewModel : ViewModelBase {

  /// <summary>Fires when a new level is loaded (UI updates cell colors).</summary>
  public event Action<LevelResult?>? LevelChanged;

  // ── Navigation stack ───────────────────────────────────────────────

  private readonly Stack<LevelResult> _zoomStack = new();
  private LevelResult? _currentLevel;
  private Stream? _stream;
  private string? _filePath;

  // ── Bindable properties ────────────────────────────────────────────

  private string _breadcrumbPath = "No file loaded";
  public string BreadcrumbPath { get => _breadcrumbPath; set => SetField(ref _breadcrumbPath, value); }

  private bool _isEmpty = true;
  public bool IsEmpty { get => _isEmpty; set => SetField(ref _isEmpty, value); }

  private string _selectedBlockInfo = "";
  public string SelectedBlockInfo { get => _selectedBlockInfo; set => SetField(ref _selectedBlockInfo, value); }

  private string _selectedBlockDetail = "";
  public string SelectedBlockDetail { get => _selectedBlockDetail; set => SetField(ref _selectedBlockDetail, value); }

  private bool _canExtract;
  public bool CanExtract { get => _canExtract; set => SetField(ref _canExtract, value); }

  private int _selectedIndex = -1;

  // ── Commands ───────────────────────────────────────────────────────

  public ICommand OpenFileCommand { get; }
  public ICommand ZoomOutCommand { get; }
  public ICommand ExtractCommand { get; }

  public HeatmapExplorerViewModel() {
    OpenFileCommand = new RelayCommand(_ => OpenFile());
    ZoomOutCommand = new RelayCommand(_ => ZoomOut(), _ => _zoomStack.Count > 0);
    ExtractCommand = new RelayCommand(_ => ExtractSelected(), _ => CanExtract);
  }

  // ── File loading ───────────────────────────────────────────────────

  /// <summary>Opens a file via dialog.</summary>
  public void OpenFile() {
    var dlg = new Microsoft.Win32.OpenFileDialog {
      Title = "Open file to explore",
      Filter = "All files (*.*)|*.*|Disk images (*.vmdk;*.vhd;*.qcow2;*.vdi;*.img;*.raw)|*.vmdk;*.vhd;*.qcow2;*.vdi;*.img;*.raw"
    };
    if (dlg.ShowDialog() != true) return;
    LoadFile(dlg.FileName);
  }

  /// <summary>Loads a file directly (can be called from code).</summary>
  public void LoadFile(string path) {
    _stream?.Dispose();
    _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.RandomAccess);
    _filePath = path;
    _zoomStack.Clear();

    var level = Analyze(_stream);
    SetLevel(level);
    IsEmpty = false;
  }

  /// <summary>Loads from an already-open stream (for embedding in other views).</summary>
  public void LoadStream(Stream stream, string name = "Stream") {
    _stream = stream;
    _filePath = name;
    _zoomStack.Clear();

    var level = Analyze(stream);
    SetLevel(level);
    IsEmpty = false;
  }

  // ── Navigation ─────────────────────────────────────────────────────

  /// <summary>Drill into a specific block (called on cell click).</summary>
  public void DrillInto(int blockIndex) {
    if (_currentLevel == null || _stream == null) return;
    var block = _currentLevel.Blocks[blockIndex];
    if (!block.CanExpand) return;

    var (offset, size, path) = GetChildRegion(_currentLevel, blockIndex);

    _zoomStack.Push(_currentLevel);
    var child = Analyze(_stream, offset, size, _currentLevel.Depth + 1, path);
    SetLevel(child);
  }

  /// <summary>Go back one zoom level.</summary>
  public void ZoomOut() {
    if (_zoomStack.Count == 0) return;
    SetLevel(_zoomStack.Pop());
  }

  /// <summary>Select a block for info display (called on cell hover).</summary>
  public void SelectBlock(int blockIndex) {
    if (_currentLevel == null) return;
    _selectedIndex = blockIndex;
    var block = _currentLevel.Blocks[blockIndex];

    var size = FormatSize(block.Size);
    SelectedBlockInfo = $"Block {blockIndex} | Offset: 0x{block.Offset:X} | Size: {size} | Entropy: {block.Entropy:F2} | Type: {block.Type}";

    var detail = block.IsZero
      ? "All zeros (empty/unallocated)"
      : block.SignatureName != null
        ? $"Detected: {block.SignatureName} | Unique bytes: {block.UniqueBytes} | Dominant: 0x{block.DominantByte:X2}"
        : $"Unique bytes: {block.UniqueBytes}/256 | Dominant: 0x{block.DominantByte:X2} ({(double)block.UniqueBytes / 256:P0} diversity)";
    SelectedBlockDetail = detail;

    CanExtract = block.SignatureName != null && block.Size > 0;
  }

  // ── Extraction ─────────────────────────────────────────────────────

  private void ExtractSelected() {
    if (_currentLevel == null || _stream == null || _selectedIndex < 0) return;
    var block = _currentLevel.Blocks[_selectedIndex];

    var dlg = new Microsoft.Win32.SaveFileDialog {
      Title = "Extract block",
      FileName = $"block_{block.Offset:X}_{FormatSize(block.Size).Replace(" ", "")}.bin",
      Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*"
    };
    if (dlg.ShowDialog() != true) return;

    _stream.Position = block.Offset;
    var buffer = new byte[Math.Min(block.Size, 64 * 1024 * 1024)]; // Cap at 64MB per extract
    var toRead = (int)Math.Min(buffer.Length, block.Size);
    var read = _stream.Read(buffer, 0, toRead);
    File.WriteAllBytes(dlg.FileName, buffer[..read]);

    MessageBox.Show($"Extracted {FormatSize(read)} to {dlg.FileName}", "Extraction Complete");
  }

  // ── Internals ──────────────────────────────────────────────────────

  private void SetLevel(LevelResult level) {
    _currentLevel = level;
    BreadcrumbPath = $"{Path.GetFileName(_filePath)} | {level.Path} | {FormatSize(level.RegionSize)} | Depth {level.Depth}";
    LevelChanged?.Invoke(level);
  }

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
  };
}
