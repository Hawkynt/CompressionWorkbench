#pragma warning disable CS1591
using System.Buffers.Binary;

namespace CompressionWorkbench.FileFormat.Ico;

/// <summary>
/// Writes Windows ICO bundles. Inputs are individual image files (PNG or BMP). PNG
/// payloads are stored verbatim (Vista+ supports embedded PNG in ICO). BMP payloads
/// are converted back to icon-style DIBs: BITMAPFILEHEADER stripped, biHeight
/// doubled, and a zero AND-mask appended so legacy parsers stay happy.
/// </summary>
public sealed class IcoWriter {

  /// <summary>Single image to embed.</summary>
  public sealed record Image(byte[] Data);

  public static byte[] BuildIco(IReadOnlyList<Image> images) => Build(images, isCursor: false);
  public static byte[] BuildCur(IReadOnlyList<Image> images) => Build(images, isCursor: true);

  private static byte[] Build(IReadOnlyList<Image> images, bool isCursor) {
    if (images.Count == 0) throw new ArgumentException("ICO: at least one image required", nameof(images));
    if (images.Count > ushort.MaxValue) throw new ArgumentException("ICO: too many images (>65535)", nameof(images));

    var encoded = new (byte[] Payload, int Width, int Height, int Bpp, bool IsPng)[images.Count];
    for (var i = 0; i < images.Count; i++)
      encoded[i] = Encode(images[i].Data);

    var dirSize = 6 + 16 * images.Count;
    var totalSize = dirSize + encoded.Sum(e => e.Payload.Length);
    var output = new byte[totalSize];

    // ICONDIR
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(0), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2), (ushort)(isCursor ? 2 : 1));
    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), (ushort)images.Count);

    // Directory entries + payloads.
    var payloadCursor = dirSize;
    for (var i = 0; i < encoded.Length; i++) {
      var off = 6 + 16 * i;
      var (payload, w, h, bpp, isPng) = encoded[i];
      output[off] = (byte)(w == 256 ? 0 : w);
      output[off + 1] = (byte)(h == 256 ? 0 : h);
      output[off + 2] = (byte)(bpp <= 8 ? (1 << bpp) & 0xFF : 0); // bColorCount
      output[off + 3] = 0;
      // For ICO: planes=1, bitCount=bpp. For CUR: planes/bitCount = hotspot 0,0 (we don't
      // know hotspots from raw images — caller can patch them after).
      BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(off + 4), (ushort)(isCursor ? 0 : 1));
      BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(off + 6), (ushort)(isCursor ? 0 : bpp));
      BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(off + 8), (uint)payload.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(off + 12), (uint)payloadCursor);

      payload.CopyTo(output.AsSpan(payloadCursor));
      payloadCursor += payload.Length;
    }

    return output;
  }

  /// <summary>
  /// Encodes a source image (PNG or BMP) into an ICO-ready payload. Returns the
  /// dimensions extracted from the source so the directory entry can be filled in.
  /// </summary>
  private static (byte[] Payload, int Width, int Height, int Bpp, bool IsPng) Encode(byte[] source) {
    if (IsPng(source)) {
      var (w, h, bpp) = ReadPngDimensions(source);
      return (source, w, h, bpp, true);
    }
    if (IsBmp(source)) {
      var icoDib = ConvertBmpToIconDib(source, out var w, out var h, out var bpp);
      return (icoDib, w, h, bpp, false);
    }
    throw new ArgumentException("ICO: input is neither a PNG nor a BMP file");
  }

  private static bool IsPng(ReadOnlySpan<byte> data) =>
    data.Length >= 8
    && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
    && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

  private static bool IsBmp(ReadOnlySpan<byte> data) =>
    data.Length >= 14 && data[0] == (byte)'B' && data[1] == (byte)'M';

  private static (int W, int H, int Bpp) ReadPngDimensions(ReadOnlySpan<byte> png) {
    // IHDR is the first chunk after the 8-byte signature.
    // Chunk: length(4 BE) + type(4) + data + crc(4). IHDR data: width(4 BE) + height(4 BE) + bitdepth(1) + colortype(1) + ...
    if (png.Length < 8 + 8 + 13) throw new InvalidDataException("PNG: truncated");
    if (png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R')
      throw new InvalidDataException("PNG: first chunk is not IHDR");
    var w = (int)BinaryPrimitives.ReadUInt32BigEndian(png[16..]);
    var h = (int)BinaryPrimitives.ReadUInt32BigEndian(png[20..]);
    var depth = png[24];
    var colorType = png[25];
    // Channel count by colour type: 0=Y(1), 2=RGB(3), 3=Indexed(1), 4=YA(2), 6=RGBA(4).
    var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 4 };
    var bpp = depth * channels;
    return (w, h, bpp);
  }

  /// <summary>
  /// Strips the BITMAPFILEHEADER, copies the BITMAPINFOHEADER + palette + pixel data,
  /// patches biHeight to be 2× the real height, and appends a zero-filled AND-mask
  /// row-aligned to 4 bytes.
  /// </summary>
  private static byte[] ConvertBmpToIconDib(ReadOnlySpan<byte> bmp, out int width, out int height, out int bpp) {
    if (bmp.Length < 14 + 12) throw new InvalidDataException("BMP: truncated");
    var pixelOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bmp.Slice(10, 4));
    var dibStart = 14;
    var dibHeaderSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bmp.Slice(dibStart, 4));
    if (dibHeaderSize < 12) throw new InvalidDataException("BMP: invalid DIB header size");

    int rawW, rawH, rawBpp;
    if (dibHeaderSize == 12) {
      // BITMAPCOREHEADER
      rawW = BinaryPrimitives.ReadInt16LittleEndian(bmp.Slice(dibStart + 4, 2));
      rawH = BinaryPrimitives.ReadInt16LittleEndian(bmp.Slice(dibStart + 6, 2));
      rawBpp = BinaryPrimitives.ReadUInt16LittleEndian(bmp.Slice(dibStart + 10, 2));
    } else {
      rawW = BinaryPrimitives.ReadInt32LittleEndian(bmp.Slice(dibStart + 4, 4));
      rawH = BinaryPrimitives.ReadInt32LittleEndian(bmp.Slice(dibStart + 8, 4));
      rawBpp = BinaryPrimitives.ReadUInt16LittleEndian(bmp.Slice(dibStart + 14, 2));
    }
    if (rawH < 0) throw new NotSupportedException("BMP: top-down bitmaps not supported for ICO encoding");

    width = rawW;
    height = rawH;
    bpp = rawBpp;

    var pixelLen = bmp.Length - pixelOffset;
    var dibTailLen = pixelOffset - 14; // header + palette
    var maskRowBytes = ((rawW + 31) / 32) * 4;
    var maskLen = maskRowBytes * rawH;

    var output = new byte[dibTailLen + pixelLen + maskLen];
    bmp.Slice(14, dibTailLen).CopyTo(output);
    bmp.Slice(pixelOffset, pixelLen).CopyTo(output.AsSpan(dibTailLen));
    // AND-mask is left zero — fully opaque icon.

    // Patch biHeight to 2× real height (icon convention).
    if (dibHeaderSize == 12) {
      BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(6), (short)(rawH * 2));
    } else {
      BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), rawH * 2);
    }

    // Patch biSizeImage if present (BITMAPINFOHEADER offset 20 = file offset 34, our offset 20).
    if (dibHeaderSize >= 24) {
      BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(20), (uint)(pixelLen + maskLen));
    }

    return output;
  }
}
