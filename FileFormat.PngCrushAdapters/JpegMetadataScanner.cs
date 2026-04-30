#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.PngCrushAdapters;

/// <summary>
/// Cheap JPEG header walker. Reads only the marker stream until the SOF
/// (Start-Of-Frame) is encountered, then returns. Does NOT do any DCT, IDCT,
/// Huffman or colour-space conversion work. Typical JPEG: SOF lands within
/// the first ~few hundred bytes after APPn marker(s); we cap the scan at
/// <see cref="ScanLimit"/> bytes regardless to defend against pathological
/// inputs (e.g. a giant EXIF blob preceding the SOF).
/// </summary>
/// <remarks>
/// JPEG marker structure:
/// <code>
///   0xFF 0xXX                           — marker byte (XX != 0x00, != 0xFF)
///   length (uint16 BE) including itself — only for variable-length markers
///   payload (length-2 bytes)
/// </code>
/// Stand-alone markers (SOI 0xD8, EOI 0xD9, RSTn 0xD0..0xD7, TEM 0x01) carry
/// no length field. The SOF group (0xC0..0xCF excluding 0xC4 DHT, 0xC8 JPG,
/// 0xCC DAC) carries the frame metadata we want.
/// </remarks>
public static class JpegMetadataScanner {

  /// <summary>Hard cap on bytes scanned looking for the SOF. ~16 KB is a generous bound.</summary>
  public const int ScanLimit = 65536;

  /// <summary>
  /// Returns <see cref="FrameMetadata"/> describing the JPEG frame the stream
  /// starts at. The stream position on entry must be at or before the SOI.
  /// On any parsing trouble returns a fallback <c>(0, 0, 24, false)</c>.
  /// JPEG never carries alpha; <see cref="FrameMetadata.HasAlpha"/> is always false.
  /// </summary>
  public static FrameMetadata Scan(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    Span<byte> header = stackalloc byte[ScanLimit];
    var read = ReadUpTo(stream, header);
    return ScanBytes(header[..read]);
  }

  /// <summary>Same as <see cref="Scan"/> but takes an in-memory buffer.</summary>
  public static FrameMetadata ScanBytes(ReadOnlySpan<byte> data) {
    // SOI must be 0xFFD8. Anything else: bail with fallback so List() keeps producing entries.
    if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
      return new FrameMetadata(0, 0, 24, false);

    var i = 2;
    while (i + 3 < data.Length) {
      // Walk to the next 0xFF / non-stuff / non-fill marker.
      if (data[i] != 0xFF) { i++; continue; }
      // Skip fill bytes (0xFF 0xFF...).
      while (i + 1 < data.Length && data[i + 1] == 0xFF) i++;
      if (i + 1 >= data.Length) break;
      var marker = data[i + 1];
      i += 2;
      // 0xFF00 stuffing (not actually a marker) — already handled by top-of-loop check.
      if (marker == 0x00) continue;

      // Stand-alone markers carry no length.
      if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 ||
          (marker >= 0xD0 && marker <= 0xD7)) {
        if (marker == 0xD9) break; // EOI — no frame found.
        continue;
      }

      if (i + 1 >= data.Length) break;
      var segLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i, 2));
      if (segLen < 2) return new FrameMetadata(0, 0, 24, false);

      // SOF range: 0xC0..0xCF excluding 0xC4 (DHT), 0xC8 (JPG reserved), 0xCC (DAC).
      var isSof = marker is >= 0xC0 and <= 0xCF
                  && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

      if (isSof) {
        // SOF payload: precision (1) + height (2 BE) + width (2 BE) + nf (1) + ...
        // The 'segLen' includes the two length bytes itself, so payload starts at i+2.
        var payloadStart = i + 2;
        if (payloadStart + 6 > data.Length) return new FrameMetadata(0, 0, 24, false);
        var precision = data[payloadStart];
        var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(payloadStart + 1, 2));
        var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(payloadStart + 3, 2));
        var nf = data[payloadStart + 5];
        // Per-pixel bits = precision * components. JFIF baseline = 8 * 3 = 24.
        var bpp = precision * nf;
        if (bpp <= 0) bpp = 24;
        return new FrameMetadata(width, height, bpp, false);
      }

      // Skip variable-length segment payload.
      i += segLen;
    }
    return new FrameMetadata(0, 0, 24, false);
  }

  private static int ReadUpTo(Stream stream, Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var n = stream.Read(buffer[total..]);
      if (n <= 0) break;
      total += n;
    }
    return total;
  }
}
