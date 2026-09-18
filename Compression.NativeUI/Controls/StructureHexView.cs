using System.Drawing;
using Compression.Analysis.Structure;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Controls;

/// <summary>
/// Hex dump of the bytes a structure template covers, with each field washed in its own colour and
/// a few rows of dimmed context above and below so the struct's place in the file stays visible.
/// </summary>
internal sealed class StructureHexView : VirtualRowView {
  internal const int BytesPerRow = 16;
  private const int ContextRows = 4;

  /// <summary>Field tints, cycled by field index. Alpha keeps the hex text readable through them.</summary>
  internal static readonly Color[] FieldColors = [
    Color.FromArgb(70, 0x42, 0xA5, 0xF5), // blue
    Color.FromArgb(70, 0x66, 0xBB, 0x6A), // green
    Color.FromArgb(70, 0xFF, 0xA7, 0x26), // orange
    Color.FromArgb(70, 0xAB, 0x47, 0xBC), // purple
    Color.FromArgb(70, 0xEF, 0x53, 0x50), // red
    Color.FromArgb(70, 0x26, 0xC6, 0xDA), // cyan
    Color.FromArgb(70, 0xFF, 0xEE, 0x58), // yellow
    Color.FromArgb(70, 0x8D, 0x6E, 0x63), // brown
    Color.FromArgb(70, 0xEC, 0x40, 0x7A), // pink
    Color.FromArgb(70, 0x78, 0x90, 0x9C), // teal gray
    Color.FromArgb(70, 0x9C, 0xCC, 0x65), // lime
    Color.FromArgb(70, 0x5C, 0x6B, 0xC0), // indigo
  ];

  private static readonly Font MonoFont = new("Cascadia Mono", 8.5f, FontStyle.Regular);

  private readonly ToolTip _toolTip = new() { InitialDelay = 0, AutoPopDelay = 30000 };

  private byte[]? _data;
  private int[]? _byteFieldMap;
  private string[] _fieldNames = [];
  private int _displayStart;
  private int _structRowStart;
  private int _structRowEnd;
  private int _charWidth;
  private int _offsetWidth = 4;
  private int _hoverByte = -1;

  public StructureHexView() {
    this.RowHeight = 15;
    this.Selectable = false;
    this.RenderRow = this.Render;
  }

  /// <summary>Rebuilds the view for <paramref name="fields"/> parsed at <paramref name="structOffset"/>.</summary>
  public void SetStructure(byte[]? data, List<ParsedField>? fields, int structOffset) {
    this._data = data;
    if (data is null || fields is null) {
      this.RowCount = 0;
      return;
    }

    var fieldMap = new int[data.Length];
    Array.Fill(fieldMap, -1);
    var names = new List<string>();
    BuildFieldMap(fields, fieldMap, names);
    this._byteFieldMap = fieldMap;
    this._fieldNames = [.. names];

    var structEnd = structOffset;
    foreach (var f in FlattenFields(fields))
      structEnd = Math.Max(structEnd, f.Offset + f.Size);

    this._structRowStart = structOffset / BytesPerRow * BytesPerRow;
    this._structRowEnd = (structEnd + BytesPerRow - 1) / BytesPerRow * BytesPerRow;
    if (this._structRowEnd <= this._structRowStart)
      this._structRowEnd = this._structRowStart + BytesPerRow * 2;

    this._displayStart = Math.Max(0, this._structRowStart - ContextRows * BytesPerRow);
    var displayEnd = Math.Min(data.Length, this._structRowEnd + ContextRows * BytesPerRow);

    this._offsetWidth = data.Length > 0xFFFF ? 8 : 4;
    this.RowCount = (displayEnd - this._displayStart + BytesPerRow - 1) / BytesPerRow;

    var cw = this._charWidth > 0 ? this._charWidth : 8;
    this.ContentWidth = (this._offsetWidth + 2) * cw + BytesPerRow * 3 * cw + cw * 2 + BytesPerRow * cw + cw;
    this.Invalidate();
  }

  /// <summary>Scrolls so the row holding <paramref name="byteOffset"/> is at the top.</summary>
  public void ScrollToOffset(int byteOffset)
    => this.EnsureVisible(Math.Max(0, (byteOffset - this._displayStart) / BytesPerRow));

  private void Render(RowPaintContext context) {
    if (this._data is not { } data || this._byteFieldMap is not { } fieldMap) return;

    var g = context.Graphics;
    if (this._charWidth == 0) this._charWidth = Math.Max(1, g.MeasureText("0", MonoFont).Width);
    var cw = this._charWidth;

    var lineOffset = this._displayStart + context.Index * BytesPerRow;
    if (lineOffset >= data.Length) return;

    var isContext = lineOffset < this._structRowStart || lineOffset >= this._structRowEnd;
    var y = context.Bounds.Y;
    var height = context.Bounds.Height;

    g.DrawText(lineOffset.ToString($"X{this._offsetWidth}"), MonoFont,
      isContext ? Color.LightGray : Color.Gray,
      new(context.Bounds.X + 2, y, this._offsetWidth * cw, height), ContentAlignment.MiddleLeft);

    var hexStartX = context.Bounds.X + (this._offsetWidth + 2) * cw;
    var asciiStartX = hexStartX + BytesPerRow * 3 * cw + cw * 2;
    var count = Math.Min(BytesPerRow, data.Length - lineOffset);

    for (var i = 0; i < count; ++i) {
      var byteIndex = lineOffset + i;
      var b = data[byteIndex];
      var field = fieldMap[byteIndex];

      var hexX = hexStartX + i * 3 * cw;
      if (!isContext && field >= 0)
        g.FillRectangle(FieldColors[field % FieldColors.Length], new(hexX - 1, y, (int)(cw * 2.5), height));

      g.DrawText(b.ToString("X2"), MonoFont, isContext ? Color.Silver : Color.Black,
        new(hexX, y, cw * 2, height), ContentAlignment.MiddleLeft);

      var asciiX = asciiStartX + i * cw;
      if (!isContext && field >= 0) {
        var c = FieldColors[field % FieldColors.Length];
        g.FillRectangle(Color.FromArgb(50, c.R, c.G, c.B), new(asciiX, y, cw, height));
      }

      var ch = b is >= 0x20 and < 0x7F ? (char)b : '.';
      g.DrawText(ch.ToString(), MonoFont, isContext ? Color.Silver : Color.DarkGray,
        new(asciiX, y, cw, height), ContentAlignment.MiddleLeft);
    }
  }

  protected override void OnMouseMove(MouseEventArgs e) {
    base.OnMouseMove(e);
    if (this._data is not { } data || this._byteFieldMap is not { } fieldMap || this._charWidth == 0) return;

    var hexStartX = (this._offsetWidth + 2) * this._charWidth;
    var column = (e.X - hexStartX) / (3 * this._charWidth);
    var row = this.TopRow + e.Y / this.RowHeight;
    if (column < 0 || column >= BytesPerRow || row < 0) return;

    var byteIndex = this._displayStart + row * BytesPerRow + column;
    if (byteIndex < 0 || byteIndex >= data.Length || byteIndex == this._hoverByte) return;
    this._hoverByte = byteIndex;

    var field = fieldMap[byteIndex];
    var fieldName = field >= 0 && field < this._fieldNames.Length ? this._fieldNames[field] : "(no field)";
    this._toolTip.SetToolTip(this,
      $"Offset: 0x{byteIndex:X} ({byteIndex})\nByte: 0x{data[byteIndex]:X2} ({data[byteIndex]})\nField: {fieldName}");
  }

  protected override void OnMouseLeave(EventArgs e) {
    base.OnMouseLeave(e);
    this._hoverByte = -1;
    this._toolTip.Hide();
  }

  /// <summary>
  /// Assigns every covered byte the index of the leaf field that owns it. A group with no bytes of
  /// its own contributes nothing and recurses into its children instead.
  /// </summary>
  private static void BuildFieldMap(List<ParsedField> fields, int[] map, List<string> names) {
    foreach (var f in fields) {
      if (f.Children is { Count: > 0 } && f.Size == 0) {
        BuildFieldMap(f.Children, map, names);
        continue;
      }

      var index = names.Count;
      names.Add(f.Name);

      var end = Math.Min(f.Offset + f.Size, map.Length);
      for (var i = f.Offset; i < end; ++i) map[i] = index;

      if (f.Children is null) continue;
      foreach (var child in f.Children) {
        var childEnd = Math.Min(child.Offset + child.Size, map.Length);
        for (var i = child.Offset; i < childEnd; ++i) map[i] = index;
      }
    }
  }

  private static IEnumerable<ParsedField> FlattenFields(List<ParsedField> fields) {
    foreach (var f in fields) {
      yield return f;
      if (f.Children is null) continue;
      foreach (var c in FlattenFields(f.Children)) yield return c;
    }
  }
}
