using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Compression.Lib;
using Compression.Registry;

using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Compression.UI.Views;

public partial class BenchmarkWindow : Window {

  private CancellationTokenSource? _cts;
  private readonly ObservableCollection<BenchmarkResult> _results = [];
  private readonly ObservableCollection<BenchmarkSummary> _summaries = [];

  /// <summary>Per-test timeout: 10 seconds for compress+decompress+verify+benchmark.</summary>
  private const int PerTestTimeoutMs = 10_000;

  // Family → brush color mapping, derived from AlgorithmFamily enum.
  private static readonly Dictionary<AlgorithmFamily, SolidColorBrush> FamilyBrushes = new() {
    [AlgorithmFamily.Dictionary] = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE)),
    [AlgorithmFamily.Entropy] = new SolidColorBrush(Color.FromRgb(0xF0, 0xE8, 0xFE)),
    [AlgorithmFamily.Transform] = new SolidColorBrush(Color.FromRgb(0xE8, 0xFE, 0xF0)),
    [AlgorithmFamily.ContextMixing] = new SolidColorBrush(Color.FromRgb(0xFE, 0xF0, 0xE8)),
    [AlgorithmFamily.Classic] = new SolidColorBrush(Color.FromRgb(0xFE, 0xE8, 0xF0)),
    [AlgorithmFamily.Encoding] = new SolidColorBrush(Color.FromRgb(0xE8, 0xFE, 0xFE)),
    [AlgorithmFamily.Archive] = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xE8)),
    [AlgorithmFamily.Other] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
  };

  // Family → display name mapping.
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

  private List<FormatCheckItem> _formatItems = [];

  public BenchmarkWindow() {
    InitializeComponent();
    ResultsGrid.ItemsSource = _results;
    SummaryGrid.ItemsSource = _summaries;
    LoadFormats();
  }

  private void LoadFormats() {
    FormatRegistration.EnsureInitialized();

    // Building blocks from Compression.Core — raw algorithm primitives for benchmarking
    _formatItems = BuildingBlockRegistry.All
      .OrderBy(b => b.DisplayName)
      .Select(b => new FormatCheckItem {
        Id = b.Id,
        DisplayName = b.DisplayName,
        FamilyName = FamilyNames.GetValueOrDefault(b.Family, "Other"),
        Description = b.Description,
        FamilyBrush = FamilyBrushes.GetValueOrDefault(b.Family, FamilyBrushes[AlgorithmFamily.Other]),
        Family = b.Family,
        IsSelected = true,
        IsBuildingBlock = true
      })
      .ToList();
    FormatListView.ItemsSource = _formatItems;
    UpdateFormatCount();
  }

  private void UpdateFormatCount() {
    var selected = _formatItems.Count(f => f.IsSelected);
    FormatCountText.Text = $"{selected} of {_formatItems.Count} algorithms selected";
  }

  private void SetSelection(Func<FormatCheckItem, bool> predicate) {
    foreach (var item in _formatItems)
      item.IsSelected = predicate(item);
    FormatListView.Items.Refresh();
    UpdateFormatCount();
  }

  private void OnSelectAll(object sender, RoutedEventArgs e) => SetSelection(_ => true);
  private void OnSelectNone(object sender, RoutedEventArgs e) => SetSelection(_ => false);
  private void OnSelectInvert(object sender, RoutedEventArgs e) => SetSelection(f => !f.IsSelected);
  private void OnSelectDictionary(object sender, RoutedEventArgs e) => SetSelection(f => f.FamilyName == "Dictionary");
  private void OnSelectEntropy(object sender, RoutedEventArgs e) => SetSelection(f => f.FamilyName == "Entropy");
  private void OnSelectTransforms(object sender, RoutedEventArgs e) => SetSelection(f => f.FamilyName == "Transform");
  private void OnSelectContextMixing(object sender, RoutedEventArgs e) => SetSelection(f => f.FamilyName == "Mixing");
  private void OnSelectClassic(object sender, RoutedEventArgs e) => SetSelection(f => f.FamilyName == "Classic");
  private void OnSelectEncoding(object sender, RoutedEventArgs e) => SetSelection(f => f.FamilyName == "Encoding");

  private void OnFormatCheckChanged(object sender, RoutedEventArgs e) {
    UpdateFormatCount();
  }

  private int GetDataSize() {
    if (CmbDataSize.SelectedItem is ComboBoxItem item && item.Tag is string tag)
      return int.Parse(tag, CultureInfo.InvariantCulture);
    return 262144;
  }

  private int GetIterations() {
    if (CmbIterations.SelectedItem is ComboBoxItem item && item.Tag is string tag)
      return int.Parse(tag, CultureInfo.InvariantCulture);
    return 3;
  }

  private Dictionary<string, byte[]> GenerateTestData() {
    var size = GetDataSize();
    var data = new Dictionary<string, byte[]>();

    if (ChkZeroes.IsChecked == true) {
      data["All Zeroes"] = new byte[size];
    }

    if (ChkAlternating.IsChecked == true) {
      var buf = new byte[size];
      for (var i = 0; i < size; i++)
        buf[i] = (byte)(i % 2 == 0 ? 0xAA : 0x55);
      data["Alternating"] = buf;
    }

    if (ChkIncrementing.IsChecked == true) {
      var buf = new byte[size];
      for (var i = 0; i < size; i++)
        buf[i] = (byte)(i & 0xFF);
      data["Incrementing"] = buf;
    }

    if (ChkRepeating.IsChecked == true) {
      var pattern = "ABCDEFGHIJKLMNOP"u8.ToArray();
      var buf = new byte[size];
      for (var i = 0; i < size; i++)
        buf[i] = pattern[i % pattern.Length];
      data["Repeating"] = buf;
    }

    if (ChkText.IsChecked == true) {
      var text = "The quick brown fox jumps over the lazy dog. " +
                 "Compression algorithms vary greatly in speed and ratio. " +
                 "Some prioritize speed while others maximize compression. " +
                 "Context mixing achieves the best ratios but is very slow. " +
                 "LZ77 variants are fast and widely used in practice. ";
      var textBytes = Encoding.UTF8.GetBytes(text);
      var buf = new byte[size];
      for (var i = 0; i < size; i++)
        buf[i] = textBytes[i % textBytes.Length];
      data["English Text"] = buf;
    }

    if (ChkRandom.IsChecked == true) {
      var rng = new Random(42);
      var buf = new byte[size];
      rng.NextBytes(buf);
      data["Random"] = buf;
    }

    if (ChkBinary.IsChecked == true) {
      var buf = new byte[size];
      var rng = new Random(123);
      for (var i = 0; i < size; i++) {
        buf[i] = (i % 16) switch {
          0 or 1 or 2 or 3 => (byte)(i / 16 & 0xFF),
          4 or 5 => 0,
          6 or 7 => (byte)(i % 3),
          _ => (byte)rng.Next(256),
        };
      }
      data["Binary Struct"] = buf;
    }

    return data;
  }

  private async void OnRunBenchmark(object sender, RoutedEventArgs e) {
    var selectedFormats = _formatItems.Where(f => f.IsSelected).ToList();
    if (selectedFormats.Count == 0) {
      System.Windows.MessageBox.Show("No algorithms selected.", "Benchmark", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    var testData = GenerateTestData();
    if (testData.Count == 0) {
      System.Windows.MessageBox.Show("No test data patterns selected.", "Benchmark", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
    }

    var iterations = GetIterations();
    _cts = new CancellationTokenSource();
    RunBtn.IsEnabled = false;
    StopBtn.IsEnabled = true;
    _results.Clear();
    _summaries.Clear();

    var totalSteps = selectedFormats.Count * testData.Count;
    var currentStep = 0;
    var totalSw = Stopwatch.StartNew();
    var token = _cts.Token;

    try {
      foreach (var format in selectedFormats) {
        if (token.IsCancellationRequested) break;

        var block = BuildingBlockRegistry.GetById(format.Id);
        if (block == null) continue;

        foreach (var (patternName, inputData) in testData) {
          if (token.IsCancellationRequested) break;

          currentStep++;
          var step = currentStep;
          var pct = (double)step / totalSteps * 100;

          Dispatcher.Invoke(() => {
            ProgressBar.Value = pct;
            StatusText.Text = $"[{step}/{totalSteps}] {format.DisplayName} / {patternName}...";
          });

          // Per-test timeout: longer for context-mixing (inherently slow bit-level compressors)
          var isContextMixing = format.Family == AlgorithmFamily.ContextMixing;
          var timeoutMs = isContextMixing ? PerTestTimeoutMs * 12 : PerTestTimeoutMs;
          var effectiveIter = isContextMixing ? 1 : iterations;
          using var testCts = CancellationTokenSource.CreateLinkedTokenSource(token);
          testCts.CancelAfter(timeoutMs);
          var testToken = testCts.Token;

          BenchmarkResult? result;
          result = await Task.Run(() => RunBuildingBlockBenchmark(format, block, patternName, inputData, effectiveIter, testToken), token);
          if (result != null) {
            Dispatcher.Invoke(() => _results.Add(result));
          }
        }
      }
    }
    catch (OperationCanceledException) {
      // Stopped by user
    }

    totalSw.Stop();

    BuildSummaries();

    Dispatcher.Invoke(() => {
      ProgressBar.Value = 100;
      StatusText.Text = token.IsCancellationRequested ? "Stopped" : "Done";
      TotalTimeText.Text = $"Total time: {totalSw.Elapsed.TotalSeconds:F1}s";
      RunBtn.IsEnabled = true;
      StopBtn.IsEnabled = false;
    });
  }

  private static BenchmarkResult? RunBuildingBlockBenchmark(
    FormatCheckItem format, IBuildingBlock block,
    string patternName, byte[] inputData, int iterations,
    CancellationToken token) {

    try {
      token.ThrowIfCancellationRequested();

      // Compress
      var compressed = block.Compress(inputData);

      token.ThrowIfCancellationRequested();

      // Verify round-trip
      var decompressed = block.Decompress(compressed);
      var verified = decompressed.Length == inputData.Length
                     && decompressed.AsSpan().SequenceEqual(inputData);

      token.ThrowIfCancellationRequested();

      // Benchmark compression
      var compSw = Stopwatch.StartNew();
      for (var i = 0; i < iterations; i++) {
        token.ThrowIfCancellationRequested();
        block.Compress(inputData);
      }
      compSw.Stop();
      var compressTimeMs = compSw.Elapsed.TotalMilliseconds / iterations;

      // Benchmark decompression
      var decSw = Stopwatch.StartNew();
      for (var i = 0; i < iterations; i++) {
        token.ThrowIfCancellationRequested();
        block.Decompress(compressed);
      }
      decSw.Stop();
      var decompressTimeMs = decSw.Elapsed.TotalMilliseconds / iterations;

      var ratio = inputData.Length > 0 ? (double)compressed.Length / inputData.Length : 1.0;
      var compSpeed = compressTimeMs > 0 ? inputData.Length / 1024.0 / compressTimeMs * 1000.0 : 0;
      var decSpeed = decompressTimeMs > 0 ? inputData.Length / 1024.0 / decompressTimeMs * 1000.0 : 0;

      return new BenchmarkResult {
        FormatName = format.DisplayName,
        FormatId = format.Id,
        DataPattern = patternName,
        OriginalSize = inputData.Length,
        CompressedSize = compressed.Length,
        Ratio = ratio,
        CompressTimeMs = compressTimeMs,
        DecompressTimeMs = decompressTimeMs,
        CompressSpeedKBs = compSpeed,
        DecompressSpeedKBs = decSpeed,
        Verified = verified
      };
    }
    catch (OperationCanceledException) {
      return new BenchmarkResult {
        FormatName = format.DisplayName,
        FormatId = format.Id,
        DataPattern = patternName,
        OriginalSize = inputData.Length,
        CompressedSize = -1,
        Ratio = -1,
        CompressTimeMs = -1,
        DecompressTimeMs = -1,
        CompressSpeedKBs = -1,
        DecompressSpeedKBs = -1,
        Verified = false,
        Error = "Timeout (>60s)"
      };
    }
    catch (Exception ex) {
      return new BenchmarkResult {
        FormatName = format.DisplayName,
        FormatId = format.Id,
        DataPattern = patternName,
        OriginalSize = inputData.Length,
        CompressedSize = -1,
        Ratio = -1,
        CompressTimeMs = -1,
        DecompressTimeMs = -1,
        CompressSpeedKBs = -1,
        DecompressSpeedKBs = -1,
        Verified = false,
        Error = ex.Message
      };
    }
  }

  private void BuildSummaries() {
    var groups = _results
      .Where(r => r.Error == null)
      .GroupBy(r => r.FormatId)
      .OrderBy(g => g.First().FormatName);

    foreach (var group in groups) {
      var items = group.ToList();
      _summaries.Add(new BenchmarkSummary {
        FormatName = items[0].FormatName,
        AvgRatio = items.Average(r => r.Ratio),
        BestRatio = items.Min(r => r.Ratio),
        AvgCompressSpeedKBs = items.Average(r => r.CompressSpeedKBs),
        AvgDecompressSpeedKBs = items.Average(r => r.DecompressSpeedKBs),
        TestCount = items.Count,
        AllVerified = items.All(r => r.Verified)
      });
    }
  }

  private void OnStopBenchmark(object sender, RoutedEventArgs e) {
    _cts?.Cancel();
    StatusText.Text = "Stopping...";
  }

  private void OnExportCsv(object sender, RoutedEventArgs e) {
    if (_results.Count == 0) {
      System.Windows.MessageBox.Show("No results to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
      return;
    }

    var dlg = new Microsoft.Win32.SaveFileDialog {
      Filter = "CSV Files|*.csv",
      FileName = "benchmark_results.csv"
    };
    if (dlg.ShowDialog() != true) return;

    var sb = new StringBuilder();
    sb.AppendLine("Algorithm,Data Pattern,Original Size,Compressed Size,Ratio,Compress Speed (KB/s),Decompress Speed (KB/s),Compress Time (ms),Decompress Time (ms),Verified,Error");
    foreach (var r in _results) {
      sb.Append(CultureInfo.InvariantCulture, $"\"{r.FormatName}\",\"{r.DataPattern}\",");
      sb.Append(CultureInfo.InvariantCulture, $"{r.OriginalSize},{r.CompressedSize},{r.Ratio:F4},");
      sb.Append(CultureInfo.InvariantCulture, $"{r.CompressSpeedKBs:F1},{r.DecompressSpeedKBs:F1},");
      sb.Append(CultureInfo.InvariantCulture, $"{r.CompressTimeMs:F2},{r.DecompressTimeMs:F2},");
      sb.AppendLine(CultureInfo.InvariantCulture, $"{r.Verified},\"{r.Error ?? ""}\"");
    }
    File.WriteAllText(dlg.FileName, sb.ToString());
    StatusText.Text = $"Exported to {Path.GetFileName(dlg.FileName)}";
  }
}

// Public classes for WPF data binding — must be public for the binding engine to access properties.

public sealed class FormatCheckItem : INotifyPropertyChanged {
  private bool _isSelected;

  public required string Id { get; init; }
  public required string DisplayName { get; init; }
  public required string FamilyName { get; init; }
  public required string Description { get; init; }
  public required SolidColorBrush FamilyBrush { get; init; }
  public AlgorithmFamily Family { get; init; }
  public bool IsBuildingBlock { get; init; }

  public bool IsSelected {
    get => _isSelected;
    set {
      if (_isSelected == value) return;
      _isSelected = value;
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    }
  }

  public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class BenchmarkResult {
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

  public string OriginalSizeText => OriginalSize >= 0 ? FormatSize(OriginalSize) : "N/A";
  public string CompressedSizeText => CompressedSize >= 0 ? FormatSize(CompressedSize) : "ERR";
  public string RatioText => Ratio >= 0 ? Ratio.ToString("P1", CultureInfo.InvariantCulture) : "ERR";
  public string CompressSpeedText => CompressSpeedKBs >= 0 ? FormatSpeed(CompressSpeedKBs) : "ERR";
  public string DecompressSpeedText => DecompressSpeedKBs >= 0 ? FormatSpeed(DecompressSpeedKBs) : "ERR";
  public string CompressTimeText => CompressTimeMs >= 0 ? $"{CompressTimeMs:F2} ms" : "ERR";
  public string DecompressTimeText => DecompressTimeMs >= 0 ? $"{DecompressTimeMs:F2} ms" : "ERR";
  public string VerifiedText => Error != null ? "ERR" : Verified ? "OK" : "FAIL";

  private static string FormatSize(int bytes) => bytes switch {
    >= 1048576 => $"{bytes / 1048576.0:F1} MB",
    >= 1024 => $"{bytes / 1024.0:F1} KB",
    _ => $"{bytes} B"
  };

  private static string FormatSpeed(double kbs) => kbs switch {
    >= 1024 * 1024 => $"{kbs / (1024 * 1024):F1} GB/s",
    >= 1024 => $"{kbs / 1024:F1} MB/s",
    _ => $"{kbs:F0} KB/s"
  };
}

public sealed class BenchmarkSummary {
  public required string FormatName { get; init; }
  public double AvgRatio { get; init; }
  public double BestRatio { get; init; }
  public double AvgCompressSpeedKBs { get; init; }
  public double AvgDecompressSpeedKBs { get; init; }
  public int TestCount { get; init; }
  public bool AllVerified { get; init; }

  public string AvgRatioText => AvgRatio.ToString("P1", CultureInfo.InvariantCulture);
  public string BestRatioText => BestRatio.ToString("P1", CultureInfo.InvariantCulture);
  public string AvgCompressSpeedText => FormatSpeed(AvgCompressSpeedKBs);
  public string AvgDecompressSpeedText => FormatSpeed(AvgDecompressSpeedKBs);
  public string AllVerifiedText => AllVerified ? "OK" : "FAIL";

  private static string FormatSpeed(double kbs) => kbs switch {
    >= 1024 * 1024 => $"{kbs / (1024 * 1024):F1} GB/s",
    >= 1024 => $"{kbs / 1024:F1} MB/s",
    _ => $"{kbs:F0} KB/s"
  };
}
