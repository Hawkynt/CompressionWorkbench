using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Compression.UI.ViewModels;
using IOPath = System.IO.Path;

namespace Compression.UI.Views;

public partial class PropertiesWindow : Window {
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

    var ratio = entry.OriginalSize > 0 && entry.CompressedSize >= 0
      ? 100.0 * entry.CompressedSize / entry.OriginalSize
      : -1;

    if (ratio >= 0) {
      RatioText.Text = $"{ratio:F1}%";
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

    // Ratio bar (left side)
    SetRatioBar(ratio);

    // Horizontal ratio visual bar
    SetRatioVisualBar(ratio);

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

      var freq = new long[256];
      foreach (var b in data)
        freq[b]++;

      // Entropy
      var entropy = ComputeEntropy(freq, data.Length);
      EntropyText.Text = $"{entropy:F4} bits/byte";

      // Unique byte values
      var unique = freq.Count(f => f > 0);
      UniqueBytesText.Text = $"{unique} / 256";

      // Most common byte
      var maxIdx = 0;
      for (var i = 1; i < 256; i++)
        if (freq[i] > freq[maxIdx]) maxIdx = i;
      var pct = 100.0 * freq[maxIdx] / data.Length;
      MostCommonText.Text = $"0x{maxIdx:X2} ({FormatByteChar(maxIdx)}) — {freq[maxIdx]:N0} times ({pct:F1}%)";

      // Ideal compressed size (Shannon entropy lower bound)
      var idealBits = entropy * data.Length;
      var idealBytes = (long)Math.Ceiling(idealBits / 8.0);
      IdealSizeText.Text = $"≥ {FormatSize(idealBytes)} (Shannon limit)";

      // Draw histogram
      Loaded += (_, _) => DrawHistogram(freq, data.Length);
    }
  }

  private void SetRatioBar(double ratio) {
    if (ratio < 0) {
      RatioBar.Height = 0;
      RatioBarText.Text = "";
      return;
    }

    // Clamp to 0-100 for display
    var clampedRatio = Math.Clamp(ratio, 0, 100);

    // Color gradient: green for good compression, yellow for moderate, red for poor/expanded
    RatioBar.Background = new SolidColorBrush(GetRatioColor(ratio));

    // Bind actual height after layout
    Loaded += (_, _) => {
      var parent = RatioBar.Parent as FrameworkElement;
      if (parent != null) {
        var availHeight = parent.ActualHeight;
        RatioBar.Height = availHeight * clampedRatio / 100.0;
      }
    };

    RatioBarText.Text = $"{ratio:F0}%";
  }

  private void SetRatioVisualBar(double ratio) {
    if (ratio < 0) {
      RatioVisualBar.Width = 0;
      RatioVisualText.Text = "N/A";
      return;
    }

    var clamped = Math.Clamp(ratio, 0, 100);
    RatioVisualBar.Background = new SolidColorBrush(GetRatioColor(ratio));
    RatioVisualText.Text = $"{ratio:F1}%";

    Loaded += (_, _) => {
      var parent = RatioVisualBar.Parent as FrameworkElement;
      if (parent != null) {
        RatioVisualBar.Width = parent.ActualWidth * clamped / 100.0;
      }
    };
  }

  private static System.Windows.Media.Color GetRatioColor(double ratio) {
    // <30% green, 30-70% blue, 70-100% orange, >100% red
    if (ratio < 30) return System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50);      // Green
    if (ratio < 70) return System.Windows.Media.Color.FromRgb(0x33, 0x99, 0xFF);       // Blue
    if (ratio <= 100) return System.Windows.Media.Color.FromRgb(0xFF, 0x98, 0x00);     // Orange
    return System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36);                       // Red (expanded)
  }

  private void DrawHistogram(long[] freq, int totalBytes) {
    HistogramCanvas.Children.Clear();

    var canvas = HistogramCanvas;
    var width = canvas.ActualWidth;
    var height = canvas.ActualHeight;

    if (width <= 0 || height <= 0) return;

    // Find max frequency for scaling (use log scale for better visibility)
    var maxFreq = freq.Max();
    if (maxFreq == 0) return;
    var logMax = Math.Log(maxFreq + 1);

    var barWidth = width / 256.0;

    for (var i = 0; i < 256; i++) {
      if (freq[i] == 0) continue;

      var logVal = Math.Log(freq[i] + 1);
      var barHeight = (logVal / logMax) * (height - 2);

      var rect = new System.Windows.Shapes.Rectangle {
        Width = Math.Max(barWidth - 0.3, 0.5),
        Height = Math.Max(barHeight, 1),
        Fill = GetHistogramBrush(i),
      };

      Canvas.SetLeft(rect, i * barWidth);
      Canvas.SetTop(rect, height - barHeight);
      canvas.Children.Add(rect);
    }
  }

  private static System.Windows.Media.Brush GetHistogramBrush(int byteValue) {
    // Color by byte range: 00 = red (null), 01-1F = orange (control), 20-7E = blue (printable ASCII), 7F-FF = purple (high)
    if (byteValue == 0) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36));
    if (byteValue < 0x20) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x98, 0x00));
    if (byteValue < 0x7F) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x99, 0xFF));
    return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x27, 0xB0));
  }

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
