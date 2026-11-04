using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Views;

/// <summary>
/// Asks for the password of an encrypted archive — again, saying so, when the last one was wrong.
/// </summary>
internal sealed class PasswordDialog : Form {
  private readonly TextBox _password = new() { UseSystemPasswordChar = true };
  private readonly Button _ok = new() { Text = "OK", DialogResult = DialogResult.OK };
  private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };

  public PasswordDialog(string archiveName, bool previousWasWrong) {
    this.Text = "Password";
    this.ClientSize = new(420, 150);
    this.StartPosition = FormStartPosition.CenterParent;
    this.FormBorderStyle = FormBorderStyle.FixedDialog;
    this.MaximizeBox = false;
    this.MinimizeBox = false;

    var heading = new Label {
      Font = new(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold),
      Text = previousWasWrong
        ? $"The password does not open {archiveName}. Try again:"
        : $"{archiveName} is encrypted. Enter its password:",
      Bounds = new(12, 12, 396, 40),
    };

    this._ok.Click += (_, _) => {
      this.Password = this._password.Text;
      this.DialogResult = DialogResult.OK;
      this.Close();
    };
    this._cancel.Click += (_, _) => this.Close();
    this.AcceptButton = this._ok;
    this.CancelButton = this._cancel;

    this.Controls.AddRange(heading, new Label { Text = "Password:", Bounds = new(12, 64, 80, 20) }, this._password, this._ok, this._cancel);
    this._password.Bounds = new(96, 62, 312, 24);
    this._ok.Bounds = new(240, 108, 80, 28);
    this._cancel.Bounds = new(328, 108, 80, 28);
  }

  /// <summary>The password typed, or null when cancelled.</summary>
  public string? Password { get; private set; }

  /// <summary>Shows the dialog; the password, or <see langword="null"/> when the user cancels.</summary>
  public static string? Ask(string archiveName, bool previousWasWrong) {
    var dialog = new PasswordDialog(archiveName, previousWasWrong);
    return dialog.ShowDialog() == DialogResult.OK ? dialog.Password : null;
  }
}
