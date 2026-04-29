using System.Windows;
using Rectangle = System.Windows.Shapes.Rectangle;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using BrushConverter = System.Windows.Media.BrushConverter;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Compression.UI.Controls;

/// <summary>Simple legend item: colored square + label.</summary>
public sealed class LegendItem : StackPanel {
  public static readonly DependencyProperty ColorProperty =
    DependencyProperty.Register(nameof(Color), typeof(string), typeof(LegendItem), new PropertyMetadata("#000", OnChanged));

  public static readonly DependencyProperty LabelProperty =
    DependencyProperty.Register(nameof(Label), typeof(string), typeof(LegendItem), new PropertyMetadata("", OnChanged));

  public string Color { get => (string)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
  public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

  private readonly Rectangle _rect;
  private readonly TextBlock _text;

  public LegendItem() {
    Orientation = System.Windows.Controls.Orientation.Horizontal;
    Margin = new Thickness(0, 2, 0, 2);
    _rect = new Rectangle { Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0) };
    _text = new TextBlock {
      FontSize = 11,
      VerticalAlignment = VerticalAlignment.Center,
      // Inherit Foreground from parent — HeatmapGridControl picks a
      // contrasting color at Loaded based on host Window's background
      // luminance, so this works on both dark (HeatmapExplorerWindow) and
      // light (AnalysisWindow) host backgrounds.
    };
    Children.Add(_rect);
    Children.Add(_text);
  }

  private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
    if (d is not LegendItem li) return;
    try { li._rect.Fill = new BrushConverter().ConvertFromString(li.Color) as Brush ?? Brushes.Black; } catch { }
    li._text.Text = li.Label;
  }
}
