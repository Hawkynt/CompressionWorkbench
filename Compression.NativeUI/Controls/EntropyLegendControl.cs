using System.Drawing;
using Compression.NativeUI.Theming;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>
/// The key to the entropy bar: a swatch per band with the name of what that colour means.
/// <para>
/// The swatches are not colour constants — each is <see cref="EntropyPalette.ToColor"/> sampled at
/// the entropy its label describes, so the legend cannot drift out of step with the bar it explains.
/// </para>
/// </summary>
internal sealed class EntropyLegendControl : OwnerDrawnControl {
  private const int SwatchWidth = 16;
  private const int SwatchHeight = 12;
  private const int SwatchToLabel = 3;
  private const int BetweenEntries = 10;

  /// <summary>
  /// The bands, each named for what data at that entropy usually is. Eight bits per byte is the
  /// ceiling, so the samples span the full ramp.
  /// </summary>
  private static readonly (double Entropy, string Label)[] Bands = [
    (0.0, "Plain"),
    (2.0, "Structured"),
    (4.0, "Light Comp."),
    (6.0, "Dict Comp."),
    (8.0, "Random/Enc."),
  ];

  public EntropyLegendControl() => this.BackColor = Color.Transparent;

  /// <summary>
  /// Roughly the width needed to show every band. Real text measurement needs the graphics context
  /// a control only has while painting, so this estimates per character — it decides how much room
  /// the legend asks for, never where the text goes, and painting clips rather than overflowing.
  /// </summary>
  public int PreferredWidth {
    get {
      var font = this.LegendFont;
      var total = 0;

      foreach (var (_, label) in Bands)
        total += SwatchWidth + SwatchToLabel
               + (int)Math.Ceiling(label.Length * font.SizeInPoints * 0.62) + BetweenEntries;

      return Math.Max(0, total - BetweenEntries);
    }
  }

  private Font LegendFont => new(DefaultTheme.Instance.DefaultFont.Family, 8.5f, FontStyle.Regular);

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    var height = this.Height;
    if (this.Width <= 0 || height <= 0) return;

    g.FillRectangle(DefaultTheme.Instance.ControlBackground, new(0, 0, this.Width, height));

    var font = this.LegendFont;
    var swatchTop = Math.Max(0, (height - SwatchHeight) / 2);
    var x = 0;

    foreach (var (entropy, label) in Bands) {
      if (x >= this.Width) break;

      g.FillRoundedRectangle(EntropyPalette.ToColor(entropy), new(x, swatchTop, SwatchWidth, SwatchHeight), 1);
      g.DrawRoundedRectangle(Color.FromArgb(0x88, 0x88, 0x88), new(x, swatchTop, SwatchWidth, SwatchHeight), 1, 1);
      x += SwatchWidth + SwatchToLabel;

      var width = g.MeasureText(label, font).Width;
      g.DrawText(label, font, Color.FromArgb(0x22, 0x22, 0x22), new(x, 0, width, height), ContentAlignment.MiddleLeft);
      x += width + BetweenEntries;
    }
  }
}
