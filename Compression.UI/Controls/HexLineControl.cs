using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;
using WpfColor = System.Windows.Media.Color;

namespace Compression.UI.Controls;

/// <summary>
/// Custom FrameworkElement that renders a hex line with per-byte frequency coloring.
/// </summary>
internal sealed class HexLineControl : FrameworkElement {

  private static readonly Typeface MonoTypeface = new("Cascadia Mono,Consolas,Courier New");
  private const double FontSize = 12;
  private const double Dpi = 96;
  private static readonly double PixelsPerDip = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;

  /// <summary>Frequency percentile lookup (byte value -> 0..255 percentile). Null = no coloring.</summary>
  internal static byte[]? FrequencyPercentiles { get; set; }

  /// <summary>Whether to colorize bytes by frequency. When false, monochrome rendering.</summary>
  internal static bool ColorizeHex { get; set; }

  protected override Size MeasureOverride(Size availableSize) {
    if (DataContext is not HexLineViewModel vm)
      return new Size(0, FontSize + 4);

    var charWidth = MeasureCharWidth();
    // offset + gap + hex bytes + group seps + gap + ascii
    var groups = (vm.BytesPerRow - 1) / 8;
    var width = (vm.OffsetText.Length + 2) * charWidth
                + vm.BytesPerRow * charWidth * 3
                + groups * charWidth
                + charWidth
                + vm.BytesPerRow * charWidth;
    return new Size(width, FontSize + 4);
  }

  protected override void OnRender(DrawingContext dc) {
    if (DataContext is not HexLineViewModel vm) return;

    var charWidth = MeasureCharWidth();
    var lineHeight = FontSize + 4;
    var y = 0.0;

    // Offset in gray
    var offsetFmt = CreateText(vm.OffsetText, Brushes.Gray);
    dc.DrawText(offsetFmt, new Point(0, y));
    var x = (vm.OffsetText.Length + 2) * charWidth;

    // Hex bytes
    for (var i = 0; i < vm.BytesPerRow; i++) {
      if (i < vm.ByteCount) {
        var hb = vm.Bytes[i];

        if (ColorizeHex && FrequencyPercentiles != null) {
          var pct = FrequencyPercentiles[hb.Value];
          var bgColor = PercentileToBackground(pct);
          var bgBrush = new SolidColorBrush(bgColor);
          dc.DrawRectangle(bgBrush, null, new Rect(x, y, charWidth * 2.2, lineHeight));
        }

        var fgBrush = ColorizeHex ? GetCategoryBrush(hb.Value) : Brushes.Black;
        var hexFmt = CreateText(hb.HexText, fgBrush);
        dc.DrawText(hexFmt, new Point(x, y));
        x += charWidth * 3;
      }
      else {
        x += charWidth * 3;
      }

      // Group separator every 8 bytes
      if (i > 0 && i < vm.BytesPerRow - 1 && ((i + 1) % 8) == 0)
        x += charWidth;
    }

    x += charWidth;

    // ASCII column
    for (var i = 0; i < vm.ByteCount; i++) {
      var hb = vm.Bytes[i];

      if (ColorizeHex && FrequencyPercentiles != null) {
        var pct = FrequencyPercentiles[hb.Value];
        var bgColor = PercentileToBackground(pct);
        dc.DrawRectangle(new SolidColorBrush(WpfColor.FromArgb(80, bgColor.R, bgColor.G, bgColor.B)),
          null, new Rect(x, y, charWidth, lineHeight));
      }

      var fgBrush = ColorizeHex ? GetCategoryBrush(hb.Value) : Brushes.Black;
      var ascFmt = CreateText(hb.AsciiChar.ToString(), fgBrush);
      dc.DrawText(ascFmt, new Point(x, y));
      x += charWidth;
    }
  }

  private static FormattedText CreateText(string text, Brush foreground) {
    return new FormattedText(text, CultureInfo.InvariantCulture,
      System.Windows.FlowDirection.LeftToRight, MonoTypeface, FontSize, foreground, PixelsPerDip);
  }

  private static double MeasureCharWidth() {
    var ft = CreateText("0", Brushes.Black);
    return ft.Width;
  }

  private static WpfColor PercentileToBackground(byte percentile) {
    // Green (rare) -> neutral (mid) -> red (common)
    var t = percentile / 255.0;
    if (t < 0.1)
      return WpfColor.FromArgb(40, 76, 175, 80);  // green
    if (t > 0.9)
      return WpfColor.FromArgb(40, 244, 67, 54);   // red
    return WpfColor.FromArgb(10, 158, 158, 158);    // neutral gray
  }

  private static Brush GetCategoryBrush(byte value) {
    if (value < 0x20 || value == 0x7F)
      return new SolidColorBrush(WpfColor.FromRgb(0xFF, 0x98, 0x00)); // orange for control
    if (value >= 0x80)
      return new SolidColorBrush(WpfColor.FromRgb(0x9C, 0x27, 0xB0)); // purple for high
    return Brushes.Black; // default for printable ASCII
  }
}
