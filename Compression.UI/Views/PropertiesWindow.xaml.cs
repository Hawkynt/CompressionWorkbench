using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Compression.UI.ViewModels;
using IOPath = System.IO.Path;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfPen = System.Windows.Media.Pen;

namespace Compression.UI.Views;

public partial class PropertiesWindow : Window {
  private double _ratio = -1;

  public PropertiesWindow() {
    InitializeComponent();
  }

  internal void ShowProperties(ArchiveEntryViewModel entry, IReadOnlyList<ArchiveEntryViewModel> allEntries, byte[]? data) {
    Title = $"Properties — {entry.Name}";

    EntryIcon.Text = entry.Icon;
    EntryIcon.Foreground = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(entry.IconColor)!;
    EntryName.Text = entry.Name;
    EntryPath.Text = string.IsNullOrEmpty(entry.Path) ? entry.Name : entry.Path;

    OriginalSizeText.Text = FormatSizeDetailed(entry.OriginalSize);
    CompressedSizeText.Text = entry.CompressedSize >= 0
      ? FormatSizeDetailed(entry.CompressedSize) : "N/A";

    _ratio = entry.OriginalSize > 0 && entry.CompressedSize >= 0
      ? 100.0 * entry.CompressedSize / entry.OriginalSize : -1;

    if (_ratio >= 0) {
      RatioText.Text = $"{_ratio:F1}%";
      var savings = entry.OriginalSize - entry.CompressedSize;
      SavingsText.Text = savings >= 0
        ? $"(saved {FormatSize(savings)})"
        : $"(expanded by {FormatSize(-savings)})";
    }
    else {
      RatioText.Text = "N/A";
      SavingsText.Text = "";
    }

    MethodText.Text = string.IsNullOrEmpty(entry.Method) ? "(none)" : entry.Method;
    ModifiedText.Text = entry.LastModified?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A";
    EncryptedText.Text = entry.IsEncrypted ? "Yes" : "No";
    TypeText.Text = entry.IsDirectory ? "Directory" : GetFileType(entry.Name);

    if (entry.IsDirectory) {
      FolderStatsPanel.Visibility = Visibility.Visible;
      var dirPrefix = entry.Path.EndsWith('/') ? entry.Path : entry.Path + "/";
      var fileCount = 0;
      var subdirs = new HashSet<string>(StringComparer.Ordinal);
      foreach (var e in allEntries) {
        if (!e.Path.StartsWith(dirPrefix, StringComparison.Ordinal)) continue;
        var remainder = e.Path[dirPrefix.Length..];
        if (string.IsNullOrEmpty(remainder)) continue;
        var slash = remainder.IndexOf('/');
        if (slash < 0 && !e.IsDirectory) fileCount++;
        else if (slash >= 0) subdirs.Add(remainder[..slash]);
      }
      FileCountText.Text = fileCount.ToString("N0");
      SubdirCountText.Text = subdirs.Count.ToString("N0");
    }

    if (data is { Length: > 0 }) {
      StatisticsPanel.Visibility = Visibility.Visible;
      PopulateStatistics(data);
      Loaded += (_, _) => DrawHistogram(data);
    }
  }

  // ── Isometric 3D bar — corner view (45° rotated) ─────────────────

  private void OnRatioCanvasSizeChanged(object sender, SizeChangedEventArgs e) {
    DrawIsometricBar();
  }

  private void DrawIsometricBar() {
    RatioCanvas.Children.Clear();
    var w = RatioCanvas.ActualWidth;
    var h = RatioCanvas.ActualHeight;
    if (w <= 0 || h <= 0) return;

    // Isometric corner view: the viewer looks straight at the front vertical edge.
    // Two symmetric faces recede at equal angles. The front edge is the vertical
    // center line; left face goes left-back, right face goes right-back.
    var cx = w / 2.0;           // center x — the front edge
    var isoDepth = w * 0.45;    // how far back each face recedes horizontally
    var topOff = h * 0.06;      // vertical offset for top face (isometric rise)
    var bodyH = h - topOff;     // front face height

    // 8 corners of the box:
    // Front edge (bottom and top)
    var fBot = new WpfPoint(cx, h);
    var fTop = new WpfPoint(cx, topOff);
    // Left-back (bottom and top)
    var lBot = new WpfPoint(cx - isoDepth, h - topOff * 0.4);
    var lTop = new WpfPoint(cx - isoDepth, 0);
    // Right-back (bottom and top)
    var rBot = new WpfPoint(cx + isoDepth, h - topOff * 0.4);
    var rTop = new WpfPoint(cx + isoDepth, 0);
    // Back corner (top only — visible through glass top)
    var bTop = new WpfPoint(cx, topOff * 0.6 - topOff);

    // Fill level
    var clamped = _ratio >= 0 ? Math.Clamp(_ratio, 0, 100) / 100.0 : 0;

    if (clamped > 0) {
      // Interpolate fill line positions
      var fFillY = h - bodyH * clamped;
      var lFillY = lBot.Y - (lBot.Y - lTop.Y) * clamped;
      var rFillY = rBot.Y - (rBot.Y - rTop.Y) * clamped;

      var fFill = new WpfPoint(cx, fFillY);
      var lFill = new WpfPoint(cx - isoDepth, lFillY);
      var rFill = new WpfPoint(cx + isoDepth, rFillY);

      // Left face fill
      DrawPolygon(RatioCanvas, [fFill, lFill, lBot, fBot],
        WpfColor.FromArgb(0xAA, 0x1E, 0x90, 0xFF), null);

      // Right face fill (slightly darker)
      DrawPolygon(RatioCanvas, [fFill, rFill, rBot, fBot],
        WpfColor.FromArgb(0xAA, 0x14, 0x6E, 0xCC), null);

      // Top liquid surface (diamond)
      DrawPolygon(RatioCanvas, [
        fFill,
        lFill,
        new WpfPoint(cx, lFillY - (fFillY - lFillY)),  // back midpoint
        rFill,
      ], WpfColor.FromArgb(0x77, 0x64, 0xB5, 0xF6), null);
    }

    // Wireframe edges
    var edgePen = new WpfPen(new SolidColorBrush(WpfColor.FromRgb(0x55, 0x55, 0x55)), 1.3);

    // Bottom edges
    DrawLine(RatioCanvas, fBot, lBot, edgePen);
    DrawLine(RatioCanvas, fBot, rBot, edgePen);

    // Front vertical edge
    DrawLine(RatioCanvas, fBot, fTop, edgePen);

    // Left face back vertical + bottom
    DrawLine(RatioCanvas, lBot, lTop, edgePen);

    // Right face back vertical + bottom
    DrawLine(RatioCanvas, rBot, rTop, edgePen);

    // Top edges
    DrawLine(RatioCanvas, fTop, lTop, edgePen);
    DrawLine(RatioCanvas, fTop, rTop, edgePen);
    DrawLine(RatioCanvas, lTop, rTop, edgePen);
  }

  private static void DrawPolygon(Canvas canvas, WpfPoint[] points, WpfColor fill, WpfColor? stroke) {
    var polygon = new System.Windows.Shapes.Polygon {
      Points = new PointCollection(points),
      Fill = new SolidColorBrush(fill),
      Stroke = stroke.HasValue ? new SolidColorBrush(stroke.Value) : null,
      StrokeThickness = stroke.HasValue ? 1 : 0,
    };
    canvas.Children.Add(polygon);
  }

  private static void DrawLine(Canvas canvas, WpfPoint p1, WpfPoint p2, WpfPen pen) {
    var line = new System.Windows.Shapes.Line {
      X1 = p1.X, Y1 = p1.Y,
      X2 = p2.X, Y2 = p2.Y,
      Stroke = pen.Brush,
      StrokeThickness = pen.Thickness,
    };
    canvas.Children.Add(line);
  }

  // ── Statistics ─────────────────────────────────────────────────────

  private void PopulateStatistics(byte[] data) {
    var n = data.Length;
    var freq = new long[256];
    long sum = 0;
    foreach (var b in data) {
      freq[b]++;
      sum += b;
    }

    // --- Randomness tests ---

    var entropy = ComputeEntropy(freq, n);
    EntropyText.Text = $"{entropy:F4} bits/byte  (max 8.0000)";

    var mean = (double)sum / n;
    MeanText.Text = $"{mean:F4}  (random = 127.5)";

    var expected = (double)n / 256.0;
    var chiSq = 0.0;
    foreach (var f in freq) {
      var diff = f - expected;
      chiSq += diff * diff / expected;
    }
    var k = 255.0;
    var z = Math.Pow(chiSq / k, 1.0 / 3.0) - (1.0 - 2.0 / (9.0 * k));
    z /= Math.Sqrt(2.0 / (9.0 * k));
    var pValue = 0.5 * Erfc(z / Math.Sqrt(2.0));
    var chiVerdict = pValue < 0.01 ? "NOT random" : pValue < 0.05 ? "possibly not random" : "consistent with random";
    ChiSquareText.Text = $"{chiSq:F2}  (p = {pValue:F6}, {chiVerdict})";

    double scNum = 0, scDenA = 0, scDenB = 0;
    for (var i = 0; i < n - 1; i++) {
      var a = data[i] - mean;
      var b = data[i + 1] - mean;
      scNum += a * b;
      scDenA += a * a;
      scDenB += b * b;
    }
    {
      var a = data[n - 1] - mean;
      var bv = data[0] - mean;
      scNum += a * bv;
      scDenA += a * a;
      scDenB += bv * bv;
    }
    var denom = Math.Sqrt(scDenA * scDenB);
    var serialCorr = denom > 0 ? scNum / denom : 0;
    var scVerdict = Math.Abs(serialCorr) < 0.05 ? "no correlation" : Math.Abs(serialCorr) < 0.2 ? "weak" : "correlated";
    SerialCorrelationText.Text = $"{serialCorr:F6}  ({scVerdict})";

    if (n >= 6) {
      long inside = 0;
      var pairs = n / 2;
      for (var i = 0; i + 1 < n; i += 2) {
        var x = data[i] - 127.5;
        var y = data[i + 1] - 127.5;
        if (x * x + y * y <= 127.5 * 127.5) inside++;
      }
      var piEst = 4.0 * inside / pairs;
      var piPct = 100.0 * Math.Abs(piEst - Math.PI) / Math.PI;
      MonteCarloPiText.Text = $"{piEst:F6}  (error {piPct:F2}%, {(piPct < 1 ? "good" : piPct < 5 ? "fair" : "poor")})";
    }
    else {
      MonteCarloPiText.Text = "N/A (too few bytes)";
    }

    // --- Byte distribution ---

    var unique = freq.Count(f => f > 0);
    UniqueBytesText.Text = $"{unique} / 256  ({100.0 * unique / 256:F1}%)";

    var maxIdx = 0;
    for (var i = 1; i < 256; i++)
      if (freq[i] > freq[maxIdx]) maxIdx = i;
    MostCommonText.Text = $"0x{maxIdx:X2} ({FormatByteChar(maxIdx)}) — {freq[maxIdx]:N0} ({100.0 * freq[maxIdx] / n:F1}%)";

    var minIdx = -1;
    for (var i = 0; i < 256; i++)
      if (freq[i] > 0 && (minIdx < 0 || freq[i] < freq[minIdx])) minIdx = i;
    LeastCommonText.Text = minIdx >= 0
      ? $"0x{minIdx:X2} ({FormatByteChar(minIdx)}) — {freq[minIdx]:N0} ({100.0 * freq[minIdx] / n:F1}%)"
      : "N/A";

    var idealBytes = (long)Math.Ceiling(ComputeEntropy(freq, n) * n / 8.0);
    IdealSizeText.Text = $"≥ {FormatSize(idealBytes)} (Shannon limit)";

    string assessment;
    WpfColor assessColor;
    if (entropy > 7.9 && pValue > 0.01 && Math.Abs(serialCorr) < 0.05) {
      assessment = "Highly random / encrypted / already compressed";
      assessColor = WpfColor.FromRgb(0xF4, 0x43, 0x36);
    }
    else if (entropy > 7.0) {
      assessment = "High entropy — limited compressibility";
      assessColor = WpfColor.FromRgb(0xFF, 0x98, 0x00);
    }
    else if (entropy > 5.0) {
      assessment = "Moderate entropy — good compressibility";
      assessColor = WpfColor.FromRgb(0x33, 0x99, 0xFF);
    }
    else {
      assessment = "Low entropy — excellent compressibility";
      assessColor = WpfColor.FromRgb(0x4C, 0xAF, 0x50);
    }
    AssessmentText.Text = assessment;
    AssessmentText.Foreground = new SolidColorBrush(assessColor);

    // --- Content analysis ---

    long printable = 0, control = 0, high = 0, nullBytes = freq[0];
    for (var i = 1; i < 0x20; i++) control += freq[i];
    for (var i = 0x20; i < 0x7F; i++) printable += freq[i];
    control += freq[0x7F]; // DEL
    for (var i = 0x80; i < 256; i++) high += freq[i];

    PrintableAsciiText.Text = $"{printable:N0}  ({100.0 * printable / n:F1}%)";
    ControlBytesText.Text = $"{control:N0}  ({100.0 * control / n:F1}%)";
    HighBytesText.Text = $"{high:N0}  ({100.0 * high / n:F1}%)";
    NullBytesText.Text = $"{nullBytes:N0}  ({100.0 * nullBytes / n:F1}%)";

    // N-gram unique counts (sample up to 256K for performance)
    var sampleLen = Math.Min(n, 256 * 1024);
    var bigrams = new HashSet<int>();
    var trigrams = new HashSet<long>();
    var quadgrams = new HashSet<long>();
    for (var i = 0; i < sampleLen - 1; i++) {
      bigrams.Add(data[i] << 8 | data[i + 1]);
      if (i < sampleLen - 2)
        trigrams.Add((long)data[i] << 16 | (long)data[i + 1] << 8 | data[i + 2]);
      if (i < sampleLen - 3)
        quadgrams.Add((long)data[i] << 24 | (long)data[i + 1] << 16 | (long)data[i + 2] << 8 | data[i + 3]);
    }
    var sampled = sampleLen < n ? $"  (sampled {sampleLen / 1024}K)" : "";
    Bigram2Text.Text = $"{bigrams.Count:N0} / 65,536  ({100.0 * bigrams.Count / 65536:F1}%){sampled}";
    Bigram3Text.Text = $"{trigrams.Count:N0} / 16,777,216  ({100.0 * trigrams.Count / 16777216:F2}%){sampled}";
    Bigram4Text.Text = $"{quadgrams.Count:N0}{sampled}";
  }

  private static double Erfc(double x) {
    if (x < 0) return 2.0 - Erfc(-x);
    const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741, a4 = -1.453152027, a5 = 1.061405429;
    var t = 1.0 / (1.0 + 0.3275911 * x);
    return t * (a1 + t * (a2 + t * (a3 + t * (a4 + t * a5)))) * Math.Exp(-x * x);
  }

  // ── Histogram with background bands and tooltips ───────────────────

  private void DrawHistogram(byte[] data) {
    HistogramCanvas.Children.Clear();

    var canvas = HistogramCanvas;
    var width = canvas.ActualWidth;
    var height = canvas.ActualHeight;
    if (width <= 0 || height <= 0) return;

    var freq = new long[256];
    foreach (var b in data) freq[b]++;

    var maxFreq = freq.Max();
    if (maxFreq == 0) return;
    var logMax = Math.Log(maxFreq + 1);
    var barWidth = width / 256.0;

    // Background bands: Control (0x00-0x1F), Printable (0x20-0x7E), High (0x7F-0xFF)
    // Control band
    var ctrlRect = new System.Windows.Shapes.Rectangle {
      Width = barWidth * 32,
      Height = height,
      Fill = new SolidColorBrush(WpfColor.FromArgb(0x30, 0xFF, 0x98, 0x00)),
    };
    Canvas.SetLeft(ctrlRect, 0);
    Canvas.SetTop(ctrlRect, 0);
    canvas.Children.Add(ctrlRect);

    // Printable ASCII band
    var asciiRect = new System.Windows.Shapes.Rectangle {
      Width = barWidth * 95, // 0x20 to 0x7E inclusive
      Height = height,
      Fill = new SolidColorBrush(WpfColor.FromArgb(0x18, 0x33, 0x99, 0xFF)),
    };
    Canvas.SetLeft(asciiRect, barWidth * 32);
    Canvas.SetTop(asciiRect, 0);
    canvas.Children.Add(asciiRect);

    // High byte band
    var highRect = new System.Windows.Shapes.Rectangle {
      Width = barWidth * 129, // 0x7F to 0xFF inclusive
      Height = height,
      Fill = new SolidColorBrush(WpfColor.FromArgb(0x20, 0x9C, 0x27, 0xB0)),
    };
    Canvas.SetLeft(highRect, barWidth * 127);
    Canvas.SetTop(highRect, 0);
    canvas.Children.Add(highRect);

    // Find most and least common (non-zero) indices
    var mostIdx = 0;
    var leastIdx = -1;
    for (var i = 0; i < 256; i++) {
      if (freq[i] > freq[mostIdx]) mostIdx = i;
      if (freq[i] > 0 && (leastIdx < 0 || freq[i] < freq[leastIdx])) leastIdx = i;
    }

    // Draw bars with tooltips
    for (var i = 0; i < 256; i++) {
      if (freq[i] == 0) continue;

      var logVal = Math.Log(freq[i] + 1);
      var barHeight = (logVal / logMax) * (height - 2);

      var brush = i == mostIdx ? new SolidColorBrush(WpfColor.FromRgb(0xE0, 0x40, 0x40))
        : i == leastIdx ? new SolidColorBrush(WpfColor.FromRgb(0x4C, 0xAF, 0x50))
        : GetHistogramBrush(i);

      var rect = new System.Windows.Shapes.Rectangle {
        Width = Math.Max(barWidth - 0.3, 0.5),
        Height = Math.Max(barHeight, 1),
        Fill = brush,
      };

      // Tooltip
      var tt = new System.Windows.Controls.ToolTip { Content = FormatHistogramTooltip(i, freq[i], data.Length, i == mostIdx, i == leastIdx) };
      ToolTipService.SetToolTip(rect, tt);
      ToolTipService.SetShowDuration(rect, 30000);
      ToolTipService.SetInitialShowDelay(rect, 100);

      Canvas.SetLeft(rect, i * barWidth);
      Canvas.SetTop(rect, height - barHeight);
      canvas.Children.Add(rect);
    }

    // Add invisible hit-test rectangles for zero-frequency bytes (so tooltip still works)
    for (var i = 0; i < 256; i++) {
      if (freq[i] != 0) continue;
      var rect = new System.Windows.Shapes.Rectangle {
        Width = Math.Max(barWidth - 0.3, 0.5),
        Height = height,
        Fill = System.Windows.Media.Brushes.Transparent,
      };
      var tt = new System.Windows.Controls.ToolTip { Content = $"Byte 0x{i:X2} ({i}) — {FormatByteChar(i)}\nCount: 0\nNot present in data" };
      ToolTipService.SetToolTip(rect, tt);
      ToolTipService.SetInitialShowDelay(rect, 100);
      Canvas.SetLeft(rect, i * barWidth);
      Canvas.SetTop(rect, 0);
      canvas.Children.Add(rect);
    }
  }

  private static string FormatHistogramTooltip(int byteValue, long count, int total, bool isMost, bool isLeast) {
    var pct = 100.0 * count / total;
    var label = isMost ? "  [MOST COMMON]" : isLeast ? "  [LEAST COMMON]" : "";
    var charRepr = FormatByteChar(byteValue);
    var category = byteValue == 0 ? "Null"
      : byteValue < 0x20 || byteValue == 0x7F ? "Control"
      : byteValue < 0x7F ? "Printable ASCII"
      : "High byte";

    return $"Byte 0x{byteValue:X2} ({byteValue}) — {charRepr}{label}\n" +
           $"Count: {count:N0} / {total:N0}\n" +
           $"Frequency: {pct:F4}%\n" +
           $"Category: {category}\n" +
           $"Expected (uniform): {100.0 / 256:F4}%\n" +
           $"Deviation: {(pct - 100.0 / 256):+0.0000;-0.0000;0.0000}%";
  }

  private static System.Windows.Media.Brush GetHistogramBrush(int byteValue) {
    if (byteValue < 0x20 || byteValue == 0x7F) return new SolidColorBrush(WpfColor.FromRgb(0xFF, 0x98, 0x00));
    if (byteValue < 0x7F) return new SolidColorBrush(WpfColor.FromRgb(0x33, 0x99, 0xFF));
    return new SolidColorBrush(WpfColor.FromRgb(0x9C, 0x27, 0xB0));
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static double ComputeEntropy(long[] freq, int total) {
    var entropy = 0.0;
    foreach (var f in freq) {
      if (f <= 0) continue;
      var p = (double)f / total;
      entropy -= p * Math.Log2(p);
    }
    return entropy;
  }

  private static string FormatByteChar(int b) => b switch {
    0x00 => "NUL", 0x01 => "SOH", 0x02 => "STX", 0x03 => "ETX",
    0x07 => "BEL", 0x08 => "BS", 0x09 => "TAB", 0x0A => "LF",
    0x0B => "VT", 0x0C => "FF", 0x0D => "CR", 0x1B => "ESC",
    0x20 => "SPACE", 0x7F => "DEL",
    >= 0x21 and < 0x7F => $"'{(char)b}'",
    _ => $"0x{b:X2}",
  };

  private static string FormatSizeDetailed(long bytes) => bytes switch {
    < 1024 => $"{bytes:N0} bytes",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB ({bytes:N0} bytes)",
    < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB ({bytes:N0} bytes)",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB ({bytes:N0} bytes)",
  };

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes:N0} bytes",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };

  private static string GetFileType(string name) {
    var ext = IOPath.GetExtension(name).ToLowerInvariant();
    return ext switch {
      ".txt" or ".log" or ".csv" => "Text file",
      ".xml" or ".html" or ".htm" or ".xaml" => "Markup file",
      ".json" or ".yaml" or ".yml" or ".toml" => "Config file",
      ".cs" or ".java" or ".cpp" or ".c" or ".h" or ".py" or ".js" or ".ts" => "Source code",
      ".exe" or ".dll" or ".sys" => "Executable",
      ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".webp" => "Image",
      ".mp3" or ".wav" or ".flac" or ".ogg" or ".aac" => "Audio",
      ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" => "Video",
      ".pdf" => "PDF document",
      ".doc" or ".docx" => "Word document",
      ".xls" or ".xlsx" => "Spreadsheet",
      ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "Archive",
      "" => "File",
      _ => $"{ext.TrimStart('.').ToUpperInvariant()} file",
    };
  }

  private void OnOk(object sender, RoutedEventArgs e) => Close();
}
