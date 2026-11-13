namespace FileFormat.Uharc;

/// <summary>
/// Undoes UHARC's fax-page transform (docs/UHARC-ON-DISK.md, "Fax filter"): a file of exactly
/// 513216 bytes is taken as a 1728 × 2376 one-bit page and stored as 297 bands of eight rows, each
/// band holding one byte per pixel column with the band's top row in bit 7.
/// </summary>
internal static class UharcFaxFilter {

  public const int PageSize = Width / 8 * Height;
  private const int Width = 1728;
  private const int Height = 2376;
  private const int RowBytes = Width / 8;

  /// <summary>Restores the row-major page in place; other sizes are left alone.</summary>
  public static void Restore(Span<byte> data) {
    if (data.Length != PageSize)
      return;

    var page = new byte[PageSize];
    for (var row = 0; row < Height; ++row) {
      var band = (row >> 3) * Width;
      var bit = 7 - (row & 7);
      for (var column = 0; column < RowBytes; ++column) {
        var value = 0;
        var source = band + column * 8;
        for (var i = 0; i < 8; ++i)
          value |= ((data[source + i] >> bit) & 1) << (7 - i);
        page[row * RowBytes + column] = (byte)value;
      }
    }

    page.CopyTo(data);
  }
}
