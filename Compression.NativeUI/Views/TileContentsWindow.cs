using System.Drawing;
using Compression.Registry;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Views;

/// <summary>
/// Drill-down for a single tile of the block map: every block whose byte range intersects the
/// clicked tile, one line each.
/// </summary>
internal sealed class TileContentsWindow : Form {
  private static readonly Font MonoFont = new("Cascadia Mono", 9f, FontStyle.Regular);

  private readonly Label _header = new();
  private readonly Label _subHeader = new() { ForeColor = Color.DimGray };
  private readonly ListBox _contents = new() { Font = MonoFont };
  private readonly Button _close = new() { Text = "Close", DialogResult = DialogResult.OK };

  public TileContentsWindow() {
    this.Text = "Tile contents";
    this.ClientSize = new(520, 420);
    this.MinimumSize = new(360, 240);
    this.StartPosition = FormStartPosition.CenterParent;

    this._header.Font = new(DefaultTheme.Instance.DefaultFont.Family, 10.5f, FontStyle.Bold);
    this._close.Click += (_, _) => this.Close();
    this.AcceptButton = this._close;
    this.CancelButton = this._close;

    this.Controls.AddRange(this._header, this._subHeader, this._contents, this._close);
    this._contents.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
    this._close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;

    this.Resize += (_, _) => this.LayoutChildren();
    this.LayoutChildren();
  }

  private void LayoutChildren() {
    const int Gutter = 10;
    var width = this.ClientSize.Width - 2 * Gutter;

    this._header.Bounds = new(Gutter, Gutter, width, 20);
    this._subHeader.Bounds = new(Gutter, Gutter + 22, width, 18);
    this._contents.Bounds = new(Gutter, Gutter + 46, width, Math.Max(40, this.ClientSize.Height - Gutter - 46 - 44));
    this._close.Bounds = new(this.ClientSize.Width - Gutter - 80, this.ClientSize.Height - Gutter - 28, 80, 28);
  }

  /// <summary>Re-targets the window at another tile.</summary>
  public void SetContents(long startOffset, long endOffset, IReadOnlyList<DefragBlockInfo> contents) {
    var span = Math.Max(0L, endOffset - startOffset);
    this._header.Text = $"Tile @ offset 0x{startOffset:X} ({FormatBytes(span)} span)";
    this._subHeader.Text = contents.Count == 1 ? "1 entry" : $"{contents.Count:N0} entries";

    this._contents.Items.Clear();
    if (contents.Count == 0) {
      this._contents.Items.Add("(no blocks intersect this tile)");
      return;
    }

    foreach (var b in contents)
      this._contents.Items.Add(FormatLine(b, startOffset, endOffset));
  }

  /// <summary>
  /// Reports the intersected portion's length, so it is clear how much of each entry actually lives
  /// inside the clicked tile: one very large file can cover it entirely, while many tiny files each
  /// contribute only a few bytes.
  /// </summary>
  private static string FormatLine(DefragBlockInfo b, long tileStart, long tileEnd) {
    var clipStart = Math.Max(b.Offset, tileStart);
    var clipEnd = Math.Min(b.Offset + b.Length, tileEnd);
    var clipLength = Math.Max(0L, clipEnd - clipStart);

    var sizeDisplay = clipLength == b.Length
      ? FormatBytes(clipLength)
      : $"{FormatBytes(clipLength)} of {FormatBytes(b.Length)}";

    return b.Kind switch {
      DefragBlockKind.Used => $"FILE: {b.FileName ?? "(unnamed)"}  ({sizeDisplay})",
      DefragBlockKind.MetadataReserved => $"META: {b.FileName ?? "reserved region"}  ({sizeDisplay})",
      DefragBlockKind.Free => $"FREE: {sizeDisplay}",
      DefragBlockKind.Bad => $"BAD:  {sizeDisplay}",
      DefragBlockKind.InProgress => $"BUSY: {b.FileName ?? "(in progress)"}  ({sizeDisplay})",
      _ => $"{b.Kind}: {sizeDisplay}",
    };
  }

  private static string FormatBytes(long bytes) => bytes switch {
    < 1024 => $"{bytes:N0} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
  };
}
