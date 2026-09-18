using System.Drawing;
using Compression.Analysis.Statistics;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>
/// The statistics panel shared by the preview and properties windows: a byte-frequency histogram
/// over randomness tests, byte distribution and — outside compact mode — content analysis.
/// </summary>
internal sealed class StatisticsControl : Panel {
  private const int LabelColumn = 130;
  private const int RowHeight = 22;
  private const int HistogramHeight = 100;
  private static readonly Color HeadingColor = Color.FromArgb(0x44, 0x66, 0xAA);

  private readonly HistogramControl _histogram = new();
  private readonly List<Control> _contentAnalysis = [];

  private readonly Label _entropy = Value();
  private readonly Label _mean = Value();
  private readonly Label _chiSquare = Value();
  private readonly Label _serialCorrelation = Value();
  private readonly Label _monteCarloPi = Value();

  private readonly Label _uniqueBytes = Value();
  private readonly Label _mostCommon = Value();
  private readonly Label _leastCommon = Value();
  private readonly Label _idealSize = Value();
  private readonly Label _assessment = Value();

  private readonly Label _printableAscii = Value();
  private readonly Label _controlBytes = Value();
  private readonly Label _highBytes = Value();
  private readonly Label _nullBytes = Value();
  private readonly Label _bigrams = Value();
  private readonly Label _trigrams = Value();
  private readonly Label _quadgrams = Value();

  private byte[]? _data;
  private bool _compactMode;

  public StatisticsControl() {
    this.AutoScroll = true;
    this.BuildLayout();
  }

  /// <summary>When set, analyses the data and populates every field.</summary>
  public byte[]? Data {
    get => this._data;
    set {
      this._data = value;
      if (value is { Length: > 0 }) this.Populate(value);
    }
  }

  /// <summary>When true, hides the content-analysis section to save space.</summary>
  public bool CompactMode {
    get => this._compactMode;
    set {
      this._compactMode = value;
      foreach (var control in this._contentAnalysis) control.Visible = !value;
      this.PerformLayout();
    }
  }

  /// <summary>Byte-frequency table from the last analysis, for callers that colour by frequency.</summary>
  public long[]? FrequencyTable { get; private set; }

  private static Label Value() => new() { Bounds = new(0, 0, 320, RowHeight) };

  private void BuildLayout() {
    var y = 0;

    y = this.AddHeading("Byte Frequency Distribution", y);
    this._histogram.Bounds = new(8, y, 320, HistogramHeight);
    this.Controls.Add(this._histogram);
    y += HistogramHeight + 4;

    y = this.AddLegend(y) + 8;

    y = this.AddHeading("Randomness Tests", y);
    y = this.AddRow("Entropy:", this._entropy, y);
    y = this.AddRow("Arithmetic mean:", this._mean, y);
    y = this.AddRow("Chi-square:", this._chiSquare, y);
    y = this.AddRow("Serial correlation:", this._serialCorrelation, y);
    y = this.AddRow("Monte Carlo Pi:", this._monteCarloPi, y) + 8;

    y = this.AddHeading("Byte Distribution", y);
    y = this.AddRow("Unique bytes:", this._uniqueBytes, y);
    y = this.AddRow("Most common:", this._mostCommon, y);
    y = this.AddRow("Least common:", this._leastCommon, y);
    this._idealSize.ForeColor = Color.Gray;
    y = this.AddRow("Ideal size:", this._idealSize, y);
    y = this.AddRow("Assessment:", this._assessment, y) + 8;

    var contentStart = this.Controls.Count;
    y = this.AddHeading("Content Analysis", y);
    y = this.AddRow("Printable ASCII:", this._printableAscii, y);
    y = this.AddRow("Control bytes:", this._controlBytes, y);
    y = this.AddRow("High bytes:", this._highBytes, y);
    y = this.AddRow("Null bytes:", this._nullBytes, y);
    y = this.AddRow("Unique 2-grams:", this._bigrams, y);
    y = this.AddRow("Unique 3-grams:", this._trigrams, y);
    this.AddRow("Unique 4-grams:", this._quadgrams, y);

    for (var i = contentStart; i < this.Controls.Count; ++i)
      this._contentAnalysis.Add(this.Controls[i]);
  }

  private int AddHeading(string text, int y) {
    this.Controls.Add(new Label {
      Bounds = new(0, y, 320, 20),
      Text = text,
      ForeColor = HeadingColor,
      Font = new(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold),
    });
    return y + 24;
  }

  private int AddRow(string caption, Label value, int y) {
    this.Controls.Add(new Label { Bounds = new(8, y, LabelColumn, RowHeight), Text = caption });
    value.Bounds = new(8 + LabelColumn, y, 320, RowHeight);
    this.Controls.Add(value);
    return y + RowHeight;
  }

  /// <summary>The histogram key, matching the bar and band colours.</summary>
  private int AddLegend(int y) {
    var entries = new (string Text, Color Swatch)[] {
      ("Most", Color.FromArgb(0xE0, 0x60, 0x60)),
      ("Least", Color.FromArgb(0x4C, 0xAF, 0x50)),
      ("Ctrl", Color.FromArgb(0x30, 0xFF, 0x98, 0x00)),
      ("ASCII", Color.FromArgb(0x30, 0x33, 0x99, 0xFF)),
      ("High", Color.FromArgb(0x30, 0x9C, 0x27, 0xB0)),
    };

    var x = 8;
    foreach (var (text, swatch) in entries) {
      this.Controls.Add(new Panel { Bounds = new(x, y + 3, 10, 10), BackColor = swatch });
      x += 13;
      this.Controls.Add(new Label {
        Bounds = new(x, y, 42, 16),
        Text = text,
        ForeColor = Color.Gray,
        Font = new(DefaultTheme.Instance.DefaultFont.Family, 7.5f, FontStyle.Regular),
      });
      x += 46;
    }

    return y + 18;
  }

  private void Populate(byte[] data) {
    var n = data.Length;
    var stats = BinaryStatistics.Analyze(data);
    this.FrequencyTable = stats.ByteFrequency;
    var freq = stats.ByteFrequency;

    this._entropy.Text = $"{stats.Entropy:F4} bits/byte  (max 8.0000)";
    this._mean.Text = $"{stats.Mean:F4}  (random = 127.5)";

    var chiVerdict = stats.PValue < 0.01 ? "NOT random"
      : stats.PValue < 0.05 ? "possibly not random"
      : "consistent with random";
    this._chiSquare.Text = $"{stats.ChiSquare:F2}  (p = {stats.PValue:F6}, {chiVerdict})";

    var absCorr = Math.Abs(stats.SerialCorrelation);
    var scVerdict = absCorr < 0.05 ? "no correlation" : absCorr < 0.2 ? "weak" : "correlated";
    this._serialCorrelation.Text = $"{stats.SerialCorrelation:F6}  ({scVerdict})";

    if (n >= 6) {
      var piEst = stats.MonteCarloPi;
      var piPct = 100.0 * Math.Abs(piEst - Math.PI) / Math.PI;
      var quality = piPct < 1 ? "good" : piPct < 5 ? "fair" : "poor";
      this._monteCarloPi.Text = $"{piEst:F6}  (error {piPct:F2}%, {quality})";
    } else {
      this._monteCarloPi.Text = "N/A (too few bytes)";
    }

    this._uniqueBytes.Text = $"{stats.UniqueBytesCount} / 256  ({100.0 * stats.UniqueBytesCount / 256:F1}%)";
    this._mostCommon.Text = $"0x{stats.MostCommonByte:X2} ({FormatByteChar(stats.MostCommonByte)}) — {stats.MostCommonCount:N0} ({100.0 * stats.MostCommonCount / n:F1}%)";
    this._leastCommon.Text = stats.LeastCommonCount > 0
      ? $"0x{stats.LeastCommonByte:X2} ({FormatByteChar(stats.LeastCommonByte)}) — {stats.LeastCommonCount:N0} ({100.0 * stats.LeastCommonCount / n:F1}%)"
      : "N/A";

    var idealBytes = (long)Math.Ceiling(stats.Entropy * n / 8.0);
    this._idealSize.Text = $"≥ {FormatSize(idealBytes)} (Shannon limit)";

    var (assessment, assessColor) = Assess(stats.Entropy, stats.PValue, stats.SerialCorrelation);
    this._assessment.Text = assessment;
    this._assessment.ForeColor = assessColor;

    long printable = 0, control = 0, high = 0;
    var nullBytes = freq[0];
    for (var i = 1; i < 0x20; ++i) control += freq[i];
    for (var i = 0x20; i < 0x7F; ++i) printable += freq[i];
    control += freq[0x7F]; // DEL
    for (var i = 0x80; i < 256; ++i) high += freq[i];

    this._printableAscii.Text = $"{printable:N0}  ({100.0 * printable / n:F1}%)";
    this._controlBytes.Text = $"{control:N0}  ({100.0 * control / n:F1}%)";
    this._highBytes.Text = $"{high:N0}  ({100.0 * high / n:F1}%)";
    this._nullBytes.Text = $"{nullBytes:N0}  ({100.0 * nullBytes / n:F1}%)";

    var sampled = n > 256 * 1024 ? "  (sampled 256K)" : "";
    this._bigrams.Text = $"{stats.UniqueBigrams:N0} / 65,536  ({100.0 * stats.UniqueBigrams / 65536:F1}%){sampled}";
    this._trigrams.Text = $"{stats.UniqueTrigrams:N0} / 16,777,216  ({100.0 * stats.UniqueTrigrams / 16777216:F2}%){sampled}";
    this._quadgrams.Text = $"{stats.UniqueQuadgrams:N0}{sampled}";

    this._histogram.SetData(data);
  }

  private static (string Text, Color Color) Assess(double entropy, double pValue, double serialCorrelation)
    => entropy > 7.9 && pValue > 0.01 && Math.Abs(serialCorrelation) < 0.05
      ? ("Highly random / encrypted / already compressed", Color.FromArgb(0xF4, 0x43, 0x36))
      : entropy > 7.0 ? ("High entropy — limited compressibility", Color.FromArgb(0xFF, 0x98, 0x00))
      : entropy > 5.0 ? ("Moderate entropy — good compressibility", Color.FromArgb(0x33, 0x99, 0xFF))
      : ("Low entropy — excellent compressibility", Color.FromArgb(0x4C, 0xAF, 0x50));

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
