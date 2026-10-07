using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>
/// A read-only log that wraps every line to its width and keeps the newest line in view; the mouse
/// wheel scrolls back through older ones.
/// </summary>
/// <remarks>
/// A multiline text box neither wraps nor scrolls sideways on every backend, so a long outcome line
/// ran off its right edge. Drawing the lines here wraps them the same way everywhere.
/// </remarks>
internal sealed class LogView : OwnerDrawnControl {
  private const int Inset = 3;

  private readonly List<string> _lines = [];
  private int _scrollBack;

  /// <summary>Every line logged since the last <see cref="Clear"/>, unwrapped.</summary>
  public string LogText => string.Join(Environment.NewLine, this._lines);

  public void Append(string line) {
    this._lines.AddRange(line.Replace("\r\n", "\n").Split('\n'));
    this._scrollBack = 0;
    this.Invalidate();
  }

  public void Clear() {
    this._lines.Clear();
    this._scrollBack = 0;
    this.Invalidate();
  }

  /// <summary>
  /// Breaks <paramref name="line"/> into rows no wider than <paramref name="width"/>: at spaces where
  /// it can, inside a word only when the word alone is wider than the row.
  /// </summary>
  internal static List<string> Wrap(string line, int width, Func<string, int> measure) {
    var rows = new List<string>();
    if (line.Length == 0 || width <= 0 || measure(line) <= width) {
      rows.Add(line);
      return rows;
    }

    var rest = line;
    while (rest.Length > 0) {
      if (measure(rest) <= width) {
        rows.Add(rest);
        break;
      }

      // The longest prefix that fits, found by halving rather than one character at a time.
      int lo = 1, hi = rest.Length;
      while (lo < hi) {
        var mid = (lo + hi + 1) / 2;
        if (measure(rest[..mid]) <= width) lo = mid;
        else hi = mid - 1;
      }

      var cut = lo;
      var space = rest.LastIndexOf(' ', Math.Min(cut, rest.Length - 1));
      if (space > 0) cut = space;
      rows.Add(rest[..cut].TrimEnd());
      rest = rest[cut..].TrimStart();
    }

    return rows;
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    var theme = DefaultTheme.Instance;
    g.FillRectangle(theme.FieldBackground, new(0, 0, this.Width, this.Height));
    g.DrawRectangle(theme.Border, new(0, 0, this.Width - 1, this.Height - 1));

    var width = this.Width - 2 * Inset;
    var rowHeight = Math.Max(1, g.MeasureText("Ag", this.Font).Height);
    var rows = this._lines.SelectMany(l => Wrap(l, width, s => g.MeasureText(s, this.Font).Width)).ToList();
    var visible = Math.Max(1, (this.Height - 2 * Inset) / rowHeight);
    this._scrollBack = Math.Clamp(this._scrollBack, 0, Math.Max(0, rows.Count - visible));

    var first = Math.Max(0, rows.Count - visible - this._scrollBack);
    var y = Inset;
    for (var i = first; i < rows.Count && y + rowHeight <= this.Height - Inset + 1; ++i, y += rowHeight)
      g.DrawText(rows[i], this.Font, theme.ControlText, new(Inset, y, width, rowHeight), ContentAlignment.MiddleLeft);
  }

  protected override void OnMouseWheel(MouseEventArgs e) {
    this._scrollBack += e.Delta > 0 ? 3 : -3;
    if (this._scrollBack < 0) this._scrollBack = 0;
    this.Invalidate();
  }
}
