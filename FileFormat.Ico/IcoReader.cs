#pragma warning disable CS1591
using System.Buffers.Binary;

namespace CompressionWorkbench.FileFormat.Ico;

/// <summary>
/// Reader for Windows ICO/CUR icon-bundle files. Each embedded image is exposed as
/// a standalone PNG (when the entry is already PNG-encoded) or BMP (when the entry
/// is a DIB — BITMAPFILEHEADER is reconstructed and the AND-mask half of the height
/// is stripped so the BMP renders correctly in standard viewers).
/// </summary>
public sealed class IcoReader {

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

  public sealed record Bundle(
    bool IsCursor,
    IReadOnlyList<IconEntry> Entries
  );

  public static Bundle Read(ReadOnlySpan<byte> data) {
    if (data.Length < 6) throw new InvalidDataException("ICO: truncated header");

    var reserved = BinaryPrimitives.ReadUInt16LittleEndian(data);
    var type = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
    var count = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
    if (reserved != 0) throw new InvalidDataException($"ICO: reserved field is 0x{reserved:X4}, expected 0");
    if (type != 1 && type != 2) throw new InvalidDataException($"ICO: unknown type {type} (expected 1=ICO or 2=CUR)");

    var isCursor = type == 2;
    var dirSize = 6 + 16 * count;
    if (data.Length < dirSize) throw new InvalidDataException("ICO: truncated directory");

    var entries = new List<IconEntry>(count);
    for (var i = 0; i < count; i++) {
      var off = 6 + 16 * i;
      var widthByte = data[off];
      var heightByte = data[off + 1];
      var width = widthByte == 0 ? 256 : widthByte;
      var height = heightByte == 0 ? 256 : heightByte;
      // bColorCount at off+2, bReserved at off+3 — not load-bearing for extraction.
      var planes = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 4)..]);
      var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 6)..]);
      var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 8)..]);
      var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 12)..]);

      if (dataOffset + dataSize > (uint)data.Length)
        throw new InvalidDataException($"ICO: entry {i} extends past end of file");

      var raw = data.Slice((int)dataOffset, (int)dataSize);
      var isPng = raw.Length >= 8
        && raw[0] == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E && raw[3] == 0x47
        && raw[4] == 0x0D && raw[5] == 0x0A && raw[6] == 0x1A && raw[7] == 0x0A;

      int hotX = 0, hotY = 0;
      var bpp = bitCount;
      if (isCursor) {
        hotX = planes;
        hotY = bitCount;
        bpp = 0; // can't infer from header; will read DIB below if applicable
      }

      byte[] payload;
      if (isPng) {
        payload = raw.ToArray();
      } else {
        // DIB. Extract bpp from BITMAPINFOHEADER.biBitCount and rebuild a real BMP.
        if (raw.Length < 40) throw new InvalidDataException($"ICO: entry {i} DIB header truncated");
        var biBitCount = BinaryPrimitives.ReadUInt16LittleEndian(raw[14..]);
        if (bpp == 0) bpp = biBitCount;
        payload = BuildBmpFromIconDib(raw, width, height, biBitCount);
      }

      var ext = isPng ? "png" : "bmp";
      var name = isCursor
        ? $"cursor_{i:D2}_{width}x{height}_h{hotX}_{hotY}.{ext}"
        : $"icon_{i:D2}_{width}x{height}x{bpp}.{ext}";

      entries.Add(new IconEntry(
        Index: i, Width: width, Height: height, BitsPerPixel: bpp,
        HotspotX: hotX, HotspotY: hotY, IsPng: isPng, Name: name, Data: payload));
    }

    return new Bundle(IsCursor: isCursor, Entries: entries);
  }

  /// <summary>
  /// Wraps an ICO-embedded DIB in a real BMP. The icon DIB encodes the XOR colour
  /// bitmap stacked on top of an AND-mask of equal width and half the total height —
  /// we strip the AND-mask and patch BITMAPINFOHEADER.biHeight accordingly.
  /// </summary>
  private static byte[] BuildBmpFromIconDib(ReadOnlySpan<byte> dib, int realWidth, int realHeight, ushort bpp) {
    // Read DIB header size to locate palette/pixel-data boundary.
    if (dib.Length < 4) throw new InvalidDataException("ICO: DIB too small");
    var dibHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
    if (dibHeaderSize < 12 || dibHeaderSize > (uint)dib.Length)
      throw new InvalidDataException($"ICO: invalid DIB header size {dibHeaderSize}");

    // Palette size for indexed formats (1/4/8-bit). 0 for >=16-bit.
    var paletteEntries = bpp <= 8 ? (1 << bpp) : 0;
    // BITMAPINFOHEADER stores biClrUsed at offset 32 (when header is the 40-byte variant).
    if (bpp <= 8 && dibHeaderSize >= 36) {
      var clrUsed = (int)BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
      if (clrUsed > 0) paletteEntries = clrUsed;
    }
    var paletteBytes = paletteEntries * 4;
    var pixelDataStart = (int)dibHeaderSize + paletteBytes;

    // Compute the XOR (colour) stride and total colour-byte count.
    var rowBytes = ((realWidth * bpp + 31) / 32) * 4;
    var xorBytes = rowBytes * realHeight;
    if (pixelDataStart + xorBytes > dib.Length) {
      // Defensive: just emit whatever DIB bytes we have prefixed with a BMP header
      // pointing at pixelDataStart and let the viewer cope.
      xorBytes = Math.Max(0, dib.Length - pixelDataStart);
    }

    var bmpDibLen = pixelDataStart + xorBytes; // strip AND-mask
    const int FileHeaderLen = 14;
    var fileLen = FileHeaderLen + bmpDibLen;

    var bmp = new byte[fileLen];
    // BITMAPFILEHEADER
    bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
    BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)fileLen);
    // bfReserved1/2 = 0 (already)
    BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), (uint)(FileHeaderLen + pixelDataStart));

    // Copy DIB header + palette + XOR pixel data.
    dib[..bmpDibLen].CopyTo(bmp.AsSpan(FileHeaderLen));

    // Patch biHeight to the real (non-doubled) height — DIB stores it doubled.
    if (dibHeaderSize >= 12) {
      // BITMAPCOREHEADER stores height as i16 at +6; BITMAPINFOHEADER (and v4/v5) at +8 as i32.
      if (dibHeaderSize == 12) {
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(FileHeaderLen + 6), (short)realHeight);
      } else {
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(FileHeaderLen + 8), realHeight);
      }
    }

    return bmp;
  }
}
