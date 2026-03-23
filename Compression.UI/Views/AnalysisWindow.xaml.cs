using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compression.Analysis;
using Compression.Analysis.Fingerprinting;
using Compression.Analysis.Scanning;
using Compression.Analysis.Statistics;
using Compression.Analysis.TrialDecompression;
using Compression.UI.Converters;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using RadioButton = System.Windows.Controls.RadioButton;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Compression.UI.Views;

public partial class AnalysisWindow : Window {

  private string? _filePath;
  private byte[]? _fileData;
  private AnalysisResult? _lastResult;
  private List<RegionProfile>? _entropyRegions;
  private const string CliPlaceholder = "CLI flags, e.g.: --deep-scan --max-depth 5 --offset 1024";

  // Map each bar rectangle to its region index
  private readonly Dictionary<Rectangle, int> _barRegionMap = [];

  // All tool panels for toolbar switching
  private UIElement[]? _toolPanels;
  private string _lastTool = "Scan";

  public AnalysisWindow() {
    InitializeComponent();
    CliOverrideBox.Text = CliPlaceholder;
    _toolPanels = [SignaturesGrid, FingerprintsGrid, EntropyPanel, TrialPanel, ChainPanel,
                   AnalysisStatsControl, StringsControl, StructureControl];
  }

  private void OnToolSelected(object sender, RoutedEventArgs e) {
    if (_toolPanels == null || !IsInitialized) return;

    foreach (var panel in _toolPanels)
      panel.Visibility = Visibility.Collapsed;

    var rb = sender as RadioButton;
    var name = rb?.Name ?? "";
    UIElement? target = name switch {
      "ToolScan" => SignaturesGrid,
      "ToolFingerprint" => FingerprintsGrid,
      "ToolEntropy" => EntropyPanel,
      "ToolTrial" => TrialPanel,
      "ToolChain" => ChainPanel,
      "ToolStats" => AnalysisStatsControl,
      "ToolStrings" => StringsControl,
      "ToolStructure" => StructureControl,
      _ => SignaturesGrid,
    };
    if (target != null) target.Visibility = Visibility.Visible;
    _lastTool = name;

    if (name == "ToolStats" && _fileData != null)
      AnalysisStatsControl.Data = _fileData;
    if (name == "ToolStrings" && _fileData != null)
      StringsControl.Data = _fileData;
    if (name == "ToolStructure" && _fileData != null)
      StructureControl.Data = _fileData;
  }

  internal void RunAnalysis(string fileName, byte[] data) {
    _filePath = fileName;
    _fileData = data;
    FilePathBox.Text = fileName;
    FilePathBox.Foreground = Brushes.Black;
    RunBtn.IsEnabled = true;
    ExecuteAnalysis();
  }

  private void OnBrowse(object sender, RoutedEventArgs e) {
    var dlg = new Microsoft.Win32.OpenFileDialog {
      Title = "Select file to analyze",
      Filter = "All Files|*.*",
    };
    if (dlg.ShowDialog() == true)
      LoadFile(dlg.FileName);
  }

  private void OnFileDrop(object sender, DragEventArgs e) {
    if (e.Data.GetDataPresent(DataFormats.FileDrop)) {
      var files = (string[])e.Data.GetData(DataFormats.FileDrop);
      if (files.Length > 0)
        LoadFile(files[0]);
    }
  }

  private void OnDragOver(object sender, DragEventArgs e) {
    e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
      ? DragDropEffects.Copy : DragDropEffects.None;
    e.Handled = true;
  }

  private void LoadFile(string path) {
    try {
      _fileData = File.ReadAllBytes(path);
      _filePath = path;
      FilePathBox.Text = path;
      FilePathBox.Foreground = Brushes.Black;
      RunBtn.IsEnabled = true;
      ExecuteAnalysis();
    }
    catch (Exception ex) {
      MessageBox.Show($"Failed to read file: {ex.Message}", "Error",
        MessageBoxButton.OK, MessageBoxImage.Error);
    }
  }

  private void OnRunAnalysis(object sender, RoutedEventArgs e) {
    if (_fileData == null) return;
    ExecuteAnalysis();
  }

  private void OnAllChecked(object sender, RoutedEventArgs e) {
    if (!IsInitialized) return;
    ChkDeepScan.IsChecked = true;
    ChkFingerprint.IsChecked = true;
    ChkTrial.IsChecked = true;
    ChkEntropyMap.IsChecked = true;
    ChkChain.IsChecked = true;
  }

  private void OnAllUnchecked(object sender, RoutedEventArgs e) { }

  private void OnCliGotFocus(object sender, RoutedEventArgs e) {
    if (CliOverrideBox.Text == CliPlaceholder) {
      CliOverrideBox.Text = "";
      CliOverrideBox.Foreground = Brushes.Black;
    }
  }

  private void OnCliLostFocus(object sender, RoutedEventArgs e) {
    if (string.IsNullOrWhiteSpace(CliOverrideBox.Text)) {
      CliOverrideBox.Text = CliPlaceholder;
      CliOverrideBox.Foreground = Brushes.Gray;
    }
  }

  private AnalysisOptions BuildOptions() {
    var cliText = CliOverrideBox.Text;
    if (!string.IsNullOrWhiteSpace(cliText) && cliText != CliPlaceholder)
      return ParseCliFlags(cliText);

    int.TryParse(TxtMaxDepth.Text, out var maxDepth);
    int.TryParse(TxtWindow.Text, out var window);
    long.TryParse(TxtOffset.Text, out var offset);
    long.TryParse(TxtLength.Text, out var length);
    int.TryParse(TxtTimeout.Text, out var timeout);

    if (maxDepth <= 0) maxDepth = 10;
    if (window <= 0) window = 256;
    if (timeout <= 0) timeout = 200;

    return new AnalysisOptions {
      All = ChkAll.IsChecked == true,
      DeepScan = ChkDeepScan.IsChecked == true,
      Fingerprint = ChkFingerprint.IsChecked == true,
      Trial = ChkTrial.IsChecked == true,
      EntropyMap = ChkEntropyMap.IsChecked == true,
      Chain = ChkChain.IsChecked == true,
      BoundaryDetection = ChkCUSUM.IsChecked == true,
      MaxDepth = maxDepth, WindowSize = window,
      Offset = offset, Length = length, PerTrialTimeoutMs = timeout,
    };
  }

  private static AnalysisOptions ParseCliFlags(string flags) {
    var args = flags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    bool deepScan = false, fingerprint = false, trial = false,
         entropyMap = false, chain = false, all = false;
    int maxDepth = 10, window = 256, timeout = 200;
    long offset = 0, length = 0;

    for (var i = 0; i < args.Length; i++) {
      switch (args[i]) {
        case "--all": all = true; break;
        case "--deep-scan": deepScan = true; break;
        case "--fingerprint": fingerprint = true; break;
        case "--trial": trial = true; break;
        case "--entropy-map": entropyMap = true; break;
        case "--chain": chain = true; break;
        case "--max-depth" when i + 1 < args.Length: int.TryParse(args[++i], out maxDepth); break;
        case "--window" when i + 1 < args.Length: int.TryParse(args[++i], out window); break;
        case "--offset" when i + 1 < args.Length: long.TryParse(args[++i], out offset); break;
        case "--length" when i + 1 < args.Length: long.TryParse(args[++i], out length); break;
        case "--timeout" when i + 1 < args.Length: int.TryParse(args[++i], out timeout); break;
      }
    }

    if (!all && !deepScan && !fingerprint && !trial && !entropyMap && !chain)
      all = true;

    return new AnalysisOptions {
      All = all, DeepScan = deepScan, Fingerprint = fingerprint,
      Trial = trial, EntropyMap = entropyMap, Chain = chain,
      MaxDepth = maxDepth, WindowSize = window,
      Offset = offset, Length = length, PerTrialTimeoutMs = timeout,
    };
  }

  private async void ExecuteAnalysis() {
    if (_fileData == null || _filePath == null) return;

    var options = BuildOptions();
    var data = _fileData;
    var fileName = _filePath;

    RunBtn.IsEnabled = false;
    StatusText.Text = "Analyzing...";
    Title = $"Analysis \u2014 {Path.GetFileName(fileName)}";

    var sw = Stopwatch.StartNew();

    AnalysisResult result;
    try {
      result = await System.Threading.Tasks.Task.Run(() => {
        var analyzer = new BinaryAnalyzer(options);
        return analyzer.Analyze(data);
      });
    }
    catch (Exception ex) {
      StatusText.Text = $"Error: {ex.Message}";
      RunBtn.IsEnabled = true;
      return;
    }

    sw.Stop();
    _lastResult = result;
    _entropyRegions = result.EntropyMap;
    StatusText.Text = $"Done ({sw.ElapsedMilliseconds}ms)";
    RunBtn.IsEnabled = true;

    PopulateResults(fileName, data, result);
  }

  private void PopulateResults(string fileName, byte[] data, AnalysisResult result) {
    FileInfoText.Text = $"{Path.GetFileName(fileName)}  ({data.Length:N0} bytes)";
    if (result.Statistics != null) {
      var s = result.Statistics;
      StatsSummaryText.Text = $"Entropy: {s.Entropy:F4}  Mean: {s.Mean:F1}  Chi\u00b2: {s.ChiSquare:F1}  Unique: {s.UniqueBytesCount}/256";
    }

    SignaturesGrid.ItemsSource = result.Signatures;
    FingerprintsGrid.ItemsSource = result.Fingerprints;
    EntropyGrid.ItemsSource = result.EntropyMap;
    TrialGrid.ItemsSource = result.TrialResults;

    BuildEntropyBar(result.EntropyMap, data.Length);

    if (result.Chain != null) {
      var sb = new StringBuilder();
      if (result.Chain.Depth == 0) {
        sb.AppendLine("No compression layers detected.");
      }
      else {
        for (var i = 0; i < result.Chain.Layers.Count; i++) {
          var l = result.Chain.Layers[i];
          sb.AppendLine($"Layer {i + 1}: {l.Algorithm}  ({l.InputSize:N0} \u2192 {l.OutputSize:N0} bytes)  confidence={l.Confidence:F2}");
        }
        sb.AppendLine();
        sb.AppendLine($"Final data: {result.Chain.FinalData.Length:N0} bytes");
      }
      ChainText.Text = sb.ToString();
    }
    else {
      ChainText.Text = "(Chain reconstruction not enabled)";
    }
  }

  // ── Entropy bar ──────────────────────────────────────────────────────

  private void BuildEntropyBar(List<RegionProfile>? regions, long fileSize) {
    EntropyBarCanvas.Children.Clear();
    _barRegionMap.Clear();
    if (regions == null || regions.Count == 0 || fileSize == 0) return;

    var totalSpan = fileSize > 0 ? fileSize : regions.Sum(r => (long)r.Length);
    if (totalSpan == 0) return;

    // Defer to Render priority to ensure canvas has a valid ActualWidth
    EntropyBarCanvas.Dispatcher.InvokeAsync(() => {
      var canvasWidth = EntropyBarCanvas.ActualWidth;
      // If still 0, try the parent border width
      if (canvasWidth <= 0 && EntropyBarCanvas.Parent is FrameworkElement parent)
        canvasWidth = parent.ActualWidth - 2;
      if (canvasWidth <= 0) canvasWidth = 800;

      double x = 0;
      for (var i = 0; i < regions.Count; i++) {
        var r = regions[i];
        double w;
        if (i == regions.Count - 1) {
          // Last region: fill remaining space to prevent white gap
          w = canvasWidth - x;
        }
        else {
          w = Math.Max(1, r.Length / (double)totalSpan * canvasWidth);
        }

        var color = EntropyToColorConverter.EntropyToColor(r.Entropy);

        var tooltip = $"Offset 0x{r.Offset:X} ({r.Offset:N0})\n" +
                      $"Length: {r.Length:N0} bytes\n" +
                      $"Entropy: {r.Entropy:F4} bits/byte\n" +
                      $"Chi\u00b2: {r.ChiSquare:F1}  Mean: {r.Mean:F1}\n" +
                      $"Type: {r.Classification}";

        var rect = new Rectangle {
          Width = w, Height = 26,
          Fill = new SolidColorBrush(color),
          ToolTip = tooltip,
          Cursor = System.Windows.Input.Cursors.Hand,
        };
        ToolTipService.SetInitialShowDelay(rect, 0);
        ToolTipService.SetShowDuration(rect, 30000);

        _barRegionMap[rect] = i;
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, 0);
        EntropyBarCanvas.Children.Add(rect);
        x += w;
      }
    }, System.Windows.Threading.DispatcherPriority.Render);
  }

  private void OnEntropyModeChanged(object sender, RoutedEventArgs e) {
    if (!IsInitialized || _fileData == null) return;
    // Re-run analysis with the new boundary detection mode
    ExecuteAnalysis();
  }

  private void OnEntropyBarSizeChanged(object sender, SizeChangedEventArgs e) {
    // Redraw entropy bar when canvas resizes
    if (_entropyRegions != null && _fileData != null)
      BuildEntropyBar(_entropyRegions, _fileData.Length);
  }

  private void OnEntropyBarClick(object sender, MouseButtonEventArgs e) {
    // Find which rectangle was clicked
    if (e.OriginalSource is Rectangle rect && _barRegionMap.TryGetValue(rect, out var index)) {
      if (_entropyRegions != null && index < _entropyRegions.Count) {
        EntropyGrid.SelectedIndex = index;
        EntropyGrid.ScrollIntoView(EntropyGrid.SelectedItem);
      }
    }
  }

  // ── Row coloring ─────────────────────────────────────────────────────

  private void OnEntropyRowLoading(object? sender, DataGridRowEventArgs e) {
    if (e.Row.DataContext is RegionProfile profile) {
      var color = EntropyToColorConverter.EntropyToColor(profile.Entropy);
      e.Row.Background = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B));
    }
  }

  /// <summary>Generates a unique hue for a string using golden ratio distribution to avoid collisions.</summary>
  private static Color FormatNameToColor(string name) {
    // Use a stable hash (sum of char codes * prime) to avoid .NET hash randomization
    uint hash = 0;
    foreach (var c in name) hash = hash * 31 + c;
    // Golden ratio distribution for maximum separation between hues
    var hue = (hash * 0.618033988749895) % 1.0;
    // HSL to RGB with fixed saturation=0.55, lightness=0.55
    var h = hue * 6.0;
    const double chroma = 0.5;
    var x = chroma * (1 - Math.Abs(h % 2 - 1));
    double r1, g1, b1;
    if (h < 1) { r1 = chroma; g1 = x; b1 = 0; }
    else if (h < 2) { r1 = x; g1 = chroma; b1 = 0; }
    else if (h < 3) { r1 = 0; g1 = chroma; b1 = x; }
    else if (h < 4) { r1 = 0; g1 = x; b1 = chroma; }
    else if (h < 5) { r1 = x; g1 = 0; b1 = chroma; }
    else { r1 = chroma; g1 = 0; b1 = x; }
    var m = 0.55 - chroma / 2;
    return Color.FromRgb((byte)((r1 + m) * 255), (byte)((g1 + m) * 255), (byte)((b1 + m) * 255));
  }

  private void OnSignatureRowLoading(object? sender, DataGridRowEventArgs e) {
    if (e.Row.DataContext is ScanResult scan) {
      var baseColor = FormatNameToColor(scan.FormatName);
      var alpha = (byte)(25 + scan.Confidence * 45);
      e.Row.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
    }
  }

  private void OnFingerprintRowLoading(object? sender, DataGridRowEventArgs e) {
    if (e.Row.DataContext is FingerprintResult fp) {
      var baseColor = FormatNameToColor(fp.Algorithm);
      var alpha = (byte)(25 + fp.Confidence * 50);
      e.Row.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
    }
  }

  private void OnTrialRowLoading(object? sender, DataGridRowEventArgs e) {
    if (e.Row.DataContext is DecompressionAttempt trial) {
      if (trial.Success) {
        // Green tint, intensity by output size ratio
        var alpha = (byte)Math.Min(50, 20 + trial.OutputSize / 500);
        e.Row.Background = new SolidColorBrush(Color.FromArgb(alpha, 70, 170, 70));
      }
      else {
        e.Row.Background = new SolidColorBrush(Color.FromArgb(15, 200, 50, 50));
      }
    }
  }

  // ── Double-click handlers ────────────────────────────────────────────

  private void OnEntropyGridDoubleClick(object sender, MouseButtonEventArgs e) {
    if (EntropyGrid.SelectedItem is RegionProfile region && _fileData != null) {
      var start = (int)Math.Min(region.Offset, _fileData.Length);
      var len = Math.Min(region.Length, _fileData.Length - start);
      if (len <= 0) return;

      var slice = new byte[len];
      Array.Copy(_fileData, start, slice, 0, len);

      var preview = new PreviewWindow();
      preview.ShowData(
        $"Region 0x{region.Offset:X}\u20130x{region.Offset + region.Length:X} ({region.Classification})",
        slice, hex: true, analyzeMode: true);
      preview.Show();
    }
  }

  private void OnTrialGridDoubleClick(object sender, MouseButtonEventArgs e) {
    var trial = TrialGrid.SelectedItem as DecompressionAttempt;
    if (trial?.Output != null && trial.Success) {
      var preview = new PreviewWindow();
      preview.ShowData($"{trial.Algorithm} output ({trial.OutputSize} bytes)", trial.Output, hex: false, analyzeMode: true);
      preview.Show();
    }
  }

  // ── Trial preview/save ───────────────────────────────────────────────

  private void OnPreviewTrialOutput(object sender, RoutedEventArgs e) {
    var trial = TrialGrid.SelectedItem as DecompressionAttempt;
    if (trial?.Output == null || !trial.Success) {
      MessageBox.Show("Select a successful trial result first.", "Preview",
        MessageBoxButton.OK, MessageBoxImage.Information);
      return;
    }
    var preview = new PreviewWindow();
    preview.ShowData($"{trial.Algorithm} output ({trial.OutputSize} bytes)", trial.Output, hex: false, analyzeMode: true);
    preview.Show();
  }

  private void OnSaveTrialOutput(object sender, RoutedEventArgs e) {
    var trial = TrialGrid.SelectedItem as DecompressionAttempt;
    if (trial?.Output == null || !trial.Success) {
      MessageBox.Show("Select a successful trial result first.", "Save",
        MessageBoxButton.OK, MessageBoxImage.Information);
      return;
    }
    var dlg = new Microsoft.Win32.SaveFileDialog {
      Title = "Save decompressed output",
      FileName = $"{trial.Algorithm}_output.bin",
      Filter = "All Files|*.*",
    };
    if (dlg.ShowDialog() == true) {
      File.WriteAllBytes(dlg.FileName, trial.Output);
      StatusText.Text = $"Saved {trial.OutputSize} bytes to {dlg.FileName}";
    }
  }

  // ── Chain preview/save ───────────────────────────────────────────────

  private void OnPreviewChainOutput(object sender, RoutedEventArgs e) {
    var chain = _lastResult?.Chain;
    if (chain == null || chain.FinalData.Length == 0) {
      MessageBox.Show("No chain data available.", "Preview",
        MessageBoxButton.OK, MessageBoxImage.Information);
      return;
    }
    var preview = new PreviewWindow();
    preview.ShowData($"Chain final data ({chain.FinalData.Length} bytes)", chain.FinalData, hex: false, analyzeMode: true);
    preview.Show();
  }

  private void OnSaveChainOutput(object sender, RoutedEventArgs e) {
    var chain = _lastResult?.Chain;
    if (chain == null || chain.FinalData.Length == 0) {
      MessageBox.Show("No chain data available.", "Save",
        MessageBoxButton.OK, MessageBoxImage.Information);
      return;
    }
    var dlg = new Microsoft.Win32.SaveFileDialog {
      Title = "Save final chain data",
      FileName = "chain_output.bin",
      Filter = "All Files|*.*",
    };
    if (dlg.ShowDialog() == true) {
      File.WriteAllBytes(dlg.FileName, chain.FinalData);
      StatusText.Text = $"Saved {chain.FinalData.Length} bytes to {dlg.FileName}";
    }
  }
}
