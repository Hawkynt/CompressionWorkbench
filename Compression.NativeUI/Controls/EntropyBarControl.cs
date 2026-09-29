using System.Drawing;
using Compression.Analysis.Statistics;
using Compression.NativeUI.Theming;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>
/// The whole file as one horizontal strip, each region coloured by its entropy and sized by its
/// share of the bytes. Hovering reports the region; clicking selects it in the region table.
/// </summary>
internal sealed class EntropyBarControl : OwnerDrawnControl {
  private readonly ToolTip _toolTip = new() { InitialDelay = 0, AutoPopDelay = 30000 };

  private List<RegionProfile> _regions = [];
  private long _totalSpan;
  private int _hoverIndex = -1;

  public EntropyBarControl() => this.Cursor = Cursors.Hand;

  /// <summary>Raised with the index of the clicked region.</summary>
  public event Action<int>? RegionClicked;

  public void SetRegions(List<RegionProfile>? regions, long fileSize) {
    this._regions = regions ?? [];
    this._totalSpan = fileSize > 0 ? fileSize : this._regions.Sum(r => (long)r.Length);
    this._hoverIndex = -1;
    this.Invalidate();
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.FillRectangle(DefaultTheme.Instance.ControlBackground, new(0, 0, this.Width, this.Height));
    if (this._regions.Count == 0 || this._totalSpan == 0) return;

    foreach (var (index, bounds) in this.RegionBounds())
      g.FillRectangle(EntropyPalette.ToColor(this._regions[index].Entropy), bounds);

    g.DrawRectangle(Color.FromArgb(0x88, 0x88, 0x88), new(0, 0, this.Width - 1, this.Height - 1), 1);
  }

  /// <summary>
  /// Walks the regions left to right. Every region gets at least one pixel so tiny ones stay
  /// visible, and the last one absorbs the rounding so the strip has no gap at its right edge.
  /// </summary>
  private IEnumerable<(int Index, Rectangle Bounds)> RegionBounds() {
    var width = this.Width;
    double x = 0;

    for (var i = 0; i < this._regions.Count; ++i) {
      if (x >= width) yield break;

      var w = i == this._regions.Count - 1
        ? Math.Max(0, width - x)
        : Math.Max(1, this._regions[i].Length / (double)this._totalSpan * width);
      if (x + w > width) w = Math.Max(0, width - x);
      if (w <= 0) yield break;

      yield return (i, new((int)x, 0, (int)Math.Ceiling(w), this.Height));
      x += w;
    }
  }

  private int IndexAt(int x) {
    foreach (var (index, bounds) in this.RegionBounds())
      if (x >= bounds.X && x < bounds.Right) return index;
    return -1;
  }

  protected override void OnMouseMove(MouseEventArgs e) {
    base.OnMouseMove(e);

    var index = this.IndexAt(e.X);
    if (index == this._hoverIndex) return;
    this._hoverIndex = index;

    if (index < 0) {
      this._toolTip.Hide();
      return;
    }

    var r = this._regions[index];
    this._toolTip.SetToolTip(this,
      $"Offset 0x{r.Offset:X} ({r.Offset:N0})\n"
      + $"Length: {r.Length:N0} bytes\n"
      + $"Entropy: {r.Entropy:F4} bits/byte\n"
      + $"Chi²: {r.ChiSquare:F1}  Mean: {r.Mean:F1}\n"
      + $"Type: {r.Classification}");
  }

  protected override void OnMouseLeave(EventArgs e) {
    base.OnMouseLeave(e);
    this._hoverIndex = -1;
    this._toolTip.Hide();
  }

  protected override void OnMouseDown(MouseEventArgs e) {
    base.OnMouseDown(e);
    var index = this.IndexAt(e.X);
    if (index >= 0) this.RegionClicked?.Invoke(index);
  }
}
