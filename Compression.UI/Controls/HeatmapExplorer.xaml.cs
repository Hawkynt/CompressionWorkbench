using System.Windows;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WpfColor = System.Windows.Media.Color;
using Compression.Analysis.Visualization;
using Compression.UI.ViewModels;
using WpfToolTip = System.Windows.Controls.ToolTip;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace Compression.UI.Controls;

public partial class HeatmapExplorer : System.Windows.Controls.UserControl {
  private readonly WpfRectangle[] _cells = new WpfRectangle[256];
  private readonly WpfToolTip[] _tips = new WpfToolTip[256];

  public HeatmapExplorer() {
    InitializeComponent();

    // Create the 256 rectangles.
    for (var i = 0; i < 256; i++) {
      var tip = new WpfToolTip();
      _tips[i] = tip;
      var rect = new WpfRectangle {
        Fill = System.Windows.Media.Brushes.Black,
        Stroke = new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(40, 40, 40)),
        StrokeThickness = 0.5,
        ToolTip = tip,
        Tag = i,
        Cursor = System.Windows.Input.Cursors.Hand
      };
      rect.MouseLeftButtonDown += OnCellClick;
      rect.MouseEnter += OnCellHover;
      _cells[i] = rect;
      CellGrid.Children.Add(rect);
    }

    if (DataContext is HeatmapExplorerViewModel vm)
      vm.LevelChanged += OnLevelChanged;

    DataContextChanged += (_, _) => {
      if (DataContext is HeatmapExplorerViewModel newVm)
        newVm.LevelChanged += OnLevelChanged;
    };
  }

  private void OnLevelChanged(HierarchicalBlockMap.LevelResult? level) {
    if (level == null) {
      for (var i = 0; i < 256; i++)
        _cells[i].Fill = System.Windows.Media.Brushes.Black;
      return;
    }

    for (var i = 0; i < 256; i++) {
      var block = level.Blocks[i];
      _cells[i].Fill = GetBlockBrush(block);
      _tips[i].Content = FormatBlockTip(block);
    }
  }

  private void OnCellClick(object sender, WpfMouseButtonEventArgs e) {
    if (sender is not WpfRectangle { Tag: int index }) return;
    if (DataContext is HeatmapExplorerViewModel vm)
      vm.DrillInto(index);
  }

  private void OnCellHover(object sender, WpfMouseEventArgs e) {
    if (sender is not WpfRectangle { Tag: int index }) return;
    if (DataContext is HeatmapExplorerViewModel vm)
      vm.SelectBlock(index);
  }

  private static System.Windows.Media.Brush GetBlockBrush(HierarchicalBlockMap.BlockInfo block) {
    // Use entropy-based gradient within each type for nuance.
    return block.Type switch {
      HierarchicalBlockMap.BlockType.Empty => new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(25, 25, 25)),
      HierarchicalBlockMap.BlockType.HasSignature => new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(233, 30, 99)),
      HierarchicalBlockMap.BlockType.LowEntropy => LerpBrush(
        WpfColor.FromRgb(13, 71, 161), WpfColor.FromRgb(33, 150, 243), block.Entropy / 3.0),
      HierarchicalBlockMap.BlockType.Structured => LerpBrush(
        WpfColor.FromRgb(27, 94, 32), WpfColor.FromRgb(76, 175, 80), (block.Entropy - 3.0) / 2.5),
      HierarchicalBlockMap.BlockType.Compressed => LerpBrush(
        WpfColor.FromRgb(230, 81, 0), WpfColor.FromRgb(255, 152, 0), (block.Entropy - 5.5) / 2.0),
      HierarchicalBlockMap.BlockType.Random => LerpBrush(
        WpfColor.FromRgb(183, 28, 28), WpfColor.FromRgb(244, 67, 54), (block.Entropy - 7.5) / 0.5),
      _ => System.Windows.Media.Brushes.Gray
    };
  }

  private static System.Windows.Media.SolidColorBrush LerpBrush(WpfColor a, WpfColor b, double t) {
    t = Math.Clamp(t, 0, 1);
    return new System.Windows.Media.SolidColorBrush(WpfColor.FromRgb(
      (byte)(a.R + (b.R - a.R) * t),
      (byte)(a.G + (b.G - a.G) * t),
      (byte)(a.B + (b.B - a.B) * t)));
  }

  private static string FormatBlockTip(HierarchicalBlockMap.BlockInfo block) {
    var size = block.Size switch {
      < 1024 => $"{block.Size} B",
      < 1024 * 1024 => $"{block.Size / 1024.0:F1} KB",
      < 1024L * 1024 * 1024 => $"{block.Size / (1024.0 * 1024):F1} MB",
      _ => $"{block.Size / (1024.0 * 1024 * 1024):F2} GB"
    };
    var sig = block.SignatureName != null ? $"\nSignature: {block.SignatureName}" : "";
    return $"Offset: 0x{block.Offset:X} ({size})\nEntropy: {block.Entropy:F2}\nType: {block.Type}\nUnique bytes: {block.UniqueBytes}/256{sig}\nClick to drill in";
  }
}
