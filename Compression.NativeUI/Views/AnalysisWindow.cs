using System.Diagnostics;
using System.Drawing;
using Compression.Analysis;
using Compression.Analysis.ChainReconstruction;
using Compression.Analysis.Fingerprinting;
using Compression.Analysis.Scanning;
using Compression.Analysis.Statistics;
using Compression.Analysis.TrialDecompression;
using Compression.NativeUI.Controls;
using Compression.NativeUI.Theming;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Views;

/// <summary>One row of the reconstructed decompression chain.</summary>
internal sealed class ChainRow {
  public required int Layer { get; init; }
  public required string Algorithm { get; init; }
  public required string InputSize { get; init; }
  public required string OutputSize { get; init; }
  public required double Confidence { get; init; }
  public required string Status { get; init; }
}

/// <summary>
/// Inspects an arbitrary binary: magic scan, algorithm fingerprints, entropy map, trial
/// decompression, chain reconstruction, and the shared statistics, strings, structure and heatmap
/// panels. One tool is visible at a time, chosen from the toolbar.
/// </summary>
internal sealed class AnalysisWindow : Form {
  private const string CliPlaceholder = "CLI flags, e.g.: --deep-scan --max-depth 5 --offset 1024";
  private const int EntropyBarHeight = 28;

  private readonly GroupBox _fileBox = new() { Text = "File" };
  private readonly TextBox _filePath = new() { ReadOnly = true, Text = "Drop a file here or click Browse...", ForeColor = Color.Gray };
  private readonly Button _browse = new() { Text = "Browse..." };

  private readonly GroupBox _optionsBox = new() { Text = "Options" };
  private readonly CheckBox _all = new() { Text = "All" };
  private readonly CheckBox _deepScan = new() { Text = "Deep Scan", Checked = true };
  private readonly CheckBox _fingerprint = new() { Text = "Fingerprint", Checked = true };
  private readonly CheckBox _trial = new() { Text = "Trial Decompress" };
  private readonly CheckBox _entropyMap = new() { Text = "Entropy Map", Checked = true };
  private readonly CheckBox _chain = new() { Text = "Chain Reconstruct" };
  private readonly TextBox _maxDepth = new() { Text = "10" };
  private readonly TextBox _window = new() { Text = "256" };
  private readonly TextBox _offset = new() { Text = "0" };
  private readonly TextBox _length = new() { Text = "0" };
  private readonly TextBox _timeout = new() { Text = "200" };

  private readonly Button _run = new() { Text = "Run Analysis", Enabled = false };
  private readonly TextBox _cliOverride = new() { Text = CliPlaceholder, ForeColor = Color.Gray };
  private readonly ProgressBar _busy = new() { Style = ProgressBarStyle.Marquee, Visible = false };
  private readonly Label _status = new();

  private readonly Label _fileInfo = new();
  private readonly Label _statsSummary = new() { ForeColor = Color.Gray };

  private readonly ToolStrip _toolSelector = new();
  private readonly Dictionary<string, ToolStripButton> _toolButtons = [];

  private readonly DataGridView _signatures = new() { ReadOnly = true, ShowGridLines = true };
  private readonly DataGridView _fingerprints = new() { ReadOnly = true, ShowGridLines = true };
  private readonly Panel _entropyPanel = new();
  private readonly CheckBox _cusum = new() { Text = "CUSUM Boundary Detection" };
  private readonly EntropyBarControl _entropyBar = new();
  private readonly EntropyLegendControl _entropyLegend = new();
  private readonly DataGridView _entropyGrid = new() { ReadOnly = true, ShowGridLines = true };
  private readonly Panel _trialPanel = new();
  private readonly DataGridView _trialGrid = new() { ReadOnly = true, ShowGridLines = true };
  private readonly Button _previewTrial = new() { Text = "Preview Output" };
  private readonly Button _saveTrial = new() { Text = "Save Output..." };
  private readonly DataGridView _chainGrid = new() { ReadOnly = true, ShowGridLines = true };
  private readonly StatisticsControl _stats = new() { Visible = false };
  private readonly StringSearchControl _strings = new() { Visible = false };
  private readonly StructureViewControl _structure = new() { Visible = false };
  private readonly HeatmapGridControl _heatmap = new() { Visible = false };
  private readonly ToolTip _toolTips = new();

  private Control[] _toolPanels = [];
  private string? _currentPath;
  private byte[]? _fileData;
  private AnalysisResult? _lastResult;
  private List<RegionProfile>? _entropyRegions;

  public AnalysisWindow() {
    this.Text = "Binary Analysis";
    this.ClientSize = new(900, 750);
    this.MinimumSize = new(700, 500);
    this.StartPosition = FormStartPosition.CenterScreen;
    this.AllowDrop = true;

    this.BuildFileBox();
    this.BuildOptionsBox();
    this.BuildRunRow();
    this.BuildHeader();
    this.BuildToolSelector();
    this.BuildToolPanels();

    this.DragOver += (_, e) => e.Effect = DragDropEffects.Copy;
    this.DragDrop += (_, e) => {
      if (e.Data is string[] { Length: > 0 } files) this.LoadFile(files[0]);
    };

    this.Resize += (_, _) => this.LayoutChildren();
    this.LayoutChildren();
    this.SelectTool("Scan");
  }

  /// <summary>
  /// Leaves the elapsed time out of the status line. Documentation capture renders this window to a
  /// PNG that is committed, and a duration differs on every run, so the image would differ on every
  /// run — the capture bot would then push a commit whose own CI run cancels the one it was pushed
  /// into, and the changed bytes would conflict with every other branch carrying the same file.
  /// </summary>
  public bool OmitElapsedTime { get; set; }

  private void BuildFileBox() {
    this._browse.Image = Images.Icon(IconKeys.Browse, 14);
    this._browse.Click += (_, _) => {
      var dialog = new OpenFileDialog { Title = "Select file to analyze", Filter = "All Files|*.*" };
      if (dialog.ShowDialog() == DialogResult.OK) this.LoadFile(dialog.FileName);
    };

    this._fileBox.Controls.AddRange(this._filePath, this._browse);
    this.Controls.Add(this._fileBox);
  }

  private void BuildOptionsBox() {
    // "All" turns everything on; it deliberately does not turn anything off again.
    this._all.CheckedChanged += (_, _) => {
      if (!this._all.Checked) return;
      foreach (var box in new[] { this._deepScan, this._fingerprint, this._trial, this._entropyMap, this._chain })
        box.Checked = true;
    };

    this._toolTips.SetToolTip(this._trial, "SLOW — runs every registered decompressor with timeout. Enable on demand.");
    this._toolTips.SetToolTip(this._chain, "SLOW — recursive trial decompression. Enable on demand.");

    this._optionsBox.Controls.AddRange(
      this._all, this._deepScan, this._fingerprint, this._trial, this._entropyMap, this._chain,
      new Label { Text = "Max Depth:" }, this._maxDepth,
      new Label { Text = "Window:" }, this._window,
      new Label { Text = "Offset:" }, this._offset,
      new Label { Text = "Length:" }, this._length,
      new Label { Text = "Timeout (ms):" }, this._timeout);

    this.Controls.Add(this._optionsBox);
  }

  private void BuildRunRow() {
    this._run.Image = Images.Icon(IconKeys.Analyze, 16);
    this._run.Click += async (_, _) => {
      if (this._fileData is not null) await this.ExecuteAnalysisAsync();
    };

    this._toolTips.SetToolTip(this._cliOverride,
      "Optional CLI-style flags, e.g.: --deep-scan --max-depth 5 --offset 1024 --length 4096");
    this._cliOverride.GotFocus += (_, _) => {
      if (this._cliOverride.Text != CliPlaceholder) return;
      this._cliOverride.Text = "";
      this._cliOverride.ForeColor = Color.Black;
    };
    this._cliOverride.LostFocus += (_, _) => {
      if (!string.IsNullOrWhiteSpace(this._cliOverride.Text)) return;
      this._cliOverride.Text = CliPlaceholder;
      this._cliOverride.ForeColor = Color.Gray;
    };

    this._toolTips.SetToolTip(this._busy, "Analysis in progress — large files take time");
    this.Controls.AddRange(this._run, this._cliOverride, this._busy, this._status);
  }

  private void BuildHeader() {
    var font = DefaultTheme.Instance.DefaultFont;
    this._fileInfo.Font = new(font.Family, 11f, FontStyle.Bold);
    this.Controls.AddRange(this._fileInfo, this._statsSummary);
  }

  private void BuildToolSelector() {
    var tools = new (string Key, string Label)[] {
      ("Scan", "Scan Results"), ("Fingerprint", "Fingerprints"), ("Entropy", "Entropy Map"),
      ("Heatmap", "Heatmap"), ("Trial", "Trial Decompress"), ("Chain", "Chain"),
      ("Stats", "Statistics"), ("Strings", "Strings"), ("Structure", "Structure"),
    };

    foreach (var (key, label) in tools) {
      var button = new ToolStripButton(label) { CheckOnClick = true };
      var captured = key;
      button.CheckedChanged += (_, _) => {
        if (button.Checked) this.SelectTool(captured);
      };
      this._toolButtons[key] = button;

      // The separator sits where the WPF toolbar put it: before the shared panels.
      if (key == "Stats") this._toolSelector.Items.Add(new ToolStripSeparator());
      this._toolSelector.Items.Add(button);
    }

    this.Controls.Add(this._toolSelector);
  }

  private void BuildToolPanels() {
    AddColumn(this._signatures, "Offset", static o => ((ScanResult)o!).Offset, 80);
    AddColumn(this._signatures, "Format", static o => ((ScanResult)o!).FormatName, 120);
    AddColumn(this._signatures, "Confidence", static o => ((ScanResult)o!).Confidence.ToString("F2"), 80);
    AddColumn(this._signatures, "Magic Len", static o => ((ScanResult)o!).MagicLength, 70);
    AddColumn(this._signatures, "Header Preview", static o => ((ScanResult)o!).HeaderPreview, 400);
    this._signatures.RowBackColorSelector = static o => o is ScanResult s
      ? Tint(FormatNameToColor(s.FormatName), (byte)(25 + s.Confidence * 45))
      : null;

    AddColumn(this._fingerprints, "Algorithm", static o => ((FingerprintResult)o!).Algorithm, 150);
    AddColumn(this._fingerprints, "Confidence", static o => ((FingerprintResult)o!).Confidence.ToString("F2"), 80);
    AddColumn(this._fingerprints, "Explanation", static o => ((FingerprintResult)o!).Explanation, 500);
    this._fingerprints.RowBackColorSelector = static o => o is FingerprintResult f
      ? Tint(FormatNameToColor(f.Algorithm), (byte)(25 + f.Confidence * 50))
      : null;

    this.BuildEntropyPanel();
    this.BuildTrialPanel();

    AddColumn(this._chainGrid, "#", static o => ((ChainRow)o!).Layer, 30);
    AddColumn(this._chainGrid, "Algorithm", static o => ((ChainRow)o!).Algorithm, 200);
    AddColumn(this._chainGrid, "Input", static o => ((ChainRow)o!).InputSize, 90);
    AddColumn(this._chainGrid, "Output", static o => ((ChainRow)o!).OutputSize, 90);
    AddColumn(this._chainGrid, "Confidence", static o => ((ChainRow)o!).Confidence.ToString("F2"), 80);
    AddColumn(this._chainGrid, "Status", static o => ((ChainRow)o!).Status, 200);
    this._chainGrid.CellDoubleClick += (_, _) => this.PreviewChainLayer();

    this._toolPanels = [
      this._signatures, this._fingerprints, this._entropyPanel, this._trialPanel, this._chainGrid,
      this._stats, this._strings, this._structure, this._heatmap,
    ];

    foreach (var panel in this._toolPanels) {
      panel.Visible = false;
      panel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
      this.Controls.Add(panel);
    }
  }

  private void BuildEntropyPanel() {
    this._toolTips.SetToolTip(this._cusum,
      "Use CUSUM change-point detection instead of adaptive windowing. Better for large files with sharp transitions.");
    this._cusum.CheckedChanged += async (_, _) => {
      if (this._fileData is not null) await this.ExecuteAnalysisAsync();
    };

    this._entropyBar.RegionClicked += index => {
      if (this._entropyRegions is null || index >= this._entropyRegions.Count) return;
      this._entropyGrid.SelectedItem = this._entropyRegions[index];
      this._entropyGrid.EnsureVisible(index);
    };

    AddColumn(this._entropyGrid, "Offset (hex)", static o => ((RegionProfile)o!).Offset.ToString("X"), 80);
    AddColumn(this._entropyGrid, "Offset (dec)", static o => ((RegionProfile)o!).Offset, 80);
    AddColumn(this._entropyGrid, "Length", static o => ((RegionProfile)o!).Length.ToString("N0"), 70);
    AddColumn(this._entropyGrid, "Entropy", static o => ((RegionProfile)o!).Entropy.ToString("F4"), 80);
    AddColumn(this._entropyGrid, "Chi-sq", static o => ((RegionProfile)o!).ChiSquare.ToString("F1"), 80);
    AddColumn(this._entropyGrid, "Mean", static o => ((RegionProfile)o!).Mean.ToString("F1"), 60);
    AddColumn(this._entropyGrid, "Classification", static o => ((RegionProfile)o!).Classification, 200);
    this._entropyGrid.RowBackColorSelector = static o => o is RegionProfile r ? EntropyPalette.ToRowTint(r.Entropy) : null;
    this._entropyGrid.CellDoubleClick += (_, _) => this.PreviewEntropyRegion();

    this._toolTips.SetToolTip(this._entropyLegend,
      "What the bar's colours mean. Each swatch is the ramp sampled at the entropy its label describes.");

    this._entropyPanel.Controls.AddRange(this._cusum, this._entropyLegend, this._entropyBar, this._entropyGrid);
    this._entropyBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
    this._entropyGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
  }

  private void BuildTrialPanel() {
    AddColumn(this._trialGrid, "Algorithm", static o => ((DecompressionAttempt)o!).Algorithm, 120);
    AddColumn(this._trialGrid, "Output Size", static o => ((DecompressionAttempt)o!).OutputSize, 90);
    AddColumn(this._trialGrid, "Output Entropy", static o => ((DecompressionAttempt)o!).OutputEntropy.ToString("F4"), 100);
    AddColumn(this._trialGrid, "Success", static o => ((DecompressionAttempt)o!).Success, 60);
    AddColumn(this._trialGrid, "Error", static o => ((DecompressionAttempt)o!).Error, 300);
    this._trialGrid.RowBackColorSelector = static o => o is not DecompressionAttempt t ? null
      : t.Success
        ? Color.FromArgb((byte)Math.Min(50, 20 + t.OutputSize / 500), 70, 170, 70)
        : Color.FromArgb(15, 200, 50, 50);
    this._trialGrid.CellDoubleClick += (_, _) => this.PreviewTrialOutput(fromDoubleClick: true);

    this._previewTrial.Image = Images.Icon(IconKeys.Preview, 14);
    this._previewTrial.Click += (_, _) => this.PreviewTrialOutput(fromDoubleClick: false);
    this._saveTrial.Image = Images.Icon(IconKeys.Save, 14);
    this._saveTrial.Click += (_, _) => this.SaveTrialOutput();

    this._trialPanel.Controls.AddRange(this._trialGrid, this._previewTrial, this._saveTrial);
    this._trialGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
    this._previewTrial.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
    this._saveTrial.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
  }

  private static void AddColumn(DataGridView grid, string header, Func<object?, object?> selector, int width)
    => grid.Columns.Add(new DataGridViewColumn(header, selector) { Width = width });

  private static Color Tint(Color baseColor, byte alpha) => Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);

  private void LayoutChildren() {
    const int Margin = 8;
    var width = this.ClientSize.Width - 2 * Margin;

    this._fileBox.Bounds = new(Margin, Margin, width, 50);
    this._filePath.Bounds = new(8, 20, Math.Max(80, width - 110), 24);
    this._browse.Bounds = new(width - 96, 20, 88, 24);

    var y = Margin + 54;
    this._optionsBox.Bounds = new(Margin, y, width, 82);
    LayoutRow(22, 10, [this._all, this._deepScan, this._fingerprint, this._trial, this._entropyMap, this._chain]);
    this.LayoutOptionFields();

    y += 86;
    this._run.Bounds = new(Margin, y, 130, 28);
    this._cliOverride.Bounds = new(Margin + 138, y + 2, Math.Max(100, width - 138 - 300), 24);
    this._busy.Bounds = new(this.ClientSize.Width - Margin - 290, y + 7, 120, 14);
    this._status.Bounds = new(this.ClientSize.Width - Margin - 162, y + 5, 162, 20);

    y += 34;
    this._fileInfo.Bounds = new(Margin, y, width, 20);
    this._statsSummary.Bounds = new(Margin, y + 22, width, 18);

    y += 44;
    this._toolSelector.Bounds = new(Margin, y, width, 28);

    y += 32;
    var contentHeight = Math.Max(120, this.ClientSize.Height - y - Margin);
    foreach (var panel in this._toolPanels)
      panel.Bounds = new(Margin, y, width, contentHeight);

    this._cusum.Bounds = new(4, 4, 220, 20);

    // The legend takes what is left of the row, up to what it needs; below that it clips, which
    // still reads as a colour key.
    var legendLeft = this._cusum.Bounds.Right + 12;
    this._entropyLegend.Bounds = new(
      legendLeft, 4, Math.Max(0, Math.Min(this._entropyLegend.PreferredWidth, width - legendLeft - 4)), 20);
    this._entropyBar.Bounds = new(4, 28, Math.Max(50, width - 8), EntropyBarHeight);
    this._entropyGrid.Bounds = new(0, 28 + EntropyBarHeight + 4, width, Math.Max(40, contentHeight - EntropyBarHeight - 36));

    this._trialGrid.Bounds = new(0, 0, width, Math.Max(40, contentHeight - 34));
    this._previewTrial.Bounds = new(0, contentHeight - 30, 130, 26);
    this._saveTrial.Bounds = new(136, contentHeight - 30, 130, 26);

    static void LayoutRow(int top, int left, Control[] controls) {
      var x = left;
      foreach (var control in controls) {
        control.Bounds = new(x, top, 8 * control.Text.Length + 30, 20);
        x += control.Width + 12;
      }
    }
  }

  private void LayoutOptionFields() {
    var fields = new (string Caption, TextBox Box, int Width)[] {
      ("Max Depth:", this._maxDepth, 40),
      ("Window:", this._window, 50),
      ("Offset:", this._offset, 70),
      ("Length:", this._length, 70),
      ("Timeout (ms):", this._timeout, 50),
    };

    var x = 10;
    foreach (var (caption, box, boxWidth) in fields) {
      var label = this._optionsBox.Controls.OfType<Label>().First(l => l.Text == caption);
      label.Bounds = new(x, 52, 8 * caption.Length + 6, 20);
      x += label.Width + 4;
      box.Bounds = new(x, 50, boxWidth, 22);
      x += boxWidth + 12;
    }
  }

  private void SelectTool(string key) {
    foreach (var (name, button) in this._toolButtons)
      if (name != key) button.Checked = false;
    if (this._toolButtons.TryGetValue(key, out var selected)) selected.Checked = true;

    foreach (var panel in this._toolPanels) panel.Visible = false;

    var target = key switch {
      "Fingerprint" => (Control)this._fingerprints,
      "Entropy" => this._entropyPanel,
      "Trial" => this._trialPanel,
      "Chain" => this._chainGrid,
      "Stats" => this._stats,
      "Strings" => this._strings,
      "Structure" => this._structure,
      "Heatmap" => this._heatmap,
      _ => this._signatures,
    };
    target.Visible = true;

    if (this._fileData is not { } data) return;
    switch (key) {
      case "Stats": this._stats.Data = data; break;
      case "Strings": this._strings.Data = data; break;
      case "Structure": this._structure.Data = data; break;
      case "Heatmap":
        // Prefer the real file: the heatmap reads it with random access, which matters for large
        // inputs. When the path is a synthetic archive-entry name there is nothing on disk to open,
        // so stream the bytes we already hold rather than probing the working directory.
        if (this._currentPath is not null && File.Exists(this._currentPath)) this._heatmap.OpenFile(this._currentPath);
        else this._heatmap.OpenStream(new MemoryStream(data), this._currentPath ?? "(in-memory)");
        break;
    }
  }

  /// <summary>Loads and analyses <paramref name="data"/> without waiting for the result.</summary>
  public void RunAnalysis(string fileName, byte[] data) => _ = this.RunAnalysisAsync(fileName, data);

  /// <summary>
  /// Loads the data and analyses it, completing only once the results are on screen. Interactive
  /// callers do not need that signal; documentation capture does, because it renders the window
  /// immediately afterwards.
  /// </summary>
  public Task RunAnalysisAsync(string fileName, byte[] data) {
    this._currentPath = fileName;
    this._fileData = data;
    this._filePath.Text = fileName;
    this._filePath.ForeColor = Color.Black;
    this._run.Enabled = true;
    return this.ExecuteAnalysisAsync();
  }

  private void LoadFile(string path) {
    try {
      this._fileData = File.ReadAllBytes(path);
      this._currentPath = path;
      this._filePath.Text = path;
      this._filePath.ForeColor = Color.Black;
      this._run.Enabled = true;
      _ = this.ExecuteAnalysisAsync();
    } catch (Exception ex) {
      MessageBox.Show(this, $"Failed to read file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
  }

  private AnalysisOptions BuildOptions() {
    var cli = this._cliOverride.Text;
    if (!string.IsNullOrWhiteSpace(cli) && cli != CliPlaceholder) return ParseCliFlags(cli);

    int.TryParse(this._maxDepth.Text, out var maxDepth);
    int.TryParse(this._window.Text, out var window);
    long.TryParse(this._offset.Text, out var offset);
    long.TryParse(this._length.Text, out var length);
    int.TryParse(this._timeout.Text, out var timeout);

    if (maxDepth <= 0) maxDepth = 10;
    if (window <= 0) window = 256;
    if (timeout <= 0) timeout = 200;

    return new() {
      All = this._all.Checked,
      DeepScan = this._deepScan.Checked,
      Fingerprint = this._fingerprint.Checked,
      Trial = this._trial.Checked,
      EntropyMap = this._entropyMap.Checked,
      Chain = this._chain.Checked,
      BoundaryDetection = this._cusum.Checked,
      MaxDepth = maxDepth,
      WindowSize = window,
      Offset = offset,
      Length = length,
      PerTrialTimeoutMs = timeout,
    };
  }

  private static AnalysisOptions ParseCliFlags(string flags) {
    var args = flags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    bool deepScan = false, fingerprint = false, trial = false, entropyMap = false, chain = false, all = false;
    int maxDepth = 10, window = 256, timeout = 200;
    long offset = 0, length = 0;

    for (var i = 0; i < args.Length; ++i)
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

    // Flags that name no pass at all mean "everything".
    if (!all && !deepScan && !fingerprint && !trial && !entropyMap && !chain) all = true;

    return new() {
      All = all, DeepScan = deepScan, Fingerprint = fingerprint,
      Trial = trial, EntropyMap = entropyMap, Chain = chain,
      MaxDepth = maxDepth, WindowSize = window,
      Offset = offset, Length = length, PerTrialTimeoutMs = timeout,
    };
  }

  private async Task ExecuteAnalysisAsync() {
    if (this._fileData is not { } data || this._currentPath is not { } fileName) return;

    var options = this.BuildOptions();
    this._run.Enabled = false;
    this._busy.Visible = true;
    this._status.Text = $"Analyzing {data.Length:N0} bytes...";
    this.Text = $"Analysis — {Path.GetFileName(fileName)}";

    var sw = Stopwatch.StartNew();
    AnalysisResult result;
    try {
      result = await Task.Run(() => new BinaryAnalyzer(options).Analyze(data));
    } catch (Exception ex) {
      this._status.Text = $"Error: {ex.Message}";
      this._busy.Visible = false;
      this._run.Enabled = true;
      return;
    }
    sw.Stop();

    this._lastResult = result;
    this._entropyRegions = result.EntropyMap;
    this._status.Text = this.OmitElapsedTime ? "Done" : $"Done ({sw.ElapsedMilliseconds:N0}ms)";
    this._busy.Visible = false;
    this._run.Enabled = true;

    this.PopulateResults(fileName, data, result);
  }

  private void PopulateResults(string fileName, byte[] data, AnalysisResult result) {
    this._fileInfo.Text = $"{Path.GetFileName(fileName)}  ({data.Length:N0} bytes)";
    if (result.Statistics is { } s)
      this._statsSummary.Text = $"Entropy: {s.Entropy:F4}  Mean: {s.Mean:F1}  Chi²: {s.ChiSquare:F1}  Unique: {s.UniqueBytesCount}/256";

    this._signatures.DataSource = result.Signatures;
    this._fingerprints.DataSource = result.Fingerprints;
    this._entropyGrid.DataSource = result.EntropyMap;
    this._trialGrid.DataSource = result.TrialResults;
    this._entropyBar.SetRegions(result.EntropyMap, data.Length);

    this._chainGrid.DataSource = result.Chain is not { } chain ? Array.Empty<object>() : BuildChainRows(chain);
  }

  private static List<ChainRow> BuildChainRows(CompressionChain chain) {
    var rows = new List<ChainRow>();
    for (var i = 0; i < chain.Layers.Count; ++i) {
      var l = chain.Layers[i];
      rows.Add(new() {
        Layer = i + 1,
        Algorithm = l.Algorithm,
        InputSize = $"{l.InputSize:N0}",
        OutputSize = $"{l.OutputSize:N0}",
        Confidence = l.Confidence,
        Status = l.OutputData is not null ? "Has data" : "No data",
      });
    }

    if (chain.FinalData.Length > 0)
      rows.Add(new() {
        Layer = chain.Layers.Count + 1,
        Algorithm = "(final output)",
        InputSize = "",
        OutputSize = $"{chain.FinalData.Length:N0}",
        Confidence = 1.0,
        Status = "Final",
      });

    return rows;
  }

  private void PreviewEntropyRegion() {
    if (this._entropyGrid.SelectedItem is not RegionProfile region || this._fileData is not { } data) return;

    var start = (int)Math.Min(region.Offset, data.Length);
    var length = Math.Min(region.Length, data.Length - start);
    if (length <= 0) return;

    var slice = new byte[length];
    Array.Copy(data, start, slice, 0, length);

    var preview = new PreviewWindow();
    preview.ShowData(
      $"Region 0x{region.Offset:X}–0x{region.Offset + region.Length:X} ({region.Classification})",
      slice, hex: true, analyzeMode: true);
    preview.Show();
  }

  private void PreviewTrialOutput(bool fromDoubleClick) {
    if (this._trialGrid.SelectedItem is not DecompressionAttempt { Success: true, Output: not null } trial) {
      if (!fromDoubleClick)
        MessageBox.Show(this, "Select a successful trial result first.", "Preview", MessageBoxButtons.OK, MessageBoxIcon.Information);
      return;
    }

    var preview = new PreviewWindow();
    if (fromDoubleClick
        && (trial.Algorithm.StartsWith("Detected:", StringComparison.Ordinal)
            || trial.Algorithm.Contains("(archive)", StringComparison.Ordinal))) {
      // Magic matches and archive listings are UTF-8 descriptions, not payloads.
      preview.ShowData(trial.Algorithm, trial.Output, hex: false, analyzeMode: false);
    } else {
      preview.ShowData($"{trial.Algorithm} output ({trial.OutputSize} bytes)", trial.Output,
        hex: fromDoubleClick && trial.OutputSize < 4096, analyzeMode: true);
    }

    preview.Show();
  }

  private void SaveTrialOutput() {
    if (this._trialGrid.SelectedItem is not DecompressionAttempt { Success: true, Output: not null } trial) {
      MessageBox.Show(this, "Select a successful trial result first.", "Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
      return;
    }

    var dialog = new SaveFileDialog {
      Title = "Save decompressed output",
      FileName = $"{trial.Algorithm}_output.bin",
      Filter = "All Files|*.*",
    };
    if (dialog.ShowDialog() != DialogResult.OK) return;

    File.WriteAllBytes(dialog.FileName, trial.Output);
    this._status.Text = $"Saved {trial.OutputSize} bytes to {dialog.FileName}";
  }

  private void PreviewChainLayer() {
    if (this._lastResult?.Chain is not { } chain) return;
    if (this._chainGrid.SelectedItem is not ChainRow row) return;

    var index = row.Layer - 1;
    var (data, title) = index < chain.Layers.Count
      ? (chain.Layers[index].OutputData, $"Layer {row.Layer}: {chain.Layers[index].Algorithm} ({chain.Layers[index].OutputSize:N0} bytes)")
      : (chain.FinalData, $"Final output ({chain.FinalData.Length:N0} bytes)");

    if (data is not { Length: > 0 }) {
      MessageBox.Show(this, "No data available for this layer.", "Chain", MessageBoxButtons.OK, MessageBoxIcon.Information);
      return;
    }

    var preview = new PreviewWindow();
    preview.ShowData(title, data, hex: data.Length < 4096, analyzeMode: true);
    preview.Show();
  }

  /// <summary>
  /// Gives each format name a stable hue. The hash is computed by hand rather than taken from
  /// <see cref="string.GetHashCode()"/> because that is randomised per process, and the golden-ratio
  /// step spreads consecutive hues as far apart as possible.
  /// </summary>
  private static Color FormatNameToColor(string name) {
    uint hash = 0;
    foreach (var c in name) hash = hash * 31 + c;

    var h = hash * 0.618033988749895 % 1.0 * 6.0;
    const double Chroma = 0.5;
    var x = Chroma * (1 - Math.Abs(h % 2 - 1));

    var (r1, g1, b1) = h switch {
      < 1 => (Chroma, x, 0.0),
      < 2 => (x, Chroma, 0.0),
      < 3 => (0.0, Chroma, x),
      < 4 => (0.0, x, Chroma),
      < 5 => (x, 0.0, Chroma),
      _ => (Chroma, 0.0, x),
    };

    const double M = 0.55 - Chroma / 2;
    return Color.FromArgb((byte)((r1 + M) * 255), (byte)((g1 + M) * 255), (byte)((b1 + M) * 255));
  }
}
