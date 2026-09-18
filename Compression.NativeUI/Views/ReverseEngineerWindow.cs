using System.Drawing;
using Compression.NativeUI.ViewModels;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using Step = Compression.NativeUI.ViewModels.ReverseEngineerWizardViewModel.WizardStep;

namespace Compression.NativeUI.Views;

/// <summary>
/// Walks the user through discovering an unknown format: pick a mode, configure the tool or add
/// sample pairs, watch the analysis run, then read the report.
/// </summary>
internal sealed class ReverseEngineerWindow : Form {
  private static readonly Font MonoFont = new("Cascadia Mono", 9f, FontStyle.Regular);

  private readonly ReverseEngineerWizardViewModel _model = new();

  // Step 1.
  private readonly Panel _chooseMode = new();
  private readonly RadioButton _toolMode = new() { Text = "I have the tool — probe it with test inputs", Checked = true };
  private readonly RadioButton _staticMode = new() { Text = "I have archive files with known original content" };

  // Step 2a.
  private readonly Panel _configureTool = new() { Visible = false };
  private readonly TextBox _toolPath = new();
  private readonly TextBox _toolArguments = new();
  private readonly TextBox _toolTimeout = new();

  // Step 2b.
  private readonly Panel _addSamples = new() { Visible = false };
  private readonly Button _addSample = new() { Text = "+ Add Sample" };
  private readonly Panel _sampleList = new() { AutoScroll = true };

  // Step 3.
  private readonly Panel _running = new() { Visible = false };
  private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
  private readonly Label _statusText = new() { TextAlign = ContentAlignment.MiddleCenter };
  private readonly Label _currentProbe = new() { ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleCenter };

  // Step 4.
  private readonly Panel _results = new() { Visible = false };
  private readonly TabControl _resultTabs = new();
  private readonly TextBox _reportText = new() { ReadOnly = true, Multiline = true, Font = MonoFont };
  private readonly TextBox _headerHex = new() { ReadOnly = true, Font = MonoFont };
  private readonly TextBox _compressionResult = new() { ReadOnly = true, Font = MonoFont };
  private readonly DataGridView _sizeFields = new() { ReadOnly = true, ShowGridLines = true };
  private readonly DataGridView _probeResults = new() { ReadOnly = true, ShowGridLines = true };

  // Navigation.
  private readonly Button _back = new() { Text = "Back" };
  private readonly Button _cancel = new() { Text = "Cancel" };
  private readonly Button _next = new() { Text = "Next" };

  private readonly List<SampleRow> _sampleRows = [];

  public ReverseEngineerWindow() {
    this.Text = "Format Reverse Engineer";
    this.ClientSize = new(800, 600);
    this.MinimumSize = new(640, 480);
    this.StartPosition = FormStartPosition.CenterParent;

    // The analyzers report progress from worker threads; the form owns the UI thread.
    this._model.UiMarshaller = action => this.BeginInvoke(action);
    this._model.RequestClose += (_, _) => this.Close();
    this._model.PropertyChanged += (_, _) => this.SyncFromModel();

    this.BuildChooseMode();
    this.BuildConfigureTool();
    this.BuildAddSamples();
    this.BuildRunning();
    this.BuildResults();
    this.BuildNavigation();

    this.Controls.AddRange(this._chooseMode, this._configureTool, this._addSamples, this._running, this._results,
      this._back, this._cancel, this._next);

    this.Resize += (_, _) => this.LayoutChildren();
    this.LayoutChildren();
    this.SyncFromModel();
  }

  private void BuildChooseMode() {
    var heading = Heading("Format Reverse Engineering Wizard");
    var intro = new Label {
      Text = "This wizard helps you discover the internal format of an unknown archive or compressed file. "
           + "Choose how you want to provide data:",
    };

    this._toolMode.CheckedChanged += (_, _) => this._model.IsToolMode = this._toolMode.Checked;
    this._staticMode.CheckedChanged += (_, _) => this._model.IsToolMode = !this._staticMode.Checked;

    this._chooseMode.Controls.AddRange(
      heading, intro,
      this._toolMode,
      new Label {
        Text = "Runs the tool ~40 times with controlled inputs to discover headers, size fields, and compression.",
        ForeColor = Color.Gray,
      },
      this._staticMode,
      new Label {
        Text = "Analyzes archives by locating known content inside them — finds where data is stored and how it's compressed.",
        ForeColor = Color.Gray,
      });
  }

  private void BuildConfigureTool() {
    this._toolPath.TextChanged += (_, _) => {
      this._model.ToolPath = this._toolPath.Text;
      this.UpdateNavigation();
    };
    this._toolArguments.Text = this._model.ToolArguments;
    this._toolArguments.TextChanged += (_, _) => {
      this._model.ToolArguments = this._toolArguments.Text;
      this.UpdateNavigation();
    };
    this._toolTimeout.Text = this._model.ToolTimeout.ToString();
    this._toolTimeout.TextChanged += (_, _) => {
      if (int.TryParse(this._toolTimeout.Text, out var ms)) this._model.ToolTimeout = ms;
    };

    this._configureTool.Controls.AddRange(
      Heading("Configure External Tool"),
      new Label {
        Text = "Enter the tool's executable path and the argument template. "
             + "Use {input} for the input file and {output} for the output file.",
      },
      new Label { Text = "Executable:" }, this._toolPath,
      new Label { Text = "Arguments:" }, this._toolArguments,
      new Label { Text = "Example: {input} {output}  or  --compress {input} -o {output}", ForeColor = Color.Gray },
      new Label { Text = "Timeout (ms):" }, this._toolTimeout);
  }

  private void BuildAddSamples() {
    this._addSample.Click += (_, _) => {
      this._model.AddSampleCommand.Execute(null);
      this.RebuildSampleRows();
    };

    this._addSamples.Controls.AddRange(
      Heading("Add Sample Pairs"),
      new Label {
        Text = "Add pairs of original files and their corresponding archives. "
             + "The analyzer will search for the original content inside each archive.",
      },
      this._addSample, this._sampleList);
  }

  private void BuildRunning() {
    this._running.Controls.AddRange(Heading("Analyzing..."), this._progress, this._statusText, this._currentProbe);
  }

  private void BuildResults() {
    AddColumn(this._sizeFields, "Offset", static o => ((ReverseEngineerWizardViewModel.SizeFieldEntry)o!).Offset, 80);
    AddColumn(this._sizeFields, "Width", static o => ((ReverseEngineerWizardViewModel.SizeFieldEntry)o!).Width, 80);
    AddColumn(this._sizeFields, "Endianness", static o => ((ReverseEngineerWizardViewModel.SizeFieldEntry)o!).Endianness, 100);
    AddColumn(this._sizeFields, "Meaning", static o => ((ReverseEngineerWizardViewModel.SizeFieldEntry)o!).Meaning, 300);

    AddColumn(this._probeResults, "Probe", static o => ((ReverseEngineerWizardViewModel.ProbeResultEntry)o!).Name, 200);
    AddColumn(this._probeResults, "Input Size", static o => ((ReverseEngineerWizardViewModel.ProbeResultEntry)o!).InputSize, 100);
    AddColumn(this._probeResults, "Output Size", static o => ((ReverseEngineerWizardViewModel.ProbeResultEntry)o!).OutputSize, 100);
    AddColumn(this._probeResults, "Status", static o => ((ReverseEngineerWizardViewModel.ProbeResultEntry)o!).Status, 240);

    var summaryTab = new TabPage("Summary");
    this._reportText.Dock = DockStyle.Fill;
    summaryTab.Controls.Add(this._reportText);

    var headerTab = new TabPage("Header");
    headerTab.Controls.AddRange(
      new Label { Text = "Detected Magic Bytes:", Bounds = new(8, 8, 200, 20), Font = new(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold) },
      this._headerHex,
      new Label { Text = "Detected Compression:", Bounds = new(8, 74, 200, 20), Font = new(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold) },
      this._compressionResult);
    this._headerHex.Bounds = new(8, 32, 700, 26);
    this._compressionResult.Bounds = new(8, 98, 700, 26);
    this._headerHex.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
    this._compressionResult.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

    var sizeTab = new TabPage("Size Fields");
    this._sizeFields.Dock = DockStyle.Fill;
    sizeTab.Controls.Add(this._sizeFields);

    var probeTab = new TabPage("Probe Results");
    this._probeResults.Dock = DockStyle.Fill;
    probeTab.Controls.Add(this._probeResults);

    this._resultTabs.TabPages.Add(summaryTab);
    this._resultTabs.TabPages.Add(headerTab);
    this._resultTabs.TabPages.Add(sizeTab);
    this._resultTabs.TabPages.Add(probeTab);

    this._results.Controls.AddRange(Heading("Analysis Results"), this._resultTabs);
  }

  private static void AddColumn(DataGridView grid, string header, Func<object?, object?> selector, int width)
    => grid.Columns.Add(new DataGridViewColumn(header, selector) { Width = width });

  private void BuildNavigation() {
    this._back.Click += (_, _) => {
      this._model.BackCommand.Execute(null);
      this.RebuildSampleRows();
    };
    this._cancel.Click += (_, _) => this._model.CancelCommand.Execute(null);
    this._next.Click += (_, _) => {
      this._model.NextCommand.Execute(null);
      this.RebuildSampleRows();
    };
    this.AcceptButton = this._next;
  }

  private static Label Heading(string text) => new() {
    Text = text,
    Font = new(DefaultTheme.Instance.DefaultFont.Family, 13f, FontStyle.Bold),
  };

  private void LayoutChildren() {
    const int Gutter = 16;
    const int FooterHeight = 56;
    var width = this.ClientSize.Width - 2 * Gutter;
    var height = Math.Max(100, this.ClientSize.Height - FooterHeight - Gutter);

    foreach (var panel in new[] { this._chooseMode, this._configureTool, this._addSamples, this._running, this._results })
      panel.Bounds = new(Gutter, Gutter, width, height);

    LayoutStack(this._chooseMode, width, [30, 40, 26, 34, 26, 34]);
    LayoutStack(this._configureTool, width, [30, 40, 20, 26, 20, 26, 20, 20, 26]);

    var samples = this._addSamples.Controls;
    samples[0].Bounds = new(0, 0, width, 30);
    samples[1].Bounds = new(0, 34, width, 34);
    this._addSample.Bounds = new(0, 74, 120, 26);
    this._sampleList.Bounds = new(0, 108, width, Math.Max(60, height - 108));

    this._running.Controls[0].Bounds = new(0, height / 2 - 70, width, 30);
    this._progress.Bounds = new(40, height / 2 - 30, Math.Max(60, width - 80), 24);
    this._statusText.Bounds = new(0, height / 2 + 2, width, 20);
    this._currentProbe.Bounds = new(0, height / 2 + 24, width, 20);

    this._results.Controls[0].Bounds = new(0, 0, width, 30);
    this._resultTabs.Bounds = new(0, 36, width, Math.Max(60, height - 36));

    var footerY = this.ClientSize.Height - FooterHeight + 14;
    this._next.Bounds = new(this.ClientSize.Width - Gutter - 80, footerY, 80, 28);
    this._cancel.Bounds = new(this.ClientSize.Width - Gutter - 168, footerY, 80, 28);
    this._back.Bounds = new(this.ClientSize.Width - Gutter - 256, footerY, 80, 28);

    this.LayoutSampleRows();

    static void LayoutStack(Panel panel, int width, int[] heights) {
      var y = 0;
      for (var i = 0; i < panel.Controls.Count && i < heights.Length; ++i) {
        panel.Controls[i].Bounds = new(0, y, width, heights[i]);
        y += heights[i] + 6;
      }
    }
  }

  /// <summary>Rebuilds one editor row per sample pair.</summary>
  private void RebuildSampleRows() {
    this._sampleList.Controls.Clear();
    this._sampleRows.Clear();

    foreach (var sample in this._model.Samples) {
      var row = new SampleRow(sample, this._model, () => {
        this._model.RemoveSampleCommand.Execute(sample);
        this.RebuildSampleRows();
      }, () => this.UpdateNavigation());

      this._sampleRows.Add(row);
      this._sampleList.Controls.Add(row);
    }

    this.LayoutSampleRows();
    this.UpdateNavigation();
  }

  private void LayoutSampleRows() {
    var width = Math.Max(200, this._sampleList.Width - 20);
    var y = 0;

    foreach (var row in this._sampleRows) {
      row.Bounds = new(0, y, width, SampleRow.RowHeight);
      row.LayoutChildren();
      y += SampleRow.RowHeight + 6;
    }
  }

  private void SyncFromModel() {
    var step = this._model.CurrentStep;
    this._chooseMode.Visible = step == Step.ChooseMode;
    this._configureTool.Visible = step == Step.ConfigureTool;
    this._addSamples.Visible = step == Step.AddSamples;
    this._running.Visible = step == Step.Running;
    this._results.Visible = step == Step.Results;

    this._progress.Value = (int)Math.Clamp(this._model.Progress, 0, 100);
    this._statusText.Text = this._model.StatusText;
    this._currentProbe.Text = this._model.CurrentProbe;

    this._reportText.Text = this._model.ReportText;
    this._headerHex.Text = this._model.HeaderHex;
    this._compressionResult.Text = this._model.CompressionResult;
    this._sizeFields.DataSource = this._model.DetectedSizeFields;
    this._probeResults.DataSource = this._model.ProbeResults;

    this.UpdateNavigation();
  }

  private void UpdateNavigation() {
    this._back.Enabled = this._model.BackCommand.CanExecute(null);
    this._next.Enabled = this._model.NextCommand.CanExecute(null);
  }

  /// <summary>One original-and-archive pair, with a browse button for each side.</summary>
  private sealed class SampleRow : Panel {
    public const int RowHeight = 66;

    private readonly TextBox _original = new();
    private readonly TextBox _archive = new();
    private readonly Button _browseOriginal = new() { Text = "Browse..." };
    private readonly Button _browseArchive = new() { Text = "Browse..." };
    private readonly Button _remove = new() { Text = "X" };
    private readonly Label _originalCaption = new() { Text = "Original:" };
    private readonly Label _archiveCaption = new() { Text = "Archive:" };

    public SampleRow(
      ReverseEngineerWizardViewModel.SampleEntry sample,
      ReverseEngineerWizardViewModel model,
      Action onRemove,
      Action onChanged) {
      this.BorderStyle = BorderStyle.FixedSingle;

      this._original.Text = sample.OriginalPath;
      this._original.TextChanged += (_, _) => {
        sample.OriginalPath = this._original.Text;
        onChanged();
      };

      this._archive.Text = sample.ArchivePath;
      this._archive.TextChanged += (_, _) => {
        sample.ArchivePath = this._archive.Text;
        onChanged();
      };

      this._browseOriginal.Click += (_, _) => {
        model.BrowseOriginalCommand.Execute(sample);
        this._original.Text = sample.OriginalPath;
      };
      this._browseArchive.Click += (_, _) => {
        model.BrowseArchiveCommand.Execute(sample);
        this._archive.Text = sample.ArchivePath;
      };
      this._remove.Click += (_, _) => onRemove();

      this.Controls.AddRange(this._originalCaption, this._original, this._browseOriginal,
        this._archiveCaption, this._archive, this._browseArchive, this._remove);
    }

    public void LayoutChildren() {
      var pathWidth = Math.Max(80, this.Width - 80 - 90 - 40);

      this._originalCaption.Bounds = new(6, 8, 62, 20);
      this._original.Bounds = new(72, 6, pathWidth, 24);
      this._browseOriginal.Bounds = new(76 + pathWidth, 6, 84, 24);

      this._archiveCaption.Bounds = new(6, 38, 62, 20);
      this._archive.Bounds = new(72, 36, pathWidth, 24);
      this._browseArchive.Bounds = new(76 + pathWidth, 36, 84, 24);

      this._remove.Bounds = new(this.Width - 32, 20, 26, 26);
    }
  }
}
