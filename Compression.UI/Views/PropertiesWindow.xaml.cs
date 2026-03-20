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

    // Icon + name
    EntryIcon.Text = entry.Icon;
    EntryIcon.Foreground = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(entry.IconColor)!;
    EntryName.Text = entry.Name;
    EntryPath.Text = string.IsNullOrEmpty(entry.Path) ? entry.Name : entry.Path;

    // General fields
    OriginalSizeText.Text = FormatSizeDetailed(entry.OriginalSize);
    CompressedSizeText.Text = entry.CompressedSize >= 0
      ? FormatSizeDetailed(entry.CompressedSize)
      : "N/A";

    _ratio = entry.OriginalSize > 0 && entry.CompressedSize >= 0
      ? 100.0 * entry.CompressedSize / entry.OriginalSize
      : -1;

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

    // Folder statistics
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
        if (slash < 0 && !e.IsDirectory)
          fileCount++;
        else if (slash >= 0)
          subdirs.Add(remainder[..slash]);
      }
      FileCountText.Text = fileCount.ToString("N0");
      SubdirCountText.Text = subdirs.Count.ToString("N0");
    }

    // Data statistics (only for files with extracted data)
    if (data is { Length: > 0 }) {
      StatisticsPanel.Visibility = Visibility.Visible;
      PopulateStatistics(data);
      Loaded += (_, _) => DrawHistogram(data);
    }
  }

  // ── Isometric 3D bar ──────────────────────────────────────────────

  private void OnRatioCanvasSizeChanged(object sender, SizeChangedEventArgs e) {
    DrawIsometricBar();
  }

  private void DrawIsometricBar() {
    RatioCanvas.Children.Clear();
    var w = RatioCanvas.ActualWidth;
    var h = RatioCanvas.ActualHeight;
    if (w <= 0 || h <= 0) return;

    // Isometric parameters — viewer looks at front edge
    var depth = w * 0.35;       // isometric depth offset
    var bodyW = w - depth;      // front face width
    var bodyH = h - depth;      // front face height
    var ox = 0.0;               // front-left x
    var oy = depth;             // front-top y

    // Glass container outline: front face + top face + right face
    // Front face: rectangle at (ox, oy) size (bodyW x bodyH)
    // Top face: parallelogram connecting front-top to back-top
    // Right face: parallelogram connecting front-right to back-right

    var frontTL = new WpfPoint(ox, oy);
    var frontTR = new WpfPoint(ox + bodyW, oy);
    var frontBL = new WpfPoint(ox, oy + bodyH);
    var frontBR = new WpfPoint(ox + bodyW, oy + bodyH);
    var backTL = new WpfPoint(ox + depth, 0);
    var backTR = new WpfPoint(ox + bodyW + depth, 0);
    var backBR = new WpfPoint(ox + bodyW + depth, bodyH);

    // Fill level
    var clamped = _ratio >= 0 ? Math.Clamp(_ratio, 0, 100) / 100.0 : 0;
    var fillH = bodyH * clamped;

    // Draw filled liquid if ratio > 0
    if (clamped > 0) {
      var fillTop = oy + bodyH - fillH;
      var fillBackTop = bodyH - fillH;

      // Front face fill
      DrawPolygon(RatioCanvas, [
        new WpfPoint(ox, fillTop),
        new WpfPoint(ox + bodyW, fillTop),
        frontBR,
        frontBL,
      ], WpfColor.FromArgb(0xAA, 0x1E, 0x90, 0xFF), null); // DodgerBlue

      // Right face fill (darker shade)
      DrawPolygon(RatioCanvas, [
        new WpfPoint(ox + bodyW, fillTop),
        new WpfPoint(ox + bodyW + depth, fillBackTop),
        backBR,
        frontBR,
      ], WpfColor.FromArgb(0xAA, 0x14, 0x6E, 0xCC), null);

      // Top surface of liquid
      DrawPolygon(RatioCanvas, [
        new WpfPoint(ox, fillTop),
        new WpfPoint(ox + depth, fillBackTop),
        new WpfPoint(ox + bodyW + depth, fillBackTop),
        new WpfPoint(ox + bodyW, fillTop),
      ], WpfColor.FromArgb(0x88, 0x52, 0xB0, 0xFF), null); // lighter blue top
    }

    // Glass wireframe edges
    var edgeBrush = new SolidColorBrush(WpfColor.FromRgb(0x55, 0x55, 0x55));
    var edgePen = new WpfPen(edgeBrush, 1.2);

    // Front face outline
    DrawLine(RatioCanvas, frontTL, frontTR, edgePen);
    DrawLine(RatioCanvas, frontTR, frontBR, edgePen);
    DrawLine(RatioCanvas, frontBR, frontBL, edgePen);
    DrawLine(RatioCanvas, frontBL, frontTL, edgePen);

    // Top face
    DrawLine(RatioCanvas, frontTL, backTL, edgePen);
    DrawLine(RatioCanvas, backTL, backTR, edgePen);
    DrawLine(RatioCanvas, backTR, frontTR, edgePen);

    // Right face
    DrawLine(RatioCanvas, frontBR, backBR, edgePen);
    DrawLine(RatioCanvas, backBR, backTR, edgePen);
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

    // Entropy (Shannon)
    var entropy = ComputeEntropy(freq, n);
    EntropyText.Text = $"{entropy:F4} bits/byte  (max 8.0000)";

    // Arithmetic mean
    var mean = (double)sum / n;
    MeanText.Text = $"{mean:F4}  (random = 127.5)";

    // Chi-square test
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

    // Serial correlation coefficient
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
    var scVerdict = Math.Abs(serialCorr) < 0.05 ? "no correlation" : Math.Abs(serialCorr) < 0.2 ? "weak correlation" : "correlated";
    SerialCorrelationText.Text = $"{serialCorr:F6}  ({scVerdict})";

    // Monte Carlo Pi estimation
    if (n >= 6) {
      long inside = 0;
      var pairs = n / 2;
      for (var i = 0; i + 1 < n; i += 2) {
        var x = data[i] - 127.5;
        var y = data[i + 1] - 127.5;
        if (x * x + y * y <= 127.5 * 127.5)
          inside++;
      }
      var piEst = 4.0 * inside / pairs;
      var piErr = Math.Abs(piEst - Math.PI);
      var piPct = 100.0 * piErr / Math.PI;
      MonteCarloPiText.Text = $"{piEst:F6}  (error {piPct:F2}%, {(piPct < 1 ? "good" : piPct < 5 ? "fair" : "poor")})";
    }
    else {
      MonteCarloPiText.Text = "N/A (too few bytes)";
    }

    // Unique byte values
    var unique = freq.Count(f => f > 0);
    UniqueBytesText.Text = $"{unique} / 256  ({100.0 * unique / 256:F1}%)";

    // Most common byte
    var maxIdx = 0;
    for (var i = 1; i < 256; i++)
      if (freq[i] > freq[maxIdx]) maxIdx = i;
    var maxPct = 100.0 * freq[maxIdx] / n;
    MostCommonText.Text = $"0x{maxIdx:X2} ({FormatByteChar(maxIdx)}) — {freq[maxIdx]:N0} ({maxPct:F1}%)";

    // Least common non-zero byte
    var minIdx = -1;
    for (var i = 0; i < 256; i++) {
      if (freq[i] > 0 && (minIdx < 0 || freq[i] < freq[minIdx]))
        minIdx = i;
    }
    if (minIdx >= 0) {
      var minPct = 100.0 * freq[minIdx] / n;
      LeastCommonText.Text = $"0x{minIdx:X2} ({FormatByteChar(minIdx)}) — {freq[minIdx]:N0} ({minPct:F1}%)";
    }
    else {
      LeastCommonText.Text = "N/A";
    }

    // Ideal compressed size (Shannon limit)
    var idealBits = entropy * n;
    var idealBytes = (long)Math.Ceiling(idealBits / 8.0);
    IdealSizeText.Text = $"≥ {FormatSize(idealBytes)} (Shannon limit)";

    // Overall assessment
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
  }

  private static double Erfc(double x) {
    if (x < 0) return 2.0 - Erfc(-x);
    const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741, a4 = -1.453152027, a5 = 1.061405429;
    var t = 1.0 / (1.0 + 0.3275911 * x);
    var poly = t * (a1 + t * (a2 + t * (a3 + t * (a4 + t * a5))));
    return poly * Math.Exp(-x * x);
  }

  // ── Histogram ──────────────────────────────────────────────────────

  private void DrawHistogram(byte[] data) {
    HistogramCanvas.Children.Clear();

    var canvas = HistogramCanvas;
    var width = canvas.ActualWidth;
    var height = canvas.ActualHeight;
    if (width <= 0 || height <= 0) return;

    var freq = new long[256];
    foreach (var b in data)
      freq[b]++;

    var maxFreq = freq.Max();
    if (maxFreq == 0) return;
    var logMax = Math.Log(maxFreq + 1);
    var barWidth = width / 256.0;

    // Find most and least common (non-zero) indices
    var mostIdx = 0;
    var leastIdx = -1;
    for (var i = 0; i < 256; i++) {
      if (freq[i] > freq[mostIdx]) mostIdx = i;
      if (freq[i] > 0 && (leastIdx < 0 || freq[i] < freq[leastIdx])) leastIdx = i;
    }

    for (var i = 0; i < 256; i++) {
      if (freq[i] == 0) continue;

      var logVal = Math.Log(freq[i] + 1);
      var barHeight = (logVal / logMax) * (height - 2);

      var brush = i == mostIdx ? new SolidColorBrush(WpfColor.FromRgb(0xF4, 0x43, 0x36))  // Red — most common
        : i == leastIdx ? new SolidColorBrush(WpfColor.FromRgb(0x4C, 0xAF, 0x50))          // Green — least common
        : GetHistogramBrush(i);

      var rect = new System.Windows.Shapes.Rectangle {
        Width = Math.Max(barWidth - 0.3, 0.5),
        Height = Math.Max(barHeight, 1),
        Fill = brush,
        ToolTip = FormatHistogramTooltip(i, freq[i], data.Length, i == mostIdx, i == leastIdx),
      };

      Canvas.SetLeft(rect, i * barWidth);
      Canvas.SetTop(rect, height - barHeight);
      canvas.Children.Add(rect);
    }
  }

  private static object FormatHistogramTooltip(int byteValue, long count, int total, bool isMost, bool isLeast) {
    var pct = 100.0 * count / total;
    var label = isMost ? "  [MOST COMMON]" : isLeast ? "  [LEAST COMMON]" : "";
    var charRepr = FormatByteChar(byteValue);
    var category = byteValue == 0 ? "Null" : byteValue < 0x20 ? "Control" : byteValue < 0x7F ? "Printable ASCII" : "High byte";

    return $"Byte 0x{byteValue:X2} ({byteValue}) — {charRepr}{label}\n" +
           $"Count: {count:N0} / {total:N0}\n" +
           $"Frequency: {pct:F3}%\n" +
           $"Category: {category}\n" +
           $"Expected (uniform): {100.0 / 256:F3}%";
  }

  private static System.Windows.Media.Brush GetHistogramBrush(int byteValue) {
    if (byteValue == 0) return new SolidColorBrush(WpfColor.FromRgb(0xE0, 0x60, 0x60));
    if (byteValue < 0x20) return new SolidColorBrush(WpfColor.FromRgb(0xFF, 0x98, 0x00));
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

  private static string FormatByteChar(int b) {
    if (b == 0x00) return "NUL";
    if (b == 0x09) return "TAB";
    if (b == 0x0A) return "LF";
    if (b == 0x0D) return "CR";
    if (b == 0x20) return "SPACE";
    if (b >= 0x21 && b < 0x7F) return $"'{(char)b}'";
    return $"0x{b:X2}";
  }

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
