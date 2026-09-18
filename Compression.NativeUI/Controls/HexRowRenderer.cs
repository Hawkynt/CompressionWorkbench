using System.Drawing;
using Compression.NativeUI.Views;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>
/// Draws hex dump rows, optionally colouring each byte by how common its value is in the buffer.
/// The colour rules are the ones the WPF hex line used: rare values get a green wash, common ones a
/// red wash, control bytes print orange and high bytes purple.
/// </summary>
internal sealed class HexRowRenderer {
  private static readonly Color Offset = Color.Gray;
  private static readonly Color Plain = Color.Black;
  private static readonly Color Control = Color.FromArgb(0xFF, 0x98, 0x00);
  private static readonly Color High = Color.FromArgb(0x9C, 0x27, 0xB0);

  private readonly Font _font;
  private int _charWidth;

  public HexRowRenderer(Font font) => this._font = font;

  /// <summary>Frequency percentile lookup (byte value to 0..255 percentile). Null disables colouring.</summary>
  public byte[]? FrequencyPercentiles { get; set; }

  /// <summary>Whether to colour bytes by frequency; monochrome when false.</summary>
  public bool ColorizeHex { get; set; }

  /// <summary>The rows to draw.</summary>
  public LazyHexLines? Lines { get; set; }

  /// <summary>Width of one monospace character, measured once against the drawing surface.</summary>
  public int CharWidth => this._charWidth;

  /// <summary>Total pixel width a row of <paramref name="bytesPerRow"/> bytes needs.</summary>
  public int MeasureRowWidth(int bytesPerRow, int offsetChars) {
    var cw = this._charWidth > 0 ? this._charWidth : 8;
    var groups = (bytesPerRow - 1) / 8;
    return (offsetChars + 2) * cw
           + bytesPerRow * cw * 3
           + groups * cw
           + cw
           + bytesPerRow * cw;
  }

  public void Render(in RowPaintContext context) {
    if (this.Lines is not { } lines || context.Index >= lines.Count) return;

    var g = context.Graphics;
    if (this._charWidth == 0) this._charWidth = Math.Max(1, g.MeasureText("0", this._font).Width);

    var line = lines[context.Index];
    var cw = this._charWidth;
    var y = context.Bounds.Y;
    var height = context.Bounds.Height;
    var x = context.Bounds.X;

    g.DrawText(line.OffsetText, this._font, Offset, new(x, y, line.OffsetText.Length * cw, height), ContentAlignment.MiddleLeft);
    x += (line.OffsetText.Length + 2) * cw;

    for (var i = 0; i < line.BytesPerRow; ++i) {
      if (i < line.ByteCount) {
        var hb = line.Bytes[i];

        if (this.ColorizeHex && this.FrequencyPercentiles is { } pct)
          g.FillRectangle(PercentileToBackground(pct[hb.Value]), new(x, y, (int)(cw * 2.2), height));

        g.DrawText(hb.HexText, this._font, this.Foreground(hb.Value), new(x, y, cw * 2, height), ContentAlignment.MiddleLeft);
      }

      x += cw * 3;

      // Group separator every eight bytes.
      if (i > 0 && i < line.BytesPerRow - 1 && (i + 1) % 8 == 0) x += cw;
    }

    x += cw;

    for (var i = 0; i < line.ByteCount; ++i) {
      var hb = line.Bytes[i];

      if (this.ColorizeHex && this.FrequencyPercentiles is { } pct) {
        var bg = PercentileToBackground(pct[hb.Value]);
        g.FillRectangle(Color.FromArgb(80, bg.R, bg.G, bg.B), new(x, y, cw, height));
      }

      g.DrawText(hb.AsciiChar.ToString(), this._font, this.Foreground(hb.Value), new(x, y, cw, height), ContentAlignment.MiddleLeft);
      x += cw;
    }
  }

  private Color Foreground(byte value) {
    if (!this.ColorizeHex) return Plain;
    if (value < 0x20 || value == 0x7F) return Control;
    return value >= 0x80 ? High : Plain;
  }

  /// <summary>Green for rare values, neutral in the middle, red for common ones.</summary>
  private static Color PercentileToBackground(byte percentile) {
    var t = percentile / 255.0;
    if (t < 0.1) return Color.FromArgb(40, 76, 175, 80);
    if (t > 0.9) return Color.FromArgb(40, 244, 67, 54);
    return Color.FromArgb(10, 158, 158, 158);
  }
}
