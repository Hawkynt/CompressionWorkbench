using System.IO;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;
using Grid = System.Windows.Controls.Grid;
using Border = System.Windows.Controls.Border;
using TextBlock = System.Windows.Controls.TextBlock;
using Brushes = System.Windows.Media.Brushes;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color = System.Windows.Media.Color;
using FontWeights = System.Windows.FontWeights;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using RowDefinition = System.Windows.Controls.RowDefinition;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using Style = System.Windows.Style;
using TextTrimming = System.Windows.TextTrimming;
using Compression.Analysis.Visualization;

namespace Compression.UI.Controls;

public partial class HeatmapGridControl : UserControl {

  private Stream? _stream;
  private readonly Stack<(long Offset, long Length)> _navStack = new();
  private long _currentOffset;
  private long _currentLength;
  private HeatmapTile[]? _currentTiles;

  public bool CanGoBack => _navStack.Count > 0;

  public HeatmapGridControl() {
    InitializeComponent();
    BuildGridStructure();
  }

  /// <summary>Opens a file and shows the top-level heatmap.</summary>
  public void OpenFile(string path) {
    _stream?.Dispose();
    _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.RandomAccess);
    _navStack.Clear();
    NavigateTo(0, _stream.Length);
    UpdateBreadcrumb(path);
  }

  /// <summary>Opens an existing stream (e.g., extracted partition data).</summary>
  public void OpenStream(Stream stream, string label) {
    _stream = stream;
    _navStack.Clear();
    NavigateTo(0, stream.Length);
    UpdateBreadcrumb(label);
  }

  private void NavigateTo(long offset, long length) {
    if (_stream == null) return;
    _currentOffset = offset;
    _currentLength = length;

    _currentTiles = HeatmapComputer.ComputeGrid(_stream, offset, length);
    RenderTiles(_currentTiles);
    UpdateInfo(null);
  }

  private void BuildGridStructure() {
    TileGrid.Children.Clear();
    TileGrid.RowDefinitions.Clear();
    TileGrid.ColumnDefinitions.Clear();

    for (var i = 0; i < HeatmapComputer.GridSize; i++) {
      TileGrid.RowDefinitions.Add(new RowDefinition());
      TileGrid.ColumnDefinitions.Add(new ColumnDefinition());
    }

    for (var row = 0; row < HeatmapComputer.GridSize; row++) {
      for (var col = 0; col < HeatmapComputer.GridSize; col++) {
        var border = new Border {
          Style = (Style)Resources["TileStyle"],
          Background = Brushes.DimGray,
          Tag = row * HeatmapComputer.GridSize + col
        };
        border.MouseLeftButtonDown += OnTileClick;
        border.MouseEnter += OnTileHover;
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        TileGrid.Children.Add(border);
      }
    }
  }

  private void RenderTiles(HeatmapTile[] tiles) {
    for (var i = 0; i < Math.Min(tiles.Length, TileGrid.Children.Count); i++) {
      if (TileGrid.Children[i] is Border border) {
        border.Background = GetTileBrush(tiles[i]);

        // Show format label on known formats.
        if (tiles[i].DetectedFormat != null) {
          border.Child = new TextBlock {
            Text = tiles[i].DetectedFormat![..Math.Min(4, tiles[i].DetectedFormat!.Length)],
            Foreground = Brushes.White, FontSize = 8, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
          };
        } else {
          border.Child = null;
        }
      }
    }
  }

  private static SolidColorBrush GetTileBrush(HeatmapTile tile) => tile.Classification switch {
    TileClass.Empty => new SolidColorBrush(Color.FromRgb(170, 170, 170)),
    TileClass.Zeros => new SolidColorBrush(Color.FromRgb(39, 64, 139)),
    TileClass.KnownFormat => new SolidColorBrush(Color.FromRgb(139, 92, 246)),
    TileClass.VeryLowEntropy => new SolidColorBrush(Color.FromRgb(65, 105, 225)),
    TileClass.LowEntropy => new SolidColorBrush(Color.FromRgb(32, 178, 170)),
    TileClass.MediumEntropy => new SolidColorBrush(Color.FromRgb(34, 139, 34)),
    TileClass.HighEntropy => new SolidColorBrush(Color.FromRgb(218, 165, 32)),
    TileClass.Compressed => new SolidColorBrush(Color.FromRgb(205, 102, 0)),
    TileClass.Encrypted => new SolidColorBrush(Color.FromRgb(205, 38, 38)),
    _ => new SolidColorBrush(Color.FromRgb(170, 170, 170))
  };

  private void OnTileClick(object sender, MouseButtonEventArgs e) {
    if (sender is not Border { Tag: int index } || _currentTiles == null) return;
    if (index >= _currentTiles.Length) return;

    var tile = _currentTiles[index];
    if (tile.Length <= 0) return;

    // If the tile is small enough (< 4KB), show detail instead of drilling down.
    if (tile.Length < 4096) {
      ShowTileDetail(tile);
      return;
    }

    // Push current view onto nav stack and drill down.
    _navStack.Push((_currentOffset, _currentLength));
    NavigateTo(tile.Offset, tile.Length);
    UpdateBreadcrumb(null);
  }

  private void OnTileHover(object sender, MouseEventArgs e) {
    if (sender is not Border { Tag: int index } || _currentTiles == null) return;
    if (index >= _currentTiles.Length) return;
    UpdateInfo(_currentTiles[index]);
  }

  private void UpdateInfo(HeatmapTile? tile) {
    if (tile == null) {
      InfoText.Text = $"Region: 0x{_currentOffset:X} - 0x{_currentOffset + _currentLength:X} ({FormatSize(_currentLength)}) | {_navStack.Count} levels deep";
      return;
    }

    var parts = new List<string> {
      $"Offset: 0x{tile.Offset:X}",
      $"Size: {FormatSize(tile.Length)}",
      $"Entropy: {tile.Entropy:F2}",
      $"Zeros: {tile.ZeroFraction:P0}",
      $"ASCII: {tile.AsciiFraction:P0}",
      $"Class: {tile.Classification}"
    };
    if (tile.DetectedFormat != null)
      parts.Add($"Format: {tile.DetectedFormat}");

    InfoText.Text = string.Join(" | ", parts);
  }

  private void ShowTileDetail(HeatmapTile tile) {
    if (_stream == null) return;

    var readLen = (int)Math.Min(tile.Length, 64 * 1024); // up to 64KB for preview
    var data = HeatmapComputer.ReadRegion(_stream, tile.Offset, readLen);

    var title = $"0x{tile.Offset:X} — {FormatSize(tile.Length)}, entropy {tile.Entropy:F2}";
    if (tile.DetectedFormat != null)
      title += $" [{tile.DetectedFormat}]";

    var preview = new Views.PreviewWindow();
    preview.ShowData(title, data, hex: tile.Entropy > 5.0 || tile.AsciiFraction < 0.5, analyzeMode: true);
    preview.Show();
  }

  private void UpdateBreadcrumb(string? rootLabel) {
    var parts = new List<string>();
    if (rootLabel != null) parts.Add(Path.GetFileName(rootLabel));

    var depth = _navStack.Count;
    if (depth > 0)
      parts.Add($"depth {depth}");

    parts.Add($"0x{_currentOffset:X}-0x{_currentOffset + _currentLength:X} ({FormatSize(_currentLength)})");
    BreadcrumbText.Text = string.Join(" > ", parts);
  }

  private void OnBack(object sender, RoutedEventArgs e) {
    if (_navStack.Count == 0) return;
    var (offset, length) = _navStack.Pop();
    NavigateTo(offset, length);
    UpdateBreadcrumb(null);
  }

  private void OnRoot(object sender, RoutedEventArgs e) {
    if (_stream == null || _navStack.Count == 0) return;
    _navStack.Clear();
    NavigateTo(0, _stream.Length);
    UpdateBreadcrumb(null);
  }

  private void OnExtract(object sender, RoutedEventArgs e) {
    if (_stream == null) return;

    var dlg = new Microsoft.Win32.SaveFileDialog {
      FileName = $"extract_0x{_currentOffset:X}_{_currentLength}.bin",
      Filter = "Binary files|*.bin|All files|*.*"
    };
    if (dlg.ShowDialog() != true) return;

    HeatmapComputer.ExtractRegion(_stream, _currentOffset, _currentLength, dlg.FileName);
    MessageBox.Show($"Extracted {FormatSize(_currentLength)} to {dlg.FileName}", "Extract", MessageBoxButton.OK, MessageBoxImage.Information);
  }

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
  };
}
