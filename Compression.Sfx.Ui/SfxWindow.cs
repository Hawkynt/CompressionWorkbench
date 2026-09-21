using System.Drawing;
using Compression.Lib;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.Sfx.Ui;

/// <summary>
/// The window a GUI self-extracting archive shows: what it contains, where it will go, and a
/// button to put it there.
/// <para>
/// This runs as the stub prepended to the archive, so it is deliberately plain — no format
/// registry warm-up, no browsing, nothing but the one job. Every byte here is a byte added to
/// every SFX archive anyone builds.
/// </para>
/// </summary>
internal sealed class SfxWindow : Form {
  private const int Gutter = 20;
  private const int WindowWidth = 500;
  private const int InnerWidth = WindowWidth - 2 * Gutter;

  private readonly string _exePath = Environment.ProcessPath ?? "";

  private readonly Label _formatLabel = new();
  private readonly TextBox _outputPath = new();
  private readonly Button _browse = new() { Text = "Browse..." };
  private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
  private readonly Label _status = new() {
    Text = "Choose a destination folder and click Extract.",
    ForeColor = Color.Gray,
  };
  private readonly Button _extract = new() { Text = "Extract" };
  private readonly Button _close = new() { Text = "Close" };

  public SfxWindow() {
    this.Text = "Self-Extracting Archive";
    this.ClientSize = new(WindowWidth, 250);
    this.FormBorderStyle = FormBorderStyle.FixedDialog;
    this.StartPosition = FormStartPosition.CenterScreen;
    this.MaximizeBox = false;

    this._formatLabel.Font = new(this._formatLabel.Font.Family, 12f, FontStyle.Bold);
    this._formatLabel.Text = "Self-Extracting Archive";
    this._formatLabel.Bounds = new(Gutter, Gutter, InnerWidth, 26);

    this._outputPath.Text = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Extracted");
    this._outputPath.Bounds = new(Gutter, 61, InnerWidth - 100, 26);

    this._browse.Bounds = new(WindowWidth - Gutter - 90, 60, 90, 28);
    this._browse.Click += this.OnBrowse;

    this._progress.Bounds = new(Gutter, 108, InnerWidth, 24);

    this._status.Bounds = new(Gutter, 146, InnerWidth, 36);

    this._extract.Bounds = new(WindowWidth - Gutter - 200, 196, 95, 30);
    this._extract.Click += this.OnExtract;

    this._close.Bounds = new(WindowWidth - Gutter - 95, 196, 95, 30);
    this._close.Click += (_, _) => this.Close();

    this.AcceptButton = this._extract;
    this.CancelButton = this._close;

    this.Controls.AddRange(
      this._formatLabel, this._outputPath, this._browse,
      this._progress, this._status, this._extract, this._close);

    this.DetectArchive();
  }

  /// <summary>Reads the trailer the builder appended, to name what is inside.</summary>
  private void DetectArchive() {
    try {
      if (SfxBuilder.ReadTrailer(this._exePath) is not { } info) {
        this.ShowError("No embedded archive found.");
        return;
      }

      if (info.Format == FormatDetector.Format.Unknown) {
        this.ShowError("Cannot identify the embedded archive format.");
        return;
      }

      this._formatLabel.Text = $"Self-Extracting Archive ({info.Format})";
    } catch (Exception ex) {
      this.ShowError($"Error: {ex.Message}");
    }
  }

  private void ShowError(string message) {
    this._status.Text = message;
    this._status.ForeColor = Color.Firebrick;
    this._extract.Enabled = false;
  }

  private void OnBrowse(object? sender, EventArgs e) {
    var dialog = new FolderBrowserDialog { Title = "Select extraction folder" };
    if (!string.IsNullOrEmpty(this._outputPath.Text) && Directory.Exists(this._outputPath.Text))
      dialog.SelectedPath = this._outputPath.Text;

    if (dialog.ShowDialog() == DialogResult.OK) this._outputPath.Text = dialog.SelectedPath;
  }

  private async void OnExtract(object? sender, EventArgs e) {
    var outputDirectory = this._outputPath.Text.Trim();
    if (string.IsNullOrEmpty(outputDirectory)) {
      this._status.Text = "Please choose a destination folder.";
      this._status.ForeColor = Color.Gray;
      return;
    }

    this._extract.Enabled = false;
    this._browse.Enabled = false;
    this._progress.Style = ProgressBarStyle.Marquee;
    this._status.Text = "Extracting...";
    this._status.ForeColor = Color.Black;

    try {
      await Task.Run(() => SfxBuilder.Extract(this._exePath, outputDirectory));

      this._progress.Style = ProgressBarStyle.Blocks;
      this._progress.Value = 100;
      this._status.Text = $"Extraction complete. Files saved to: {outputDirectory}";
      this._status.ForeColor = Color.ForestGreen;
    } catch (Exception ex) {
      this._progress.Style = ProgressBarStyle.Blocks;
      this._progress.Value = 0;
      this._status.Text = $"Extraction failed: {ex.Message}";
      this._status.ForeColor = Color.Firebrick;
    } finally {
      this._extract.Enabled = true;
      this._browse.Enabled = true;
    }
  }
}
