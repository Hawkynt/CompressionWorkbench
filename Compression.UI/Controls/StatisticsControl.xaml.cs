using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Compression.Analysis.Statistics;
using Brushes = System.Windows.Media.Brushes;
using UserControl = System.Windows.Controls.UserControl;
using WpfColor = System.Windows.Media.Color;

namespace Compression.UI.Controls;

public partial class StatisticsControl : UserControl {
  private byte[]? _data;
  private bool _compactMode;
  private DispatcherTimer? _redrawTimer;
  private long[]? _histogramFreq;
  private int _histogramMostIdx;
  private int _histogramLeastIdx = -1;

  public StatisticsControl() {
    InitializeComponent();
  }

  /// <summary>When set, analyzes the data and populates all statistics fields.</summary>
  public byte[]? Data {
    get => _data;
    set {
      _data = value;
      if (value is { Length: > 0 })
        PopulateStatistics(value);
    }
  }

  /// <summary>When true, hides Content Analysis section to save space.</summary>
  public bool CompactMode {
    get => _compactMode;
    set {
      _compactMode = value;
      ContentAnalysisPanel.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
    }
  }

  /// <summary>Exposes the frequency table for external consumers (e.g., hex coloring).</summary>
  internal long[]? FrequencyTable { get; private set; }

  private void PopulateStatistics(byte[] data) {
    var n = data.Length;
    var stats = BinaryStatistics.Analyze(data);
    FrequencyTable = stats.ByteFrequency;
    var freq = stats.ByteFrequency;
    var entropy = stats.Entropy;
    var mean = stats.Mean;
    var chiSq = stats.ChiSquare;
    var pValue = stats.PValue;
    var serialCorr = stats.SerialCorrelation;

    // Randomness tests
    EntropyText.Text = $"{entropy:F4} bits/byte  (max 8.0000)";
    MeanText.Text = $"{mean:F4}  (random = 127.5)";

    var chiVerdict = pValue < 0.01 ? "NOT random" : pValue < 0.05 ? "possibly not random" : "consistent with random";
    ChiSquareText.Text = $"{chiSq:F2}  (p = {pValue:F6}, {chiVerdict})";

    var scVerdict = Math.Abs(serialCorr) < 0.05 ? "no correlation" : Math.Abs(serialCorr) < 0.2 ? "weak" : "correlated";
    SerialCorrelationText.Text = $"{serialCorr:F6}  ({scVerdict})";

    if (n >= 6) {
      var piEst = stats.MonteCarloPi;
      var piPct = 100.0 * Math.Abs(piEst - Math.PI) / Math.PI;
      MonteCarloPiText.Text = $"{piEst:F6}  (error {piPct:F2}%, {(piPct < 1 ? "good" : piPct < 5 ? "fair" : "poor")})";
    }
    else {
      MonteCarloPiText.Text = "N/A (too few bytes)";
    }

    // Byte distribution
    UniqueBytesText.Text = $"{stats.UniqueBytesCount} / 256  ({100.0 * stats.UniqueBytesCount / 256:F1}%)";
    MostCommonText.Text = $"0x{stats.MostCommonByte:X2} ({FormatByteChar(stats.MostCommonByte)}) \u2014 {stats.MostCommonCount:N0} ({100.0 * stats.MostCommonCount / n:F1}%)";
    LeastCommonText.Text = stats.LeastCommonCount > 0
      ? $"0x{stats.LeastCommonByte:X2} ({FormatByteChar(stats.LeastCommonByte)}) \u2014 {stats.LeastCommonCount:N0} ({100.0 * stats.LeastCommonCount / n:F1}%)"
      : "N/A";

    var idealBytes = (long)Math.Ceiling(entropy * n / 8.0);
    IdealSizeText.Text = $"\u2265 {FormatSize(idealBytes)} (Shannon limit)";

    string assessment;
    WpfColor assessColor;
    if (entropy > 7.9 && pValue > 0.01 && Math.Abs(serialCorr) < 0.05) {
      assessment = "Highly random / encrypted / already compressed";
      assessColor = WpfColor.FromRgb(0xF4, 0x43, 0x36);
    }
    else if (entropy > 7.0) {
      assessment = "High entropy \u2014 limited compressibility";
      assessColor = WpfColor.FromRgb(0xFF, 0x98, 0x00);
    }
    else if (entropy > 5.0) {
      assessment = "Moderate entropy \u2014 good compressibility";
      assessColor = WpfColor.FromRgb(0x33, 0x99, 0xFF);
    }
    else {
      assessment = "Low entropy \u2014 excellent compressibility";
      assessColor = WpfColor.FromRgb(0x4C, 0xAF, 0x50);
    }
    AssessmentText.Text = assessment;
    AssessmentText.Foreground = new SolidColorBrush(assessColor);

    // Content analysis
    long printable = 0, control = 0, high = 0, nullBytes = freq[0];
    for (var i = 1; i < 0x20; i++) control += freq[i];
    for (var i = 0x20; i < 0x7F; i++) printable += freq[i];
    control += freq[0x7F]; // DEL
    for (var i = 0x80; i < 256; i++) high += freq[i];

    PrintableAsciiText.Text = $"{printable:N0}  ({100.0 * printable / n:F1}%)";
    ControlBytesText.Text = $"{control:N0}  ({100.0 * control / n:F1}%)";
    HighBytesText.Text = $"{high:N0}  ({100.0 * high / n:F1}%)";
    NullBytesText.Text = $"{nullBytes:N0}  ({100.0 * nullBytes / n:F1}%)";

    var sampled = n > 256 * 1024 ? $"  (sampled {256}K)" : "";
    Bigram2Text.Text = $"{stats.UniqueBigrams:N0} / 65,536  ({100.0 * stats.UniqueBigrams / 65536:F1}%){sampled}";
    Bigram3Text.Text = $"{stats.UniqueTrigrams:N0} / 16,777,216  ({100.0 * stats.UniqueTrigrams / 16777216:F2}%){sampled}";
    Bigram4Text.Text = $"{stats.UniqueQuadgrams:N0}{sampled}";

    // Draw histogram after layout
    Dispatcher.InvokeAsync(() => DrawHistogram(data), DispatcherPriority.Loaded);
  }

  private void OnHistogramSizeChanged(object sender, SizeChangedEventArgs e) {
    // Debounced redraw
    _redrawTimer?.Stop();
    _redrawTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    _redrawTimer.Tick += (_, _) => {
      _redrawTimer.Stop();
      if (_data is { Length: > 0 })
        DrawHistogram(_data);
    };
    _redrawTimer.Start();
  }

  private void DrawHistogram(byte[] data) {
    HistogramCanvas.Children.Clear();

    var canvas = HistogramCanvas;
    var width = canvas.ActualWidth;
    var height = canvas.ActualHeight;
    if (width <= 0 || height <= 0) return;

    var freq = new long[256];
    foreach (var b in data) freq[b]++;
    _histogramFreq = freq;

    var maxFreq = freq.Max();
    if (maxFreq == 0) return;
    var logMax = Math.Log(maxFreq + 1);
    var barWidth = width / 256.0;

    // Background bands
    AddBackgroundBand(canvas, 0, barWidth * 32, height, WpfColor.FromArgb(0x30, 0xFF, 0x98, 0x00));
    AddBackgroundBand(canvas, barWidth * 32, barWidth * 95, height, WpfColor.FromArgb(0x18, 0x33, 0x99, 0xFF));
    AddBackgroundBand(canvas, barWidth * 127, barWidth * 129, height, WpfColor.FromArgb(0x20, 0x9C, 0x27, 0xB0));

    // Find most/least common
    var mostIdx = 0;
    var leastIdx = -1;
    for (var i = 0; i < 256; i++) {
      if (freq[i] > freq[mostIdx]) mostIdx = i;
      if (freq[i] > 0 && (leastIdx < 0 || freq[i] < freq[leastIdx])) leastIdx = i;
    }
    _histogramMostIdx = mostIdx;
    _histogramLeastIdx = leastIdx;

    // Bars (no per-rect tooltips — tooltip is handled via MouseMove on the canvas)
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
        IsHitTestVisible = false,
      };

      Canvas.SetLeft(rect, i * barWidth);
      Canvas.SetTop(rect, height - barHeight);
      canvas.Children.Add(rect);
    }
  }

  private int _lastTooltipByte = -1;

  private void OnHistogramMouseMove(object sender, System.Windows.Input.MouseEventArgs e) {
    if (_histogramFreq == null || _data == null) return;
    var pos = e.GetPosition(HistogramCanvas);
    var barWidth = HistogramCanvas.ActualWidth / 256.0;
    var byteIndex = Math.Clamp((int)(pos.X / barWidth), 0, 255);

    if (byteIndex == _lastTooltipByte) return;
    _lastTooltipByte = byteIndex;

    HistogramTooltip.Content = FormatHistogramTooltip(byteIndex, _histogramFreq[byteIndex], _data.Length,
      byteIndex == _histogramMostIdx, byteIndex == _histogramLeastIdx);

    // Force tooltip to refresh position and content
    HistogramTooltip.IsOpen = false;
    HistogramTooltip.IsOpen = true;
  }

  private void OnHistogramMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) {
    _lastTooltipByte = -1;
    HistogramTooltip.IsOpen = false;
  }

  private static void AddBackgroundBand(Canvas canvas, double left, double width, double height, WpfColor color) {
    var rect = new System.Windows.Shapes.Rectangle {
      Width = width, Height = height,
      Fill = new SolidColorBrush(color),
    };
    Canvas.SetLeft(rect, left);
    Canvas.SetTop(rect, 0);
    canvas.Children.Add(rect);
  }

  private static string FormatHistogramTooltip(int byteValue, long count, int total, bool isMost, bool isLeast) {
    var pct = 100.0 * count / total;
    var label = isMost ? "  [MOST COMMON]" : isLeast ? "  [LEAST COMMON]" : "";
    var charRepr = FormatByteChar(byteValue);
    var category = byteValue == 0 ? "Null"
      : byteValue < 0x20 || byteValue == 0x7F ? "Control"
      : byteValue < 0x7F ? "Printable ASCII"
      : "High byte";

    return $"Byte 0x{byteValue:X2} ({byteValue}) \u2014 {charRepr}{label}\n" +
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

  internal static string FormatByteChar(int b) => b switch {
    0x00 => "NUL", 0x01 => "SOH", 0x02 => "STX", 0x03 => "ETX",
    0x07 => "BEL", 0x08 => "BS", 0x09 => "TAB", 0x0A => "LF",
    0x0B => "VT", 0x0C => "FF", 0x0D => "CR", 0x1B => "ESC",
    0x20 => "SPACE", 0x7F => "DEL",
    >= 0x21 and < 0x7F => $"'{(char)b}'",
    _ => $"0x{b:X2}",
  };

  internal static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes:N0} bytes",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };

  internal static string FormatSizeDetailed(long bytes) => bytes switch {
    < 1024 => $"{bytes:N0} bytes",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB ({bytes:N0} bytes)",
    < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB ({bytes:N0} bytes)",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB ({bytes:N0} bytes)",
  };
}
