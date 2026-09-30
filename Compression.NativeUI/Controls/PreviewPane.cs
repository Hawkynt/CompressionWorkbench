using System.Text;
using Compression.NativeUI.Theming;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>A preview worked out and ready to show: the picture's first frame, or the text, or just a caption.</summary>
internal sealed record PreparedPreview(PreviewKind Kind, string Caption, int Width, int Height, int[]? Pixels, string? Text);

/// <summary>What the preview pane is showing.</summary>
internal enum PreviewKind {
  /// <summary>A caption only: nothing selected, a folder, or bytes that are neither picture nor text.</summary>
  None,
  /// <summary>The first frame of a picture.</summary>
  Image,
  /// <summary>The start of a text file.</summary>
  Text,
}

/// <summary>
/// The file manager's preview pane: the selected entry as a picture when any of the image decoders
/// can read it, as text when it reads as text, and otherwise just its name. The full preview window
/// remains the place for hex, statistics, encodings and animation; this is a glance.
/// </summary>
internal sealed class PreviewPane : Panel {
  /// <summary>How much of a text file the pane shows.</summary>
  internal const int TextExcerptChars = 16 * 1024;

  private const int CaptionHeight = 40;

  /// <summary>What the pane says while there is nothing to preview.</summary>
  internal const string NothingSelected = "Select a file to preview.";

  private readonly PictureBox _picture = new() { SizeMode = PictureBoxSizeMode.Zoom, Visible = false, Dock = DockStyle.Fill };
  private readonly TextBox _text = new() { Multiline = true, ReadOnly = true, Visible = false, Dock = DockStyle.Fill };
  private readonly Label _caption = new() { TextAlign = ContentAlignment.MiddleCenter, Height = CaptionHeight };

  public PreviewPane() {
    // Docking lays the pane out: the caption along the bottom under a picture or text, or filling
    // the pane on its own. Later siblings dock first, so the caption claims its strip before the fill.
    this.Controls.AddRange(this._picture, this._text, this._caption);
    this.ShowCaption(NothingSelected);
  }

  /// <summary>What is on show now.</summary>
  public PreviewKind Kind { get; private set; }

  /// <summary>The line under the preview: the name, and what the preview is of.</summary>
  public string Caption => this._caption.Text;

  /// <summary>The text on show when <see cref="Kind"/> is <see cref="PreviewKind.Text"/>.</summary>
  public string Excerpt => this._text.Text;

  /// <summary>Shows only a caption — nothing selected, a folder, a file too large to read for a glance.</summary>
  public void ShowCaption(string caption) {
    this.Kind = PreviewKind.None;
    this._picture.Image = null;
    this._picture.Visible = false;
    this._text.Text = "";
    this._text.Visible = false;
    this._caption.Text = caption;
    this.LayoutChildren();
  }

  /// <summary>Shows <paramref name="data"/>, the contents of <paramref name="name"/>, as well as it can.</summary>
  public void ShowContent(string name, byte[] data) => this.Show(Prepare(name, data));

  /// <summary>
  /// Works out what to show for <paramref name="data"/> — decodes the picture, sniffs the text —
  /// without touching the control, so it can run off the UI thread; <see cref="Show"/> then only
  /// puts the result on screen.
  /// </summary>
  internal static PreparedPreview Prepare(string name, byte[] data) {
    if (PreviewImageDecoder.TryDecode(data, name, out var picture) && picture.Frames.Count > 0)
      return new(PreviewKind.Image, $"{name}{Environment.NewLine}{picture.Width} × {picture.Height}", picture.Width, picture.Height, picture.Frames[0].ToArray(), null);

    return AsText(data, TextExcerptChars) is { } text
      ? new(PreviewKind.Text, name, 0, 0, null, text)
      : new(PreviewKind.None, $"{name}{Environment.NewLine}No preview", 0, 0, null, null);
  }

  /// <summary>Puts a <see cref="Prepare">prepared</see> preview on screen.</summary>
  public void Show(PreparedPreview prepared) {
    switch (prepared.Kind) {
      case PreviewKind.Image:
        this.Kind = PreviewKind.Image;
        this._text.Visible = false;
        this._picture.Image = Images.FromArgb(prepared.Width, prepared.Height, prepared.Pixels!);
        this._picture.Visible = true;
        this._caption.Text = prepared.Caption;
        break;
      case PreviewKind.Text:
        this.Kind = PreviewKind.Text;
        this._picture.Image = null;
        this._picture.Visible = false;
        this._text.Text = prepared.Text!;
        this._text.Visible = true;
        this._caption.Text = prepared.Caption;
        break;
      default:
        this.ShowCaption(prepared.Caption);
        return;
    }

    this.LayoutChildren();
  }

  // With nothing above it the caption takes the whole pane, centred, as a file manager's does.
  private void LayoutChildren() {
    this._caption.Dock = this.Kind == PreviewKind.None ? DockStyle.Fill : DockStyle.Bottom;
    this._caption.Height = CaptionHeight;
    this.PerformLayout();
  }

  /// <summary>
  /// The start of <paramref name="data"/> as text, or null when it is not text: a NUL byte, or more
  /// than one control character in a hundred other than tab, line breaks and form feed. A byte-order
  /// mark picks UTF-16 or UTF-8; anything else is read as UTF-8, whose decoder never fails.
  /// </summary>
  internal static string? AsText(byte[] data, int maxChars) {
    if (data.Length == 0) return "";

    var sample = data.AsSpan(0, Math.Min(data.Length, maxChars * 4));
    string decoded;
    if (sample.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) decoded = Encoding.Unicode.GetString(sample[2..]);
    else if (sample.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) decoded = Encoding.BigEndianUnicode.GetString(sample[2..]);
    else {
      if (sample.Contains((byte)0)) return null;
      decoded = Encoding.UTF8.GetString(sample.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? sample[3..] : sample);
    }

    var controls = 0;
    foreach (var c in decoded)
      if (char.IsControl(c) && c is not ('\t' or '\r' or '\n' or '\f') || c == '�')
        ++controls;

    if (controls * 100 > decoded.Length) return null;
    return decoded.Length > maxChars ? decoded[..maxChars] : decoded;
  }
}
