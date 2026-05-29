#pragma warning disable CS1591
using System.Windows;
using Compression.Registry;

namespace Compression.UI.Views;

/// <summary>
/// Drill-down popup for a single tile in <see cref="Controls.BlockMapControl"/>.
/// Lists every <see cref="DefragBlockInfo"/> from the source map whose byte
/// range intersects the clicked tile's range, formatted as one line per
/// entry: <c>FILE: name (size)</c>, <c>META: label</c>, <c>FREE: size</c>,
/// <c>BAD: size</c>, or <c>BUSY: name (size)</c>.
/// </summary>
public partial class TileContentsWindow : Window {

  public TileContentsWindow() {
    InitializeComponent();
  }

  /// <summary>
  /// Populates the window with the byte range and entry list. Call this
  /// after construction (and before showing) — keeps the .xaml.cs free of
  /// constructor-arg ceremony so the designer is happy.
  /// </summary>
  public void SetContents(long startOffset, long endOffset, IReadOnlyList<DefragBlockInfo> contents) {
    var span = Math.Max(0L, endOffset - startOffset);
    HeaderLbl.Text = $"Tile @ offset 0x{startOffset:X} ({FormatBytes(span)} span)";
    SubHeaderLbl.Text = contents.Count == 1 ? "1 entry" : $"{contents.Count:N0} entries";

    var lines = new List<string>(contents.Count);
    if (contents.Count == 0) {
      lines.Add("(no blocks intersect this tile)");
    } else {
      foreach (var b in contents) {
        lines.Add(FormatLine(b, startOffset, endOffset));
      }
    }
    ContentsList.ItemsSource = lines;
  }

  private static string FormatLine(DefragBlockInfo b, long tileStart, long tileEnd) {
    // Show the *intersected* portion's byte length so users can see how
    // much of each entry actually lives inside the clicked tile (a single
    // very large file can fully cover the tile; many tiny files can each
    // contribute a few bytes).
    var clipStart = Math.Max(b.Offset, tileStart);
    var clipEnd = Math.Min(b.Offset + b.Length, tileEnd);
    var clipLen = Math.Max(0L, clipEnd - clipStart);
    var sizeStr = FormatBytes(clipLen);
    var fullSize = FormatBytes(b.Length);
    var sizeDisplay = clipLen == b.Length ? sizeStr : $"{sizeStr} of {fullSize}";

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

  private void OnClose(object sender, RoutedEventArgs e) => Close();
}
