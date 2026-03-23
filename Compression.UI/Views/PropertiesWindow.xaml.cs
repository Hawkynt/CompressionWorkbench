using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Compression.UI.Controls;
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

    EntryIcon.Source = entry.IconImage;
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
      StatsControl.Visibility = Visibility.Visible;
      StatsControl.Data = data;
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

  // ── Helpers ────────────────────────────────────────────────────────

  private static string FormatSizeDetailed(long bytes) => StatisticsControl.FormatSizeDetailed(bytes);

  private static string FormatSize(long bytes) => StatisticsControl.FormatSize(bytes);

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
