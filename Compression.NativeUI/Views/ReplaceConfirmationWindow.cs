using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Views;

/// <summary>
/// User decision returned by <see cref="ReplaceConfirmationWindow"/> when an archive already
/// contains an entry matching a dropped file.
/// </summary>
internal enum ReplaceDecision {
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

/// <summary>Asks what to do about a name collision while adding files to an archive.</summary>
internal sealed class ReplaceConfirmationWindow : Form {
  private const int ButtonWidth = 92;
  private const int ButtonHeight = 26;

  /// <summary>Result of the dialog — read after <see cref="Form.ShowDialog"/> returns.</summary>
  public ReplaceDecision Decision { get; private set; } = ReplaceDecision.Cancel;

  public ReplaceConfirmationWindow(string archiveName, string entryName) {
    this.Text = "Replace existing entry?";
    this.ClientSize = new(480, 220);
    this.StartPosition = FormStartPosition.CenterParent;
    this.FormBorderStyle = FormBorderStyle.FixedDialog;
    this.MaximizeBox = false;
    this.MinimizeBox = false;

    var heading = new Label {
      Bounds = new(16, 16, 448, 24),
      Text = "An entry with this name already exists",
      Font = new(DefaultTheme.Instance.DefaultFont.Family, 11f, FontStyle.Bold),
    };

    var message = new Label {
      Bounds = new(16, 48, 448, 80),
      Text = $"\"{entryName}\" already exists in {archiveName}.\nReplace it with the dropped file?",
    };

    this.Controls.AddRange(heading, message);

    // Right-aligned button row, in the same order the WPF dialog used.
    var buttons = new (string Text, ReplaceDecision Decision)[] {
      ("&Yes", ReplaceDecision.Yes),
      ("Yes to &all", ReplaceDecision.YesToAll),
      ("&Skip", ReplaceDecision.Skip),
      ("S&kip all", ReplaceDecision.SkipAll),
      ("&Cancel", ReplaceDecision.Cancel),
    };

    var x = 480 - 16 - buttons.Length * ButtonWidth - (buttons.Length - 1) * 4;
    Button? cancelButton = null;
    foreach (var (text, decision) in buttons) {
      var captured = decision;
      var button = new Button {
        Bounds = new(x, 220 - 16 - ButtonHeight, ButtonWidth, ButtonHeight),
        Text = text,
      };
      button.Click += (_, _) => {
        this.Decision = captured;
        this.DialogResult = captured == ReplaceDecision.Cancel ? DialogResult.Cancel : DialogResult.OK;
        this.Close();
      };
      this.Controls.Add(button);
      if (decision == ReplaceDecision.Cancel) cancelButton = button;
      x += ButtonWidth + 4;
    }

    this.CancelButton = cancelButton;
  }
}
