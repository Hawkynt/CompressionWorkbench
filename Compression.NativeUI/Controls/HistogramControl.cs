using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>
/// Byte-frequency histogram: 256 log-scaled bars over shaded bands that mark the control, printable
/// and high byte ranges. The most and least common values are called out in red and green, and
/// hovering a bar reports that byte's exact share of the buffer.
/// </summary>
internal sealed class HistogramControl : OwnerDrawnControl {
  private static readonly Color ControlByte = Color.FromArgb(0xFF, 0x98, 0x00);
  private static readonly Color AsciiByte = Color.FromArgb(0x33, 0x99, 0xFF);
  private static readonly Color HighByte = Color.FromArgb(0x9C, 0x27, 0xB0);
  private static readonly Color MostCommon = Color.FromArgb(0xE0, 0x40, 0x40);
  private static readonly Color LeastCommon = Color.FromArgb(0x4C, 0xAF, 0x50);

  private readonly ToolTip _toolTip = new() { InitialDelay = 0, AutoPopDelay = 60000 };

  private long[]? _frequency;
  private long _total;
  private int _mostIndex;
  private int _leastIndex = -1;
  private int _hoverByte = -1;

  public HistogramControl() {
    this.BackColor = Color.Transparent;
    this._toolTip.SetToolTip(this, "");
  }

  /// <summary>Recomputes the bars from <paramref name="data"/>.</summary>
  public void SetData(ReadOnlySpan<byte> data) {
    if (data.IsEmpty) {
      this._frequency = null;
      this.Invalidate();
      return;
    }

    var freq = new long[256];
    foreach (var b in data) ++freq[b];

    var most = 0;
    var least = -1;
    for (var i = 0; i < 256; ++i) {
      if (freq[i] > freq[most]) most = i;
      if (freq[i] > 0 && (least < 0 || freq[i] < freq[least])) least = i;
    }

    this._frequency = freq;
    this._total = data.Length;
    this._mostIndex = most;
    this._leastIndex = least;
    this.Invalidate();
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    var width = this.Width;
    var height = this.Height;
    if (width <= 0 || height <= 0) return;

    g.FillRectangle(DefaultTheme.Instance.FieldBackground, new(0, 0, width, height));
    if (this._frequency is not { } freq) return;

    var barWidth = width / 256.0;

    // Range bands behind the bars: control, printable ASCII, and the 0x7F/0x80 boundary.
    g.FillRectangle(Color.FromArgb(0x30, 0xFF, 0x98, 0x00), new(0, 0, (int)(barWidth * 32), height));
    g.FillRectangle(Color.FromArgb(0x18, 0x33, 0x99, 0xFF), new((int)(barWidth * 32), 0, (int)(barWidth * 95), height));
    g.FillRectangle(Color.FromArgb(0x20, 0x9C, 0x27, 0xB0), new((int)(barWidth * 127), 0, (int)(barWidth * 2), height));

    var maxFreq = freq.Max();
    if (maxFreq == 0) return;
    var logMax = Math.Log(maxFreq + 1);

    for (var i = 0; i < 256; ++i) {
      if (freq[i] == 0) continue;

      var barHeight = (int)Math.Max(Math.Log(freq[i] + 1) / logMax * (height - 2), 1);
      var color = i == this._mostIndex ? MostCommon
        : i == this._leastIndex ? LeastCommon
        : BarColor(i);

      g.FillRectangle(color, new(
        (int)(i * barWidth),
        height - barHeight,
        Math.Max((int)barWidth, 1),
        barHeight));
    }
  }

  protected override void OnMouseMove(MouseEventArgs e) {
    base.OnMouseMove(e);
    if (this._frequency is not { } freq || this.Width <= 0) return;

    var byteIndex = Math.Clamp((int)(e.X / (this.Width / 256.0)), 0, 255);
    if (byteIndex == this._hoverByte) return;
    this._hoverByte = byteIndex;

    this._toolTip.SetToolTip(this, FormatTooltip(
      byteIndex, freq[byteIndex], this._total,
      byteIndex == this._mostIndex, byteIndex == this._leastIndex));
  }

  protected override void OnMouseLeave(EventArgs e) {
    base.OnMouseLeave(e);
    this._hoverByte = -1;
    this._toolTip.Hide();
  }

  private static Color BarColor(int byteValue) {
    if (byteValue < 0x20 || byteValue == 0x7F) return ControlByte;
    return byteValue < 0x7F ? AsciiByte : HighByte;
  }

  private static string FormatTooltip(int byteValue, long count, long total, bool isMost, bool isLeast) {
    var pct = 100.0 * count / total;
    var label = isMost ? "  [MOST COMMON]" : isLeast ? "  [LEAST COMMON]" : "";
    var category = byteValue == 0 ? "Null"
      : byteValue < 0x20 || byteValue == 0x7F ? "Control"
      : byteValue < 0x7F ? "Printable ASCII"
      : "High byte";

    return $"Byte 0x{byteValue:X2} ({byteValue}) — {StatisticsControl.FormatByteChar(byteValue)}{label}\n"
         + $"Count: {count:N0} / {total:N0}\n"
         + $"Frequency: {pct:F4}%\n"
         + $"Category: {category}\n"
         + $"Expected (uniform): {100.0 / 256:F4}%\n"
         + $"Deviation: {pct - 100.0 / 256:+0.0000;-0.0000;0.0000}%";
  }
}
