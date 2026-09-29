using System.Drawing;
using Compression.NativeUI.Theming;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>
/// The compression ratio as a liquid level in an isometric glass box, viewed corner-on.
/// <para>
/// <see cref="IGraphics"/> draws rectangles, not arbitrary polygons, so the box is rasterized
/// through the same shape renderer the icons use and blitted as one image.
/// </para>
/// </summary>
internal sealed class RatioBarControl : OwnerDrawnControl {
  private double _ratio = -1;

  public RatioBarControl() => this.BackColor = Color.Transparent;

  /// <summary>Compressed size as a percentage of the original; negative hides the fill.</summary>
  public double Ratio {
    get => this._ratio;
    set {
      this._ratio = value;
      this.Invalidate();
    }
  }

  protected override void OnPaint(PaintEventArgs e) {
    var w = this.Width;
    var h = this.Height;
    if (w <= 8 || h <= 8) return;

    e.Graphics.FillRectangle(DefaultTheme.Instance.WindowBackground, new(0, 0, w, h));

    // The isometric rise is derived from the width, so a cell that is wide and short leaves no room
    // between the top and bottom faces and the box turns itself inside out. Nothing sensible can be
    // drawn at that aspect, so only the ground is.
    if (h < w * 0.2 + 12) return;

    e.Graphics.DrawImage(Images.FromArgb(w, h, VectorIconRenderer.RenderPixels(this.BuildShapes(w, h), w, h)), new(0, 0, w, h));
  }

  /// <summary>
  /// Lays out the eight corners of the cuboid. The front vertical edge runs down the centre line;
  /// each side face steps back by <c>dx</c> horizontally and rises by <c>dy</c>, the isometric
  /// half-rise. Only the height varies with the control, so the box keeps its proportions.
  /// </summary>
  private List<IconShape> BuildShapes(int w, int h) {
    var cx = w / 2.0;
    var dx = w * 0.40;
    var dy = dx * 0.5;

    var topY = dy + 4;
    var botY = h - 4;

    var fBot = (X: cx, Y: botY);
    var fTop = (X: cx, Y: topY);
    var lBot = (X: cx - dx, Y: botY - dy);
    var lTop = (X: cx - dx, Y: topY - dy);
    var rBot = (X: cx + dx, Y: botY - dy);
    var rTop = (X: cx + dx, Y: topY - dy);
    var bBot = (X: cx, Y: botY - 2 * dy);
    var bTop = (X: cx, Y: topY - 2 * dy);

    var shapes = new List<IconShape>();
    var clamped = this._ratio >= 0 ? Math.Clamp(this._ratio, 0, 100) / 100.0 : 0;

    if (clamped > 0) {
      var fFill = Lerp(fBot, fTop, clamped);
      var lFill = Lerp(lBot, lTop, clamped);
      var rFill = Lerp(rBot, rTop, clamped);
      var bFill = Lerp(bBot, bTop, clamped);

      // Front-left face, front-right face (a shade darker), then the liquid surface diamond.
      shapes.Add(new(IconShapeKind.Fill, [[fFill, lFill, lBot, fBot]], 0xBB1E90FF));
      shapes.Add(new(IconShapeKind.Fill, [[fFill, rFill, rBot, fBot]], 0xBB146ECC));
      shapes.Add(new(IconShapeKind.Fill, [[fFill, lFill, bFill, rFill]], 0x8864B5F6));
    }

    // Visible wireframe edges.
    const uint Edge = 0xFF555555;
    shapes.Add(new(IconShapeKind.Stroke, [
      [fBot, lBot], [fBot, rBot],
      [fBot, fTop], [lBot, lTop], [rBot, rTop],
      [fTop, lTop], [fTop, rTop],
    ], Edge, 1.3));

    // Edges hidden behind the box, drawn lighter to read as depth.
    const uint Back = 0xFFAAAAAA;
    shapes.Add(new(IconShapeKind.Stroke, [
      [lBot, bBot], [rBot, bBot],
      [bBot, bTop],
      [lTop, bTop], [rTop, bTop],
    ], Back, 0.7));

    return shapes;
  }

  private static (double X, double Y) Lerp((double X, double Y) a, (double X, double Y) b, double t)
    => (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
