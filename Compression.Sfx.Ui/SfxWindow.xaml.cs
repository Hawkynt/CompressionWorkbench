using System.IO;
using System.Windows;
using Compression.Lib;

namespace Compression.Sfx.Ui;

public partial class SfxWindow : Window {
  private readonly string _exePath;
  private FormatDetector.Format _format;

  public SfxWindow() {
    InitializeComponent();
    _exePath = Environment.ProcessPath ?? "";
    OutputPathBox.Text = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
      "Extracted"
    );
    DetectArchive();
  }

  private void DetectArchive() {
    try {
      var info = SfxBuilder.ReadTrailer(_exePath);
      if (info == null) {
        ShowError("No embedded archive found.");
        return;
      }

      _format = info.Value.Format;
      if (_format == FormatDetector.Format.Unknown) {
        ShowError("Cannot identify the embedded archive format.");
        return;
      }

      FormatLabel.Text = $"Self-Extracting Archive ({_format})";
    }
    catch (Exception ex) {
      ShowError($"Error: {ex.Message}");
    }
  }

  private void ShowError(string message) {
    StatusLabel.Text = message;
    StatusLabel.Foreground = System.Windows.Media.Brushes.Red;
    ExtractButton.IsEnabled = false;
  }

  private void OnBrowse(object sender, RoutedEventArgs e) {
    var dialog = new System.Windows.Forms.FolderBrowserDialog {
      Description = "Select extraction folder",
      ShowNewFolderButton = true,
    };
    if (!string.IsNullOrEmpty(OutputPathBox.Text) && Directory.Exists(OutputPathBox.Text))
      dialog.SelectedPath = OutputPathBox.Text;

    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
      OutputPathBox.Text = dialog.SelectedPath;
  }

  private async void OnExtract(object sender, RoutedEventArgs e) {
    var outputDir = OutputPathBox.Text.Trim();
    if (string.IsNullOrEmpty(outputDir)) {
      StatusLabel.Text = "Please choose a destination folder.";
      return;
    }

    ExtractButton.IsEnabled = false;
    ProgressBar.IsIndeterminate = true;
    StatusLabel.Text = "Extracting...";
    StatusLabel.Foreground = System.Windows.Media.Brushes.Black;

    try {
      await Task.Run(() => SfxBuilder.Extract(_exePath, outputDir));

      ProgressBar.IsIndeterminate = false;
      ProgressBar.Value = 100;
      StatusLabel.Text = $"Extraction complete. Files saved to: {outputDir}";
      StatusLabel.Foreground = System.Windows.Media.Brushes.Green;
    }
    catch (Exception ex) {
      ProgressBar.IsIndeterminate = false;
      StatusLabel.Text = $"Extraction failed: {ex.Message}";
      StatusLabel.Foreground = System.Windows.Media.Brushes.Red;
    }
    finally {
      ExtractButton.IsEnabled = true;
    }
  }

  private void OnClose(object sender, RoutedEventArgs e) => Close();
}
