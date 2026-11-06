#pragma warning disable CS1591
using ImagesIco = FileFormat.Ico;

namespace CompressionWorkbench.FileFormat.Ico;

/// <summary>
/// Reader for Windows ICO/CUR icon-bundle files. Each embedded image is exposed as
/// a standalone PNG (when the entry is already PNG-encoded) or BMP (when the entry
/// is a DIB — BITMAPFILEHEADER is reconstructed and the AND-mask half of the height
/// is stripped so the BMP renders correctly in standard viewers).
/// </summary>
/// <remarks>
/// The directory walk and the DIB-to-BMP header rewrite are
/// <see cref="ImagesIco.IcoReader.ReadBundle(byte[])"/> and
/// <see cref="ImagesIco.IcoPayload.IconDibToBmpFile"/> in Hawkynt.FileFormats.Images. What stays
/// here is the archive view: the name an entry is listed under, and the decision to hand a caller
/// a file a viewer will open rather than the bare payload.
/// </remarks>
public sealed class IcoReader {

  /// <summary>The shortest information header an entry can carry and still describe a picture.</summary>
  private const int _MinimumDibHeaderSize = 40;

  /// <summary>
  /// Represents an icon entry.
  /// </summary>
  public sealed record IconEntry(
    int Index,
    int Width,
    int Height,
    int BitsPerPixel,
    int HotspotX,        // CUR only; 0 for ICO
    int HotspotY,        // CUR only; 0 for ICO
    bool IsPng,
    string Name,         // computed display name (e.g. "icon_00_32x32x32.png")
    byte[] Data          // ready-to-write PNG bytes or fully-formed BMP bytes
  );

  /// <summary>
  /// Represents a bundle.
  /// </summary>
  public sealed record Bundle(
    bool IsCursor,
    IReadOnlyList<IconEntry> Entries
  );

  /// <summary>
  /// Reads the value from the supplied input.
  /// </summary>
  /// <exception cref="InvalidDataException">
  /// The data is not a well-formed ICO or CUR, or one of its bitmap-bodied entries is too short to
  /// describe a picture.
  /// </exception>
  public static Bundle Read(ReadOnlySpan<byte> data) {
    var bundle = ImagesIco.IcoReader.ReadBundle(data);
    var isCursor = bundle.Kind == ImagesIco.IcoFileType.Cursor;

    var entries = new List<IconEntry>(bundle.Entries.Count);
    foreach (var entry in bundle.Entries) {
      var isPng = entry.Format == ImagesIco.IcoImageFormat.Png;

      byte[] payload;
      if (isPng) {
        payload = entry.Data;
      } else {
        // A listing has nothing to show for a bitmap too short to hold an information header, and a
        // BMP built from one would be a file a viewer opens and then reports as broken. Refusing is
        // the more useful answer here, and the caller above turns it into a raw-bytes entry.
        if (entry.Data.Length < _MinimumDibHeaderSize)
          throw new InvalidDataException($"ICO: entry {entry.Index} DIB header truncated");

        payload = ImagesIco.IcoPayload.IconDibToBmpFile(entry.Data, entry.Width, entry.Height, entry.BitsPerPixel);
      }

      entries.Add(new IconEntry(
        Index: entry.Index,
        Width: entry.Width,
        Height: entry.Height,
        // A cursor's directory carries a hotspot where an icon's carries the depth, so for a
        // cursor the depth is only ever what the payload says — which is what the bundle reports.
        BitsPerPixel: entry.BitsPerPixel,
        HotspotX: entry.HotspotX,
        HotspotY: entry.HotspotY,
        IsPng: isPng,
        Name: _EntryName(entry.Index, entry.Width, entry.Height, entry.BitsPerPixel, entry.HotspotX, entry.HotspotY, isCursor, isPng),
        Data: payload));
    }

    return new Bundle(IsCursor: isCursor, Entries: entries);
  }

  /// <summary>
  /// The name an entry is listed and extracted under.
  /// </summary>
  /// <remarks>
  /// Purely this project's own: nothing in the file carries a name for an entry. A cursor is named
  /// by where it points, which is what distinguishes two otherwise identical cursors, and an icon
  /// by its depth, which is what distinguishes two entries of the same size.
  /// </remarks>
  private static string _EntryName(
    int index,
    int width,
    int height,
    int bitsPerPixel,
    int hotspotX,
    int hotspotY,
    bool isCursor,
    bool isPng
  ) {
    var extension = isPng ? "png" : "bmp";
    return isCursor
      ? $"cursor_{index:D2}_{width}x{height}_h{hotspotX}_{hotspotY}.{extension}"
      : $"icon_{index:D2}_{width}x{height}x{bitsPerPixel}.{extension}";
  }
}
