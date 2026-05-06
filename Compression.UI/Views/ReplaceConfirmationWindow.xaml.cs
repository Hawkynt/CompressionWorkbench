using System.Windows;

namespace Compression.UI.Views;

/// <summary>
/// User decision returned by <see cref="ReplaceConfirmationWindow"/> when an
/// archive already contains an entry matching a dropped file.
/// </summary>
public enum ReplaceDecision {
  /// <summary>Replace this one entry; ask again on the next collision.</summary>
  Yes,
  /// <summary>Replace this and every subsequent collision in the batch.</summary>
  YesToAll,
  /// <summary>Skip this collision (don't replace); ask again on the next.</summary>
  Skip,
  /// <summary>Skip every collision in the batch.</summary>
  SkipAll,
  /// <summary>Cancel the whole drop / add operation.</summary>
  Cancel,
}

public partial class ReplaceConfirmationWindow : Window {

  /// <summary>Result of the dialog — read after <see cref="Window.ShowDialog"/> returns.</summary>
  public ReplaceDecision Decision { get; private set; } = ReplaceDecision.Cancel;

  public ReplaceConfirmationWindow(string archiveName, string entryName) {
    InitializeComponent();
    MessageText.Text = $"\"{entryName}\" already exists in {archiveName}.\nReplace it with the dropped file?";
  }

  private void OnYes(object sender, RoutedEventArgs e) {
    this.Decision = ReplaceDecision.Yes;
    DialogResult = true;
    Close();
  }

  private void OnYesAll(object sender, RoutedEventArgs e) {
    this.Decision = ReplaceDecision.YesToAll;
    DialogResult = true;
    Close();
  }

  private void OnSkip(object sender, RoutedEventArgs e) {
    this.Decision = ReplaceDecision.Skip;
    DialogResult = true;
    Close();
  }

  private void OnSkipAll(object sender, RoutedEventArgs e) {
    this.Decision = ReplaceDecision.SkipAll;
    DialogResult = true;
    Close();
  }

  private void OnCancel(object sender, RoutedEventArgs e) {
    this.Decision = ReplaceDecision.Cancel;
    DialogResult = false;
    Close();
  }
}
