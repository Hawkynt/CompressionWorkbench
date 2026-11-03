using System.Drawing;
using Hawkynt.NativeForms;

namespace Compression.NativeUI.Views;

/// <summary>Asks for the password of an encrypted archive; says so when the last one was wrong.</summary>
internal sealed class PasswordDialog : Form {
  private readonly TextBox _password = new() { UseSystemPasswordChar = true };
  private readonly Button _ok = new() { Text = "OK", DialogResult = DialogResult.OK };
  private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };

  public PasswordDialog(string archiveName, bool previousWasWrong) {
    this.Text = "Password";
    this.ClientSize = new(380, 140);
    this.StartPosition = FormStartPosition.CenterParent;
    this.FormBorderStyle = FormBorderStyle.FixedDialog;
    this.MaximizeBox = false;
    this.MinimizeBox = false;

    var prompt = new Label {
      Text = previousWasWrong
        ? $"The password is wrong for {archiveName}. Enter it again:"
        : $"{archiveName} is encrypted. Enter its password:",
      Bounds = new(12, 12, 356, 36),
    };
    if (previousWasWrong) prompt.ForeColor = Color.Firebrick;
    this._password.Bounds = new(12, 56, 356, 24);
    this._ok.Bounds = new(212, 100, 75, 28);
    this._cancel.Bounds = new(293, 100, 75, 28);
    this.AcceptButton = this._ok;
    this.CancelButton = this._cancel;
    this.Controls.AddRange(prompt, this._password, this._ok, this._cancel);
  }

  /// <summary>The password typed.</summary>
  public string Password => this._password.Text;
}
