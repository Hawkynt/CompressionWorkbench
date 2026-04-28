using System.Text;

namespace FileFormat.JpegArchive;

/// <summary>
/// Byte-level JPEG segment manipulation. Walks an APP-marker chain to
/// find-and-replace (or insert) the XMP APP1 segment so XMP updates don't
/// re-encode the image. JPEG structure reference:
///   SOI (FF D8), then a sequence of segments of form <c>FF xx [len_hi] [len_lo] [payload]</c>
///   where <c>len</c> is 2 bytes big-endian and INCLUDES itself (but not the marker),
///   until SOS (FF DA) starts the compressed scan.
/// APP1 XMP is identified by the 29-byte ASCII prefix
/// <c>http://ns.adobe.com/xap/1.0/\0</c>.
/// </summary>
public static class JpegSegmentSurgery {
  private static readonly byte[] SoiMarker = { 0xFF, 0xD8 };
  private static readonly byte[] XmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
  private static readonly byte[] ExifHeader = Encoding.ASCII.GetBytes("Exif\0\0");
  private static readonly byte[] PhotoshopHeader = Encoding.ASCII.GetBytes("Photoshop 3.0\0");
  private const ushort PhotoshopIrbIptcId = 0x0404;
  /// <summary>
  /// Largest payload (after the 2-byte length field) a single JPEG APP
  /// segment can hold. Callers building APP1 EXIF / XMP payloads use this
  /// as the hard cap before falling back to splitting (XMP) or shrinking
  /// (thumbnails).
  /// </summary>
  public const int MaxApp1PayloadBytes = 65533;
  private const int MaxSegmentPayload = MaxApp1PayloadBytes;

  /// <summary>
  /// Produces a new JPEG byte array identical to <paramref name="input"/>
  /// except the XMP APP1 segment is set to <paramref name="xmpBytes"/>.
  /// Existing XMP APP1 segments are replaced; if none was present, a new
  /// one is inserted right after SOI (or any JFIF/APP0 segment that
  /// immediately follows SOI).
  /// </summary>
  public static byte[] ReplaceXmpSegment(ReadOnlySpan<byte> input, byte[] xmpBytes) {
    ArgumentNullException.ThrowIfNull(xmpBytes);
    if (xmpBytes.Length > MaxSegmentPayload - XmpHeader.Length)
      throw new InvalidOperationException(
        $"XMP payload ({xmpBytes.Length} bytes) exceeds the single-APP1 JPEG limit of {MaxSegmentPayload - XmpHeader.Length}. Extended XMP (multi-segment chain) is not implemented yet.");

    if (input.Length < 2 || input[0] != SoiMarker[0] || input[1] != SoiMarker[1])
      throw new InvalidDataException("File does not start with JPEG SOI marker.");

    // Walk segments from byte 2 onwards, recording boundaries so we can
    // re-emit unchanged bytes plus our replacement XMP segment.
    var segments = ParseSegments(input);

    using var output = new MemoryStream();
    output.WriteByte(0xFF);
    output.WriteByte(0xD8);

    var wroteXmp = false;

    for (var i = 0; i < segments.Count; i++) {
      var seg = segments[i];

      if (seg.IsXmp) {
        // Replace this segment with our XMP — emit once, skip further XMP segments.
        if (!wroteXmp) {
          WriteXmpSegment(output, xmpBytes);
          wroteXmp = true;
        }
        continue;
      }

      // Insert our XMP right before the first non-SOI, non-APPn segment —
      // this keeps Adobe's "XMP lives with the APPn markers" convention, and
      // handles the degenerate SOI→EOI case by inserting before EOI.
      if (!wroteXmp && !seg.IsAppMarker && seg.Marker != 0xD8) {
        WriteXmpSegment(output, xmpBytes);
        wroteXmp = true;
      }

      output.Write(input.Slice(seg.Start, seg.Length));

      // After SOS, the rest of the file (compressed scan + EOI) is copied wholesale.
      if (seg.IsSos) {
        var tail = input.Slice(seg.Start + seg.Length);
        output.Write(tail);
        break;
      }
    }

    return output.ToArray();
  }

  /// <summary>
  /// Produces a new JPEG byte array with the APP1 EXIF segment replaced by
  /// <paramref name="exifPayload"/> (which must already include the
  /// <c>Exif\0\0</c> 6-byte prefix followed by the TIFF-area bytes).
  /// Existing EXIF segments are replaced; if none was present, the new one
  /// is inserted right after SOI (before any non-APPn segment).
  /// </summary>
  public static byte[] ReplaceExifSegment(ReadOnlySpan<byte> input, byte[] exifPayload) {
    ArgumentNullException.ThrowIfNull(exifPayload);
    if (exifPayload.Length > MaxSegmentPayload)
      throw new InvalidOperationException(
        $"EXIF payload ({exifPayload.Length} bytes) exceeds the single-APP1 JPEG limit of {MaxSegmentPayload}.");
    if (exifPayload.Length < ExifHeader.Length ||
        !ExifHeader.AsSpan().SequenceEqual(exifPayload.AsSpan(0, ExifHeader.Length)))
      throw new ArgumentException("EXIF payload must begin with the 'Exif\\0\\0' 6-byte header.");

    if (input.Length < 2 || input[0] != SoiMarker[0] || input[1] != SoiMarker[1])
      throw new InvalidDataException("File does not start with JPEG SOI marker.");

    var segments = ParseSegments(input);

    using var output = new MemoryStream();
    output.WriteByte(0xFF);
    output.WriteByte(0xD8);

    var wroteExif = false;
    for (var i = 0; i < segments.Count; i++) {
      var seg = segments[i];

      if (seg.IsExif) {
        if (!wroteExif) {
          WriteApp1Segment(output, exifPayload);
          wroteExif = true;
        }
        continue;
      }

      if (!wroteExif && !seg.IsAppMarker && seg.Marker != 0xD8) {
        WriteApp1Segment(output, exifPayload);
        wroteExif = true;
      }

      output.Write(input.Slice(seg.Start, seg.Length));

      if (seg.IsSos) {
        var tail = input.Slice(seg.Start + seg.Length);
        output.Write(tail);
        break;
      }
    }

    return output.ToArray();
  }

  /// <summary>
  /// Returns the APP1 EXIF payload (INCLUDING the 6-byte <c>Exif\0\0</c>
  /// prefix) for callers that want to parse the TIFF area themselves.
  /// </summary>
  public static byte[]? TryReadExifSegment(ReadOnlySpan<byte> input) {
    if (input.Length < 2 || input[0] != SoiMarker[0] || input[1] != SoiMarker[1])
      return null;

    foreach (var seg in ParseSegments(input)) {
      if (seg.IsExif) {
        var headerSize = 2 + 2;  // marker + length
        var payloadLength = seg.Length - headerSize;
        if (payloadLength <= 0)
          return null;
        return input.Slice(seg.Start + headerSize, payloadLength).ToArray();
      }
      if (seg.IsSos)
        break;
    }

    return null;
  }

  private static void WriteApp1Segment(Stream output, byte[] payload) {
    var segmentLength = 2 + payload.Length;
    output.WriteByte(0xFF);
    output.WriteByte(0xE1);
    output.WriteByte((byte)(segmentLength >> 8));
    output.WriteByte((byte)(segmentLength & 0xFF));
    output.Write(payload, 0, payload.Length);
  }

  /// <summary>
  /// Reads the XMP APP1 payload out of a JPEG, if present. Returns null
  /// when no XMP segment exists.
  /// </summary>
  public static byte[]? TryReadXmpSegment(ReadOnlySpan<byte> input) {
    if (input.Length < 2 || input[0] != SoiMarker[0] || input[1] != SoiMarker[1])
      return null;

    foreach (var seg in ParseSegments(input)) {
      if (seg.IsXmp) {
        // Skip the 2-byte marker, 2-byte length, 29-byte XMP identifier.
        var headerSize = 2 + 2 + XmpHeader.Length;
        var payloadLength = seg.Length - headerSize;
        if (payloadLength <= 0)
          return null;
        return input.Slice(seg.Start + headerSize, payloadLength).ToArray();
      }

      if (seg.IsSos)
        break;
    }

    return null;
  }

  private static void WriteXmpSegment(Stream output, byte[] xmpBytes) {
    var segmentLength = 2 + XmpHeader.Length + xmpBytes.Length;  // 2 length bytes + header + payload
    output.WriteByte(0xFF);
    output.WriteByte(0xE1);  // APP1
    output.WriteByte((byte)(segmentLength >> 8));
    output.WriteByte((byte)(segmentLength & 0xFF));
    output.Write(XmpHeader, 0, XmpHeader.Length);
    output.Write(xmpBytes, 0, xmpBytes.Length);
  }

  /// <summary>
  /// Replaces (or inserts) the IPTC-IIM record inside a Photoshop APP13 IRB.
  /// Preserves any other IRBs in the existing APP13 (e.g. resolution info,
  /// caption digest) so we don't clobber Photoshop-specific settings the
  /// user might have set elsewhere.
  /// </summary>
  public static byte[] ReplaceIptcSegment(ReadOnlySpan<byte> input, byte[] iptcBytes) {
    ArgumentNullException.ThrowIfNull(iptcBytes);
    if (input.Length < 2 || input[0] != SoiMarker[0] || input[1] != SoiMarker[1])
      throw new InvalidDataException("File does not start with JPEG SOI marker.");

    var segments = ParseSegments(input);
    using var output = new MemoryStream();
    output.WriteByte(0xFF);
    output.WriteByte(0xD8);

    // Preserve all existing 8BIM IRBs from any existing APP13 except the
    // IPTC one — we rebuild that. We accumulate them into a single rebuilt
    // APP13 payload so the file keeps one APP13 marker in the same spot.
    byte[]? rebuiltApp13 = null;
    var wroteApp13 = false;

    foreach (var seg in segments) {
      if (seg.IsIptc) {
        if (!wroteApp13) {
          rebuiltApp13 ??= BuildApp13Payload(iptcBytes, existingApp13: input.Slice(seg.Start + 4, seg.Length - 4));
          WriteApp13Segment(output, rebuiltApp13);
          wroteApp13 = true;
        }
        continue;
      }

      if (!wroteApp13 && !seg.IsAppMarker && seg.Marker != 0xD8) {
        rebuiltApp13 ??= BuildApp13Payload(iptcBytes, existingApp13: null);
        WriteApp13Segment(output, rebuiltApp13);
        wroteApp13 = true;
      }

      output.Write(input.Slice(seg.Start, seg.Length));

      if (seg.IsSos) {
        var tail = input.Slice(seg.Start + seg.Length);
        output.Write(tail);
        break;
      }
    }

    return output.ToArray();
  }

  /// <summary>
  /// Returns the raw IPTC payload (the bytes *inside* IRB 0x0404, without
  /// the 8BIM wrapper) from the APP13 segment. Returns null when there is
  /// no APP13 or the IPTC IRB isn't present.
  /// </summary>
  public static byte[]? TryReadIptcSegment(ReadOnlySpan<byte> input) {
    if (input.Length < 2 || input[0] != SoiMarker[0] || input[1] != SoiMarker[1])
      return null;

    foreach (var seg in ParseSegments(input)) {
      if (!seg.IsIptc) {
        if (seg.IsSos)
          break;
        continue;
      }

      // seg.Start + 4 skips marker + length; payload starts there.
      var payloadStart = seg.Start + 4;
      var payloadLen = seg.Length - 4;
      var payload = input.Slice(payloadStart, payloadLen);
      if (!payload.StartsWith(PhotoshopHeader))
        return null;
      var irbs = payload.Slice(PhotoshopHeader.Length);
      return ExtractIptcIrb(irbs);
    }
    return null;
  }

  private static byte[]? ExtractIptcIrb(ReadOnlySpan<byte> irbs) {
    var i = 0;
    while (i + 12 <= irbs.Length) {
      // 8BIM signature
      if (irbs[i] != 0x38 || irbs[i + 1] != 0x42 || irbs[i + 2] != 0x49 || irbs[i + 3] != 0x4D)
        return null;
      var id = (ushort)((irbs[i + 4] << 8) | irbs[i + 5]);

      var nameLen = irbs[i + 6];
      var nameFieldLen = 1 + nameLen;
      if ((nameFieldLen & 1) != 0) nameFieldLen++;  // pad to even

      var sizeOffset = i + 6 + nameFieldLen;
      if (sizeOffset + 4 > irbs.Length)
        return null;
      var size = (irbs[sizeOffset] << 24) | (irbs[sizeOffset + 1] << 16) | (irbs[sizeOffset + 2] << 8) | irbs[sizeOffset + 3];
      var dataOffset = sizeOffset + 4;
      var paddedSize = (size + 1) & ~1;  // round up to even
      if (dataOffset + paddedSize > irbs.Length)
        return null;

      if (id == PhotoshopIrbIptcId)
        return irbs.Slice(dataOffset, size).ToArray();

      i = dataOffset + paddedSize;
    }
    return null;
  }

  private static byte[] BuildApp13Payload(byte[] iptcBytes, ReadOnlySpan<byte> existingApp13) {
    using var body = new MemoryStream();
    body.Write(PhotoshopHeader);

    var wroteIptc = false;

    // If there's an existing APP13, copy every non-IPTC IRB through verbatim
    // so user-set resolution/color/caption-digest settings aren't lost.
    if (!existingApp13.IsEmpty && existingApp13.StartsWith(PhotoshopHeader)) {
      var irbs = existingApp13.Slice(PhotoshopHeader.Length);
      var i = 0;
      while (i + 12 <= irbs.Length) {
        if (irbs[i] != 0x38 || irbs[i + 1] != 0x42 || irbs[i + 2] != 0x49 || irbs[i + 3] != 0x4D)
          break;
        var id = (ushort)((irbs[i + 4] << 8) | irbs[i + 5]);
        var nameLen = irbs[i + 6];
        var nameFieldLen = 1 + nameLen;
        if ((nameFieldLen & 1) != 0) nameFieldLen++;
        var sizeOffset = i + 6 + nameFieldLen;
        if (sizeOffset + 4 > irbs.Length)
          break;
        var size = (irbs[sizeOffset] << 24) | (irbs[sizeOffset + 1] << 16) | (irbs[sizeOffset + 2] << 8) | irbs[sizeOffset + 3];
        var dataOffset = sizeOffset + 4;
        var paddedSize = (size + 1) & ~1;
        if (dataOffset + paddedSize > irbs.Length)
          break;

        if (id == PhotoshopIrbIptcId) {
          WriteIrb(body, PhotoshopIrbIptcId, iptcBytes);
          wroteIptc = true;
        } else {
          body.Write(irbs.Slice(i, dataOffset + paddedSize - i));
        }

        i = dataOffset + paddedSize;
      }
    }

    if (!wroteIptc)
      WriteIrb(body, PhotoshopIrbIptcId, iptcBytes);

    return body.ToArray();
  }

  private static void WriteIrb(Stream output, ushort id, byte[] data) {
    output.Write(new byte[] { 0x38, 0x42, 0x49, 0x4D });  // "8BIM"
    output.WriteByte((byte)(id >> 8));
    output.WriteByte((byte)id);
    output.WriteByte(0);  // empty pascal name (length 0)
    output.WriteByte(0);  // pad to even
    // Size (big-endian 4 bytes)
    output.WriteByte((byte)(data.Length >> 24));
    output.WriteByte((byte)(data.Length >> 16));
    output.WriteByte((byte)(data.Length >> 8));
    output.WriteByte((byte)data.Length);
    output.Write(data, 0, data.Length);
    if ((data.Length & 1) != 0)
      output.WriteByte(0);  // pad data to even
  }

  private static void WriteApp13Segment(Stream output, byte[] payload) {
    if (payload.Length + 2 > 65535)
      throw new InvalidOperationException("APP13 payload exceeds 64 KB — extended Photoshop segments not implemented.");
    var segmentLength = 2 + payload.Length;
    output.WriteByte(0xFF);
    output.WriteByte(0xED);  // APP13
    output.WriteByte((byte)(segmentLength >> 8));
    output.WriteByte((byte)(segmentLength & 0xFF));
    output.Write(payload, 0, payload.Length);
  }

  private readonly record struct Segment(int Start, int Length, byte Marker, bool IsXmp, bool IsExif = false, bool IsIptc = false) {
    public bool IsAppMarker => this.Marker is >= 0xE0 and <= 0xEF;
    public bool IsSos => this.Marker == 0xDA;
    public bool IsSoiOrEoi => this.Marker is 0xD8 or 0xD9;
  }

  private static List<Segment> ParseSegments(ReadOnlySpan<byte> input) {
    var segments = new List<Segment>();
    var i = 2;  // past SOI

    while (i < input.Length - 1) {
      if (input[i] != 0xFF)
        throw new InvalidDataException($"Expected JPEG marker at offset {i} but found 0x{input[i]:X2}.");

      // Skip marker-fill bytes (some encoders pad with 0xFF).
      while (i < input.Length && input[i] == 0xFF)
        i++;
      if (i >= input.Length)
        break;

      var marker = input[i];
      i++;  // past marker byte

      // Standalone markers with no payload: SOI (D8), EOI (D9), RSTn (D0-D7), TEM (01).
      if (marker is 0xD8 or 0xD9 or 0x01 or (>= 0xD0 and <= 0xD7)) {
        segments.Add(new Segment(i - 2, 2, marker, IsXmp: false));
        if (marker == 0xD9)
          break;
        continue;
      }

      if (i + 1 >= input.Length)
        throw new InvalidDataException("JPEG truncated in segment length.");

      var segLen = (input[i] << 8) | input[i + 1];
      if (segLen < 2)
        throw new InvalidDataException($"Bogus segment length {segLen} at offset {i}.");
      if (i + segLen > input.Length)
        throw new InvalidDataException("JPEG segment extends past end of file.");

      var payloadStart = i + 2;
      var payloadLen = segLen - 2;
      var payload = input.Slice(payloadStart, payloadLen);
      var isXmp = marker == 0xE1 && HasXmpHeader(payload);
      var isExif = marker == 0xE1 && !isXmp && HasExifHeader(payload);
      var isIptc = marker == 0xED && HasPhotoshopHeader(payload);

      // Start of the whole segment is marker byte (i - 2); total length = 2 + segLen.
      segments.Add(new Segment(i - 2, 2 + segLen, marker, isXmp, isExif, isIptc));

      // SOS: after its header, compressed scan data follows all the way to EOI.
      if (marker == 0xDA)
        break;

      i += segLen;
    }

    return segments;
  }

  private static bool HasXmpHeader(ReadOnlySpan<byte> payload) {
    if (payload.Length < XmpHeader.Length)
      return false;
    for (var i = 0; i < XmpHeader.Length; i++) {
      if (payload[i] != XmpHeader[i])
        return false;
    }
    return true;
  }

  private static bool HasExifHeader(ReadOnlySpan<byte> payload) {
    if (payload.Length < ExifHeader.Length)
      return false;
    for (var i = 0; i < ExifHeader.Length; i++) {
      if (payload[i] != ExifHeader[i])
        return false;
    }
    return true;
  }

  private static bool HasPhotoshopHeader(ReadOnlySpan<byte> payload) {
    if (payload.Length < PhotoshopHeader.Length)
      return false;
    for (var i = 0; i < PhotoshopHeader.Length; i++) {
      if (payload[i] != PhotoshopHeader[i])
        return false;
    }
    return true;
  }
}
