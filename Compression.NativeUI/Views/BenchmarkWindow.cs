using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using Compression.Lib;
using Compression.NativeUI.Theming;
using Compression.Registry;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.ComponentModel;

namespace Compression.NativeUI.Views;

/// <summary>One algorithm in the selection list.</summary>
internal sealed class FormatCheckItem {
  public required string Id { get; init; }
  public required string DisplayName { get; init; }
  public required string FamilyName { get; init; }
  public required string Description { get; init; }
  public required Color FamilyColor { get; init; }
  public AlgorithmFamily Family { get; init; }
  public bool IsSelected { get; set; }
}

/// <summary>One algorithm-versus-pattern measurement.</summary>
internal sealed class BenchmarkResult {
  public required string FormatName { get; init; }
  public required string FormatId { get; init; }
  public required string DataPattern { get; init; }
  public int OriginalSize { get; init; }
  public int CompressedSize { get; init; }
  public double Ratio { get; init; }
  public double CompressTimeMs { get; init; }
  public double DecompressTimeMs { get; init; }
  public double CompressSpeedKBs { get; init; }
  public double DecompressSpeedKBs { get; init; }
  public bool Verified { get; init; }
  public string? Error { get; init; }

  public string OriginalSizeText => this.OriginalSize >= 0 ? FormatSize(this.OriginalSize) : "N/A";
  public string CompressedSizeText => this.CompressedSize >= 0 ? FormatSize(this.CompressedSize) : "ERR";
  public string RatioText => this.Ratio >= 0 ? this.Ratio.ToString("P1", CultureInfo.InvariantCulture) : "ERR";
  public string CompressSpeedText => this.CompressSpeedKBs >= 0 ? FormatSpeed(this.CompressSpeedKBs) : "ERR";
  public string DecompressSpeedText => this.DecompressSpeedKBs >= 0 ? FormatSpeed(this.DecompressSpeedKBs) : "ERR";
  public string CompressTimeText => this.CompressTimeMs >= 0 ? $"{this.CompressTimeMs:F2} ms" : "ERR";
  public string DecompressTimeText => this.DecompressTimeMs >= 0 ? $"{this.DecompressTimeMs:F2} ms" : "ERR";
  public string VerifiedText => this.Error is not null ? "ERR" : this.Verified ? "OK" : "FAIL";

  internal static string FormatSize(int bytes) => bytes switch {
    >= 1048576 => $"{bytes / 1048576.0:F1} MB",
    >= 1024 => $"{bytes / 1024.0:F1} KB",
    _ => $"{bytes} B",
  };

  internal static string FormatSpeed(double kbs) => kbs switch {
    >= 1024 * 1024 => $"{kbs / (1024 * 1024):F1} GB/s",
    >= 1024 => $"{kbs / 1024:F1} MB/s",
    _ => $"{kbs:F0} KB/s",
  };
}

/// <summary>Per-algorithm roll-up across every data pattern.</summary>
internal sealed class BenchmarkSummary {
  public required string FormatName { get; init; }
  public double AvgRatio { get; init; }
  public double BestRatio { get; init; }
  public double AvgCompressSpeedKBs { get; init; }
  public double AvgDecompressSpeedKBs { get; init; }
  public int TestCount { get; init; }
  public bool AllVerified { get; init; }

  public string AvgRatioText => this.AvgRatio.ToString("P1", CultureInfo.InvariantCulture);
  public string BestRatioText => this.BestRatio.ToString("P1", CultureInfo.InvariantCulture);
  public string AvgCompressSpeedText => BenchmarkResult.FormatSpeed(this.AvgCompressSpeedKBs);
  public string AvgDecompressSpeedText => BenchmarkResult.FormatSpeed(this.AvgDecompressSpeedKBs);
  public string AllVerifiedText => this.AllVerified ? "OK" : "FAIL";
}

/// <summary>
/// Runs every selected building block against every selected synthetic data pattern, timing
/// compression and decompression and verifying the round trip.
/// </summary>
internal sealed class BenchmarkWindow : Form {
  /// <summary>Per-test budget for compress, decompress, verify and the timed loops.</summary>
  private const int PerTestTimeoutMs = 10_000;

  private static readonly Dictionary<AlgorithmFamily, Color> FamilyColors = new() {
    [AlgorithmFamily.Dictionary] = Color.FromArgb(0xE8, 0xF0, 0xFE),
    [AlgorithmFamily.Entropy] = Color.FromArgb(0xF0, 0xE8, 0xFE),
    [AlgorithmFamily.Transform] = Color.FromArgb(0xE8, 0xFE, 0xF0),
    [AlgorithmFamily.ContextMixing] = Color.FromArgb(0xFE, 0xF0, 0xE8),
    [AlgorithmFamily.Classic] = Color.FromArgb(0xFE, 0xE8, 0xF0),
    [AlgorithmFamily.Encoding] = Color.FromArgb(0xE8, 0xFE, 0xFE),
    [AlgorithmFamily.Archive] = Color.FromArgb(0xF0, 0xF0, 0xE8),
    [AlgorithmFamily.Other] = Color.White,
  };

  private static readonly Dictionary<AlgorithmFamily, string> FamilyNames = new() {
    [AlgorithmFamily.Dictionary] = "Dictionary",
    [AlgorithmFamily.Entropy] = "Entropy",
    [AlgorithmFamily.Transform] = "Transform",
    [AlgorithmFamily.ContextMixing] = "Mixing",
    [AlgorithmFamily.Classic] = "Classic",
    [AlgorithmFamily.Encoding] = "Encoding",
    [AlgorithmFamily.Archive] = "Archive",
    [AlgorithmFamily.Other] = "Other",
  };

  private static readonly (string Label, int Bytes)[] DataSizes = [
    ("1 KB", 1024), ("4 KB", 4096), ("16 KB", 16384), ("64 KB", 65536),
    ("256 KB", 262144), ("1 MB", 1048576), ("2 MB", 2097152), ("4 MB", 4194304),
    ("8 MB", 8388608), ("16 MB", 16777216), ("32 MB", 33554432), ("64 MB", 67108864),
    ("128 MB", 134217728), ("256 MB", 268435456), ("512 MB", 536870912), ("1 GB", 1073741824),
  ];

  private readonly GroupBox _dataBox = new() { Text = "Test Data" };
  private readonly Dictionary<string, CheckBox> _patterns = [];
  private readonly ComboBox _dataSize = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly ComboBox _iterations = new() { DropDownStyle = ComboBoxStyle.DropDownList };

  private readonly GroupBox _algorithmBox = new() { Text = "Algorithms" };
  private readonly DataGridView _formats = new() { AllowUserToResizeColumns = true, ShowGridLines = true };
  private readonly Label _formatCount = new() { ForeColor = Color.Gray };

  private readonly Button _run = new() { Text = "Run Benchmark" };
  private readonly Button _stop = new() { Text = "Stop", Enabled = false };
  private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
  private readonly Label _statusText = new() { ForeColor = Color.Gray };

  private readonly TabControl _tabs = new();
  private readonly DataGridView _resultsGrid = new() { ReadOnly = true, AlternatingRows = true, ShowGridLines = true };
  private readonly DataGridView _summaryGrid = new() { ReadOnly = true, AlternatingRows = true, ShowGridLines = true };

  private readonly Button _exportCsv = new() { Text = "Export CSV..." };
  private readonly Label _totalTime = new() { ForeColor = Color.Gray };
  private readonly ToolTip _toolTips = new();

  private readonly ObservableList<object> _results = [];
  private readonly ObservableList<object> _summaries = [];
  private List<FormatCheckItem> _formatItems = [];
  private CancellationTokenSource? _cts;

  public BenchmarkWindow() {
    this.Text = "Compression Benchmark";
    this.ClientSize = new(1100, 750);
    this.MinimumSize = new(800, 550);
    this.StartPosition = FormStartPosition.CenterScreen;

    this.BuildDataBox();
    this.BuildAlgorithmBox();
    this.BuildRunRow();
    this.BuildResultTabs();

    this._exportCsv.Image = Images.Icon(IconKeys.Save, 14);
    this._exportCsv.Click += (_, _) => this.OnExportCsv();
    this.Controls.AddRange(this._exportCsv, this._totalTime);

    this.LoadFormats();
    this.Resize += (_, _) => this.LayoutChildren();
    this.LayoutChildren();
  }

  private void BuildDataBox() {
    var descriptions = new (string Key, string Label, string Tip)[] {
      ("All Zeroes", "All Zeroes", "N bytes of 0x00. Best case for RLE and\ndictionary compressors — tests maximum\ncompression ratio on trivial input."),
      ("Alternating", "Alternating (0xAA/0x55)", "Alternates 0xAA and 0x55 (10101010 / 01011010).\nRegular 2-byte pattern. Tests how compressors\nhandle very short repeating cycles."),
      ("Incrementing", "Incrementing (0..255)", "Cycles 0,1,2,...,255,0,1,... (256-byte period).\nPredictable but uses all byte values.\nTests delta filters and match finders."),
      ("Repeating", "Repeating Pattern", "Repeats 'ABCDEFGHIJKLMNOP' (16-byte ASCII).\nTests dictionary match distance and\ncompression of structured text-like data."),
      ("English Text", "English Text", "Repeated English sentences with natural\nword frequency. Realistic workload for\ntext compressors and context models."),
      ("Random", "Random", "Cryptographically-style random bytes (seed=42).\nIncompressible — measures overhead and\nworst-case expansion of each algorithm."),
      ("Binary Struct", "Binary Structured", "Mix of counters, padding zeros, small values,\nand random payload in 16-byte records.\nSimulates real binary file formats (headers, structs)."),
    };

    var x = 10;
    foreach (var (key, label, tip) in descriptions) {
      var check = new CheckBox { Text = label, Checked = true, Bounds = new(x, 22, 10 * label.Length + 30, 20) };
      this._toolTips.SetToolTip(check, tip);
      this._patterns[key] = check;
      this._dataBox.Controls.Add(check);
      x += check.Width + 12;
    }

    this._dataBox.Controls.Add(new Label { Bounds = new(10, 50, 60, 22), Text = "Data Size:" });
    foreach (var (label, _) in DataSizes) this._dataSize.Items.Add(label);
    this._dataSize.SelectedIndex = 4;
    this._dataSize.Bounds = new(74, 48, 100, 24);

    this._dataBox.Controls.Add(new Label { Bounds = new(186, 50, 64, 22), Text = "Iterations:" });
    foreach (var value in new[] { "1", "3", "5", "10" }) this._iterations.Items.Add(value);
    this._iterations.SelectedIndex = 1;
    this._iterations.Bounds = new(252, 48, 60, 24);

    this._dataBox.Controls.AddRange(this._dataSize, this._iterations);
    this.Controls.Add(this._dataBox);
  }

  private void BuildAlgorithmBox() {
    var quickPicks = new (string Text, Color Back, Func<FormatCheckItem, bool> Predicate)[] {
      ("All", Color.Empty, _ => true),
      ("None", Color.Empty, _ => false),
      ("Invert", Color.Empty, f => !f.IsSelected),
      ("Dictionary", Color.FromArgb(0xE8, 0xF0, 0xFE), f => f.FamilyName == "Dictionary"),
      ("Entropy", Color.FromArgb(0xF0, 0xE8, 0xFE), f => f.FamilyName == "Entropy"),
      ("Transform", Color.FromArgb(0xE8, 0xFE, 0xF0), f => f.FamilyName == "Transform"),
      ("Mixing", Color.FromArgb(0xFE, 0xF0, 0xE8), f => f.FamilyName == "Mixing"),
      ("Classic", Color.FromArgb(0xFE, 0xE8, 0xF0), f => f.FamilyName == "Classic"),
      ("Encoding", Color.FromArgb(0xE8, 0xFE, 0xFE), f => f.FamilyName == "Encoding"),
    };

    var x = 8;
    foreach (var (text, back, predicate) in quickPicks) {
      var captured = predicate;
      var button = new Button { Text = text, Bounds = new(x, 20, 74, 22) };
      if (back != Color.Empty) button.BackColor = back;
      button.Click += (_, _) => this.SetSelection(captured);
      this._algorithmBox.Controls.Add(button);
      x += 77;
    }

    this._formatCount.Bounds = new(x + 8, 22, 220, 18);

    // The family tint moves from a row style onto the row background selector.
    this._formats.RowBackColorSelector = static item => item is FormatCheckItem f ? f.FamilyColor : null;
    this._formats.Columns.Add(new DataGridViewColumn("", static _ => null) {
      Kind = DataGridViewColumnKind.Check,
      Width = 30,
      CheckedSelector = static o => o is FormatCheckItem f && f.IsSelected,
      CheckedSetter = (o, v) => {
        if (o is FormatCheckItem f) f.IsSelected = v;
        this.UpdateFormatCount();
      },
    });
    this._formats.Columns.Add(new DataGridViewColumn("Algorithm", static o => ((FormatCheckItem)o!).DisplayName) { Width = 120 });
    this._formats.Columns.Add(new DataGridViewColumn("Family", static o => ((FormatCheckItem)o!).FamilyName) { Width = 90 });
    this._formats.Columns.Add(new DataGridViewColumn("Description", static o => ((FormatCheckItem)o!).Description) { Width = 420 });

    this._algorithmBox.Controls.AddRange(this._formatCount, this._formats);
    this.Controls.Add(this._algorithmBox);
  }

  private void BuildRunRow() {
    this._run.Image = Images.Icon(IconKeys.Test, 16);
    this._run.Click += async (_, _) => await this.RunBenchmarkAsync();
    this._stop.Click += (_, _) => {
      this._cts?.Cancel();
      this._statusText.Text = "Stopping...";
    };

    this.Controls.AddRange(this._run, this._stop, this._progress, this._statusText);
  }

  private void BuildResultTabs() {
    AddColumn(this._resultsGrid, "Algorithm", o => ((BenchmarkResult)o!).FormatName, 120);
    AddColumn(this._resultsGrid, "Data Pattern", o => ((BenchmarkResult)o!).DataPattern, 110);
    AddNumeric(this._resultsGrid, "Original", o => ((BenchmarkResult)o!).OriginalSizeText, 80, (a, b) => ((BenchmarkResult)a!).OriginalSize.CompareTo(((BenchmarkResult)b!).OriginalSize));
    AddNumeric(this._resultsGrid, "Compressed", o => ((BenchmarkResult)o!).CompressedSizeText, 90, (a, b) => ((BenchmarkResult)a!).CompressedSize.CompareTo(((BenchmarkResult)b!).CompressedSize));
    AddNumeric(this._resultsGrid, "Ratio", o => ((BenchmarkResult)o!).RatioText, 65, (a, b) => ((BenchmarkResult)a!).Ratio.CompareTo(((BenchmarkResult)b!).Ratio));
    AddNumeric(this._resultsGrid, "Comp. Speed", o => ((BenchmarkResult)o!).CompressSpeedText, 100, (a, b) => ((BenchmarkResult)a!).CompressSpeedKBs.CompareTo(((BenchmarkResult)b!).CompressSpeedKBs));
    AddNumeric(this._resultsGrid, "Decomp. Speed", o => ((BenchmarkResult)o!).DecompressSpeedText, 110, (a, b) => ((BenchmarkResult)a!).DecompressSpeedKBs.CompareTo(((BenchmarkResult)b!).DecompressSpeedKBs));
    AddNumeric(this._resultsGrid, "Comp. Time", o => ((BenchmarkResult)o!).CompressTimeText, 90, (a, b) => ((BenchmarkResult)a!).CompressTimeMs.CompareTo(((BenchmarkResult)b!).CompressTimeMs));
    AddNumeric(this._resultsGrid, "Decomp. Time", o => ((BenchmarkResult)o!).DecompressTimeText, 90, (a, b) => ((BenchmarkResult)a!).DecompressTimeMs.CompareTo(((BenchmarkResult)b!).DecompressTimeMs));
    AddColumn(this._resultsGrid, "Verified", o => ((BenchmarkResult)o!).VerifiedText, 60);

    AddColumn(this._summaryGrid, "Algorithm", o => ((BenchmarkSummary)o!).FormatName, 120);
    AddNumeric(this._summaryGrid, "Avg Ratio", o => ((BenchmarkSummary)o!).AvgRatioText, 80, (a, b) => ((BenchmarkSummary)a!).AvgRatio.CompareTo(((BenchmarkSummary)b!).AvgRatio));
    AddNumeric(this._summaryGrid, "Best Ratio", o => ((BenchmarkSummary)o!).BestRatioText, 80, (a, b) => ((BenchmarkSummary)a!).BestRatio.CompareTo(((BenchmarkSummary)b!).BestRatio));
    AddNumeric(this._summaryGrid, "Avg Comp. Speed", o => ((BenchmarkSummary)o!).AvgCompressSpeedText, 120, (a, b) => ((BenchmarkSummary)a!).AvgCompressSpeedKBs.CompareTo(((BenchmarkSummary)b!).AvgCompressSpeedKBs));
    AddNumeric(this._summaryGrid, "Avg Decomp. Speed", o => ((BenchmarkSummary)o!).AvgDecompressSpeedText, 130, (a, b) => ((BenchmarkSummary)a!).AvgDecompressSpeedKBs.CompareTo(((BenchmarkSummary)b!).AvgDecompressSpeedKBs));
    AddNumeric(this._summaryGrid, "Tests", o => ((BenchmarkSummary)o!).TestCount.ToString(), 50, (a, b) => ((BenchmarkSummary)a!).TestCount.CompareTo(((BenchmarkSummary)b!).TestCount));
    AddColumn(this._summaryGrid, "All OK", o => ((BenchmarkSummary)o!).AllVerifiedText, 60);

    this._resultsGrid.DataSource = this._results;
    this._summaryGrid.DataSource = this._summaries;

    var resultsTab = new TabPage("Results Table");
    this._resultsGrid.Dock = DockStyle.Fill;
    resultsTab.Controls.Add(this._resultsGrid);

    var summaryTab = new TabPage("Summary by Algorithm");
    this._summaryGrid.Dock = DockStyle.Fill;
    summaryTab.Controls.Add(this._summaryGrid);

    this._tabs.TabPages.Add(resultsTab);
    this._tabs.TabPages.Add(summaryTab);
    this.Controls.Add(this._tabs);
  }

  private static void AddColumn(DataGridView grid, string header, Func<object?, object?> selector, int width)
    => grid.Columns.Add(new DataGridViewColumn(header, selector) { Width = width });

  private static void AddNumeric(DataGridView grid, string header, Func<object?, object?> selector, int width, Comparison<object?> sort)
    => grid.Columns.Add(new DataGridViewColumn(header, selector) {
      Width = width,
      Alignment = Hawkynt.NativeForms.Drawing.ContentAlignment.MiddleRight,
      SortComparison = sort,
    });

  private void LayoutChildren() {
    const int Margin = 8;
    var width = this.ClientSize.Width - 2 * Margin;

    this._dataBox.Bounds = new(Margin, Margin, width, 82);

    var y = Margin + 86;
    this._algorithmBox.Bounds = new(Margin, y, width, 220);
    this._formats.Bounds = new(8, 48, Math.Max(100, width - 16), 164);

    y += 224;
    this._run.Bounds = new(Margin, y, 130, 28);
    this._stop.Bounds = new(Margin + 134, y, 70, 28);
    this._progress.Bounds = new(Margin + 212, y + 4, Math.Max(50, width - 212 - 180), 20);
    this._statusText.Bounds = new(this.ClientSize.Width - Margin - 172, y + 5, 172, 18);

    y += 32;
    var tabHeight = Math.Max(120, this.ClientSize.Height - y - 44);
    this._tabs.Bounds = new(Margin, y, width, tabHeight);

    y += tabHeight + 6;
    this._exportCsv.Bounds = new(Margin, y, 110, 26);
    this._totalTime.Bounds = new(Margin + 122, y + 4, 240, 18);
  }

  private void LoadFormats() {
    FormatRegistration.EnsureInitialized();

    // Benchmarks measure building blocks — raw algorithm primitives — never container formats.
    this._formatItems = [.. BuildingBlockRegistry.All
      .OrderBy(b => b.DisplayName)
      .Select(b => new FormatCheckItem {
        Id = b.Id,
        DisplayName = b.DisplayName,
        FamilyName = FamilyNames.GetValueOrDefault(b.Family, "Other"),
        Description = b.Description,
        FamilyColor = FamilyColors.GetValueOrDefault(b.Family, Color.White),
        Family = b.Family,
        IsSelected = true,
      })];

    this._formats.DataSource = this._formatItems;
    this.UpdateFormatCount();
  }

  private void UpdateFormatCount()
    => this._formatCount.Text = $"{this._formatItems.Count(f => f.IsSelected)} of {this._formatItems.Count} algorithms selected";

  private void SetSelection(Func<FormatCheckItem, bool> predicate) {
    foreach (var item in this._formatItems) item.IsSelected = predicate(item);
    this._formats.Invalidate();
    this.UpdateFormatCount();
  }

  private int GetDataSize() => DataSizes[Math.Clamp(this._dataSize.SelectedIndex, 0, DataSizes.Length - 1)].Bytes;

  private int GetIterations() => int.TryParse(this._iterations.SelectedItem as string, out var n) ? n : 3;

  private Dictionary<string, byte[]> GenerateTestData() {
    var size = this.GetDataSize();
    var data = new Dictionary<string, byte[]>();

    if (this._patterns["All Zeroes"].Checked)
      data["All Zeroes"] = new byte[size];

    if (this._patterns["Alternating"].Checked) {
      var buf = new byte[size];
      for (var i = 0; i < size; ++i) buf[i] = (byte)(i % 2 == 0 ? 0xAA : 0x55);
      data["Alternating"] = buf;
    }

    if (this._patterns["Incrementing"].Checked) {
      var buf = new byte[size];
      for (var i = 0; i < size; ++i) buf[i] = (byte)(i & 0xFF);
      data["Incrementing"] = buf;
    }

    if (this._patterns["Repeating"].Checked) {
      var pattern = "ABCDEFGHIJKLMNOP"u8.ToArray();
      var buf = new byte[size];
      for (var i = 0; i < size; ++i) buf[i] = pattern[i % pattern.Length];
      data["Repeating"] = buf;
    }

    if (this._patterns["English Text"].Checked) {
      var textBytes = Encoding.UTF8.GetBytes(
        "The quick brown fox jumps over the lazy dog. " +
        "Compression algorithms vary greatly in speed and ratio. " +
        "Some prioritize speed while others maximize compression. " +
        "Context mixing achieves the best ratios but is very slow. " +
        "LZ77 variants are fast and widely used in practice. ");
      var buf = new byte[size];
      for (var i = 0; i < size; ++i) buf[i] = textBytes[i % textBytes.Length];
      data["English Text"] = buf;
    }

    if (this._patterns["Random"].Checked) {
      var buf = new byte[size];
      new Random(42).NextBytes(buf);
      data["Random"] = buf;
    }

    if (this._patterns["Binary Struct"].Checked) {
      var buf = new byte[size];
      var rng = new Random(123);
      for (var i = 0; i < size; ++i)
        buf[i] = (i % 16) switch {
          0 or 1 or 2 or 3 => (byte)(i / 16 & 0xFF),
          4 or 5 => 0,
          6 or 7 => (byte)(i % 3),
          _ => (byte)rng.Next(256),
        };
      data["Binary Struct"] = buf;
    }

    return data;
  }

  private async Task RunBenchmarkAsync() {
    var selectedFormats = this._formatItems.Where(f => f.IsSelected).ToList();
    if (selectedFormats.Count == 0) {
      MessageBox.Show(this, "No algorithms selected.", "Benchmark", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return;
    }

    var testData = this.GenerateTestData();
    if (testData.Count == 0) {
      MessageBox.Show(this, "No test data patterns selected.", "Benchmark", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return;
    }

    var iterations = this.GetIterations();
    this._cts = new();
    this._run.Enabled = false;
    this._stop.Enabled = true;
    this._results.Clear();
    this._summaries.Clear();

    var totalSteps = selectedFormats.Count * testData.Count;
    var currentStep = 0;
    var totalSw = Stopwatch.StartNew();
    var token = this._cts.Token;

    try {
      foreach (var format in selectedFormats) {
        if (token.IsCancellationRequested) break;

        var block = BuildingBlockRegistry.GetById(format.Id);
        if (block is null) continue;

        foreach (var (patternName, inputData) in testData) {
          if (token.IsCancellationRequested) break;

          ++currentStep;
          this._progress.Value = (int)((double)currentStep / totalSteps * 100);
          this._statusText.Text = $"[{currentStep}/{totalSteps}] {format.DisplayName} / {patternName}...";

          // Context mixing is bit-level and inherently slow, so it gets a longer budget and a
          // single iteration rather than being reported as a timeout.
          var isContextMixing = format.Family == AlgorithmFamily.ContextMixing;
          var timeoutMs = isContextMixing ? PerTestTimeoutMs * 12 : PerTestTimeoutMs;
          var effectiveIterations = isContextMixing ? 1 : iterations;

          using var testCts = CancellationTokenSource.CreateLinkedTokenSource(token);
          testCts.CancelAfter(timeoutMs);

          var result = await Task.Run(
            () => RunBuildingBlockBenchmark(format, block, patternName, inputData, effectiveIterations, testCts.Token),
            token);
          if (result is not null) this._results.Add(result);
        }
      }
    } catch (OperationCanceledException) {
      // Stopped by the user.
    }

    totalSw.Stop();
    this.BuildSummaries();

    this._progress.Value = 100;
    this._statusText.Text = token.IsCancellationRequested ? "Stopped" : "Done";
    this._totalTime.Text = $"Total time: {totalSw.Elapsed.TotalSeconds:F1}s";
    this._run.Enabled = true;
    this._stop.Enabled = false;
  }

  private static BenchmarkResult RunBuildingBlockBenchmark(
    FormatCheckItem format, IBuildingBlock block,
    string patternName, byte[] inputData, int iterations, CancellationToken token) {
    try {
      token.ThrowIfCancellationRequested();
      var compressed = block.Compress(inputData);

      token.ThrowIfCancellationRequested();
      var decompressed = block.Decompress(compressed);
      var verified = decompressed.Length == inputData.Length && decompressed.AsSpan().SequenceEqual(inputData);

      token.ThrowIfCancellationRequested();
      var compSw = Stopwatch.StartNew();
      for (var i = 0; i < iterations; ++i) {
        token.ThrowIfCancellationRequested();
        block.Compress(inputData);
      }
      compSw.Stop();
      var compressTimeMs = compSw.Elapsed.TotalMilliseconds / iterations;

      var decSw = Stopwatch.StartNew();
      for (var i = 0; i < iterations; ++i) {
        token.ThrowIfCancellationRequested();
        block.Decompress(compressed);
      }
      decSw.Stop();
      var decompressTimeMs = decSw.Elapsed.TotalMilliseconds / iterations;

      return new() {
        FormatName = format.DisplayName,
        FormatId = format.Id,
        DataPattern = patternName,
        OriginalSize = inputData.Length,
        CompressedSize = compressed.Length,
        Ratio = inputData.Length > 0 ? (double)compressed.Length / inputData.Length : 1.0,
        CompressTimeMs = compressTimeMs,
        DecompressTimeMs = decompressTimeMs,
        CompressSpeedKBs = compressTimeMs > 0 ? inputData.Length / 1024.0 / compressTimeMs * 1000.0 : 0,
        DecompressSpeedKBs = decompressTimeMs > 0 ? inputData.Length / 1024.0 / decompressTimeMs * 1000.0 : 0,
        Verified = verified,
      };
    } catch (Exception ex) {
      return Failure(format, patternName, inputData.Length,
        ex is OperationCanceledException ? "Timeout" : ex.Message);
    }
  }

  private static BenchmarkResult Failure(FormatCheckItem format, string patternName, int originalSize, string error) => new() {
    FormatName = format.DisplayName,
    FormatId = format.Id,
    DataPattern = patternName,
    OriginalSize = originalSize,
    CompressedSize = -1,
    Ratio = -1,
    CompressTimeMs = -1,
    DecompressTimeMs = -1,
    CompressSpeedKBs = -1,
    DecompressSpeedKBs = -1,
    Verified = false,
    Error = error,
  };

  private void BuildSummaries() {
    var groups = this._results
      .OfType<BenchmarkResult>()
      .Where(r => r.Error is null)
      .GroupBy(r => r.FormatId)
      .OrderBy(g => g.First().FormatName);

    foreach (var group in groups) {
      var items = group.ToList();
      this._summaries.Add(new BenchmarkSummary {
        FormatName = items[0].FormatName,
        AvgRatio = items.Average(r => r.Ratio),
        BestRatio = items.Min(r => r.Ratio),
        AvgCompressSpeedKBs = items.Average(r => r.CompressSpeedKBs),
        AvgDecompressSpeedKBs = items.Average(r => r.DecompressSpeedKBs),
        TestCount = items.Count,
        AllVerified = items.All(r => r.Verified),
      });
    }
  }

  private void OnExportCsv() {
    if (this._results.Count == 0) {
      MessageBox.Show(this, "No results to export.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
      return;
    }

    var dialog = new SaveFileDialog { Filter = "CSV Files|*.csv", FileName = "benchmark_results.csv" };
    if (dialog.ShowDialog() != DialogResult.OK) return;

    var sb = new StringBuilder();
    sb.AppendLine("Algorithm,Data Pattern,Original Size,Compressed Size,Ratio,Compress Speed (KB/s),Decompress Speed (KB/s),Compress Time (ms),Decompress Time (ms),Verified,Error");
    foreach (var r in this._results.OfType<BenchmarkResult>()) {
      sb.Append(CultureInfo.InvariantCulture, $"\"{r.FormatName}\",\"{r.DataPattern}\",");
      sb.Append(CultureInfo.InvariantCulture, $"{r.OriginalSize},{r.CompressedSize},{r.Ratio:F4},");
      sb.Append(CultureInfo.InvariantCulture, $"{r.CompressSpeedKBs:F1},{r.DecompressSpeedKBs:F1},");
      sb.Append(CultureInfo.InvariantCulture, $"{r.CompressTimeMs:F2},{r.DecompressTimeMs:F2},");
      sb.AppendLine(CultureInfo.InvariantCulture, $"{r.Verified},\"{r.Error ?? ""}\"");
    }

    File.WriteAllText(dialog.FileName, sb.ToString());
    this._statusText.Text = $"Exported to {Path.GetFileName(dialog.FileName)}";
  }
}
