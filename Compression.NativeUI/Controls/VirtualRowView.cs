using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>
/// Arguments handed to <see cref="VirtualRowView.RenderRow"/> for one visible row.
/// </summary>
/// <param name="Graphics">Surface to draw on.</param>
/// <param name="Index">Zero-based row index in the virtual list.</param>
/// <param name="Bounds">Row rectangle in control coordinates, already shifted by the horizontal scroll.</param>
/// <param name="Selected">Whether the row is part of the current selection.</param>
internal readonly record struct RowPaintContext(IGraphics Graphics, int Index, Rectangle Bounds, bool Selected);

/// <summary>
/// A scrollable list that only ever paints the rows on screen, leaving both the row content and the
/// row supply to the caller.
/// <para>
/// The previews show multi-hundred-megabyte buffers as text or a hex dump. Materialising a control
/// per line is not an option at that size, and the per-byte colouring the hex dump needs is beyond
/// what a text-and-subitems list row can express — so rows are drawn, not built, and only the
/// visible window is ever touched.
/// </para>
/// </summary>
internal class VirtualRowView : OwnerDrawnControl {
  private readonly VScrollBar _vertical = new();
  private readonly HScrollBar _horizontal = new();

  private int _rowCount;
  private int _rowHeight = 16;
  private int _contentWidth;
  private int _anchorIndex = -1;
  private readonly HashSet<int> _selection = [];

  public VirtualRowView() {
    this.BackColor = DefaultTheme.Instance.FieldBackground;

    this._vertical.SmallChange = 1;
    this._vertical.Scroll += (_, _) => this.Invalidate();
    this._vertical.ValueChanged += (_, _) => this.Invalidate();

    this._horizontal.SmallChange = 8;
    this._horizontal.Scroll += (_, _) => this.Invalidate();
    this._horizontal.ValueChanged += (_, _) => this.Invalidate();

    this.Controls.AddRange(this._vertical, this._horizontal);
  }

  /// <summary>Paints one row. Required — the control has no opinion about row content.</summary>
  public Action<RowPaintContext>? RenderRow { get; set; }

  /// <summary>Number of rows in the virtual list.</summary>
  public int RowCount {
    get => this._rowCount;
    set {
      this._rowCount = Math.Max(0, value);
      this._selection.Clear();
      this._anchorIndex = -1;
      this.UpdateScrollBars();
      this.Invalidate();
    }
  }

  /// <summary>Height of every row in pixels.</summary>
  public int RowHeight {
    get => this._rowHeight;
    set {
      this._rowHeight = Math.Max(1, value);
      this.UpdateScrollBars();
      this.Invalidate();
    }
  }

  /// <summary>Widest row in pixels, driving the horizontal scroll range.</summary>
  public int ContentWidth {
    get => this._contentWidth;
    set {
      this._contentWidth = Math.Max(0, value);
      this.UpdateScrollBars();
      this.Invalidate();
    }
  }

  /// <summary>Whether rows can be selected. Off for read-only decoration.</summary>
  public bool Selectable { get; set; } = true;

  /// <summary>Row indices currently selected, ascending.</summary>
  public IReadOnlyList<int> SelectedIndices => [.. this._selection.Order()];

  /// <summary>Index of the first row currently on screen.</summary>
  public int TopRow => this._vertical.Value;

  /// <summary>Scrolls so <paramref name="index"/> is on screen.</summary>
  public void EnsureVisible(int index) {
    if (index < 0 || index >= this._rowCount) return;
    var visible = this.VisibleRowCount;
    if (index < this._vertical.Value) this._vertical.Value = index;
    else if (index >= this._vertical.Value + visible) this._vertical.Value = Math.Max(0, index - visible + 1);
    this.Invalidate();
  }

  /// <summary>Drops the current selection.</summary>
  public void ClearSelection() {
    if (this._selection.Count == 0) return;
    this._selection.Clear();
    this.Invalidate();
  }

  private int ScrollBarSize => DefaultTheme.Instance.ScrollBarSize;

  private Rectangle Viewport {
    get {
      var w = Math.Max(0, this.Width - this.ScrollBarSize);
      var h = Math.Max(0, this.Height - this.ScrollBarSize);
      return new(0, 0, w, h);
    }
  }

  private int VisibleRowCount => Math.Max(1, this.Viewport.Height / this._rowHeight);

  private void UpdateScrollBars() {
    var bar = this.ScrollBarSize;
    this._vertical.Bounds = new(Math.Max(0, this.Width - bar), 0, bar, Math.Max(0, this.Height - bar));
    this._horizontal.Bounds = new(0, Math.Max(0, this.Height - bar), Math.Max(0, this.Width - bar), bar);

    var visible = this.VisibleRowCount;
    this._vertical.Minimum = 0;
    this._vertical.Maximum = Math.Max(0, this._rowCount - visible);
    this._vertical.LargeChange = visible;
    this._vertical.Visible = this._rowCount > visible;

    var viewWidth = this.Viewport.Width;
    this._horizontal.Minimum = 0;
    this._horizontal.Maximum = Math.Max(0, this._contentWidth - viewWidth);
    this._horizontal.LargeChange = Math.Max(1, viewWidth);
    this._horizontal.Visible = this._contentWidth > viewWidth;
  }

  protected override void OnPaint(PaintEventArgs e) {
    this.UpdateScrollBars();

    var g = e.Graphics;
    var viewport = this.Viewport;
    g.FillRectangle(this.BackColor, new(0, 0, this.Width, this.Height));
    if (this.RenderRow is not { } render || this._rowCount == 0) return;

    g.PushClip(viewport);
    try {
      var first = this._vertical.Value;
      var last = Math.Min(this._rowCount - 1, first + this.VisibleRowCount);
      var xOffset = -this._horizontal.Value;

      for (var i = first; i <= last; ++i) {
        var bounds = new Rectangle(xOffset, (i - first) * this._rowHeight, Math.Max(this._contentWidth, viewport.Width), this._rowHeight);
        var selected = this._selection.Contains(i);
        if (selected)
          g.FillRectangle(DefaultTheme.Instance.SelectionBackground, new(0, bounds.Y, viewport.Width, this._rowHeight));
        render(new(g, i, bounds, selected));
      }
    } finally {
      g.PopClip();
    }
  }

  protected override void OnMouseWheel(MouseEventArgs e) {
    // One notch is three rows, the platform convention on both backends.
    var rows = -Math.Sign(e.Delta) * 3;
    var max = Math.Max(0, this._rowCount - this.VisibleRowCount);
    this._vertical.Value = Math.Clamp(this._vertical.Value + rows, 0, max);
    this.Invalidate();
    base.OnMouseWheel(e);
  }

  protected override void OnMouseDown(MouseEventArgs e) {
    base.OnMouseDown(e);
    if (!this.Selectable) return;

    var row = this._vertical.Value + e.Y / this._rowHeight;
    if (row < 0 || row >= this._rowCount || e.Y >= this.Viewport.Height) return;

    if (e.Shift && this._anchorIndex >= 0) {
      this._selection.Clear();
      var (lo, hi) = this._anchorIndex <= row ? (this._anchorIndex, row) : (row, this._anchorIndex);
      for (var i = lo; i <= hi; ++i) this._selection.Add(i);
    } else if (e.Control) {
      if (!this._selection.Remove(row)) this._selection.Add(row);
      this._anchorIndex = row;
    } else {
      this._selection.Clear();
      this._selection.Add(row);
      this._anchorIndex = row;
    }

    this.Focus();
    this.Invalidate();
  }

  protected override void OnKeyDown(KeyEventArgs e) {
    base.OnKeyDown(e);
    var max = Math.Max(0, this._rowCount - this.VisibleRowCount);
    var visible = this.VisibleRowCount;

    var target = e.KeyCode switch {
      Keys.Up => this._vertical.Value - 1,
      Keys.Down => this._vertical.Value + 1,
      Keys.PageUp => this._vertical.Value - visible,
      Keys.PageDown => this._vertical.Value + visible,
      Keys.Home => 0,
      Keys.End => max,
      _ => int.MinValue,
    };
    if (target == int.MinValue) return;

    this._vertical.Value = Math.Clamp(target, 0, max);
    e.Handled = true;
    this.Invalidate();
  }
}
