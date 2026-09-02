#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.JpegArchive;

/// <summary>
/// Walks a JPEG file's marker chain and emits <see cref="DefragBlockInfo"/> tiles
/// for every segment: SOI, APPn metadata, frame/table headers, scan data, and EOI.
/// Does not decode pixel data — purely structural.
/// </summary>
public static class JpegLayoutMap {

  private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
  private static readonly byte[] XmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Position = 0;

    if (stream.Length < 2)
      yield break;

    // Read the whole file for marker walking (JPEG files are typically < 50 MB).
    var data = new byte[stream.Length];
    var totalRead = 0;
    while (totalRead < data.Length) {
      var n = stream.Read(data, totalRead, data.Length - totalRead);
      if (n == 0) break;
      totalRead += n;
    }

    if (totalRead < 2 || data[0] != 0xFF || data[1] != 0xD8)
      yield break;

    // SOI marker (2 bytes)
    yield return new DefragBlockInfo(0, 2, DefragBlockKind.MetadataReserved, FileName: "SOI");

    var pos = 2;

    while (pos < totalRead - 1) {
      if (data[pos] != 0xFF)
        break;

      // Skip padding 0xFF bytes (some encoders pad).
      var markerStart = pos;
      while (pos < totalRead && data[pos] == 0xFF)
        pos++;
      if (pos >= totalRead)
        break;

      var marker = data[pos];
      pos++; // past marker byte

      // Standalone markers with no payload: EOI (D9), RSTn (D0-D7), TEM (01).
      if (marker == 0xD9) {
        yield return new DefragBlockInfo(markerStart, pos - markerStart, DefragBlockKind.MetadataReserved, FileName: "EOI");
        yield break;
      }

      if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) {
        yield return new DefragBlockInfo(markerStart, pos - markerStart, DefragBlockKind.MetadataReserved,
          FileName: marker == 0x01 ? "TEM" : $"RST{marker - 0xD0}");
        continue;
      }

      // All other markers have a 2-byte BE length field.
      if (pos + 1 >= totalRead)
        break;

      var segLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
      if (segLen < 2 || pos + segLen > totalRead)
        break;

      var payloadStart = pos + 2;
      var payloadLen = segLen - 2;
      var segmentTotalLen = (pos - markerStart) + segLen; // marker bytes + length + payload

      switch (marker) {
        case 0xE1: // APP1 — EXIF or XMP
          if (IsExif(data.AsSpan(payloadStart, payloadLen)))
            yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.Used,
              FileName: "EXIF (APP1)", Classification: DefragBlockClass.Hot);
          else if (IsXmp(data.AsSpan(payloadStart, payloadLen)))
            yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.Used,
              FileName: "XMP (APP1)", Classification: DefragBlockClass.Normal);
          else
            yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.Used,
              FileName: "APP1", Classification: DefragBlockClass.Normal);
          break;

        case 0xED: // APP13 — IPTC
          yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.Used,
            FileName: "IPTC (APP13)", Classification: DefragBlockClass.Normal);
          break;

        case 0xE2: // APP2 — ICC Profile
          yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.Used,
            FileName: "ICC Profile (APP2)", Classification: DefragBlockClass.Cold);
          break;

        case 0xE0: // APP0 — JFIF
          yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.Used,
            FileName: "JFIF (APP0)", Classification: DefragBlockClass.Normal);
          break;

        case >= 0xE3 and <= 0xEF when marker != 0xED: // Other APPn
          yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.Used,
            FileName: $"APP{marker - 0xE0}", Classification: DefragBlockClass.Normal);
          break;

        case 0xC0: // SOF0
        case 0xC2: // SOF2
          yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.MetadataReserved,
            FileName: marker == 0xC0 ? "SOF0 (Start of Frame)" : "SOF2 (Progressive)");
          break;

        case >= 0xC1 and <= 0xCF when marker != 0xC4 && marker != 0xC8 && marker != 0xCC: // Other SOF variants
          yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.MetadataReserved,
            FileName: $"SOF{marker - 0xC0}");
          break;

        case 0xC4: // DHT
          yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.MetadataReserved,
            FileName: "DHT (Huffman Table)");
          break;

        case 0xDB: // DQT
          yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.MetadataReserved,
            FileName: "DQT (Quantization Table)");
          break;

        case 0xDD: // DRI
          yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.MetadataReserved,
            FileName: "DRI (Restart Interval)");
          break;

        case 0xFE: // COM
          yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.Used,
            FileName: "Comment", Classification: DefragBlockClass.Frozen);
          break;

        case 0xDA: // SOS — scan header
          yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.MetadataReserved,
            FileName: "SOS (Scan Header)");

          // After SOS header: entropy-coded data runs until next 0xFF+non-zero marker.
          var scanDataStart = pos + segLen;
          var scanDataEnd = scanDataStart;
          while (scanDataEnd < totalRead - 1) {
            if (data[scanDataEnd] == 0xFF && data[scanDataEnd + 1] != 0x00 &&
                data[scanDataEnd + 1] != 0xFF) {
              // Found next marker — but skip RST markers (0xD0..0xD7) inside scan data.
              var nextMarker = data[scanDataEnd + 1];
              if (nextMarker >= 0xD0 && nextMarker <= 0xD7) {
                scanDataEnd += 2;
                continue;
              }
              break;
            }
            scanDataEnd++;
          }

          if (scanDataEnd > scanDataStart) {
            yield return new DefragBlockInfo(scanDataStart, scanDataEnd - scanDataStart, DefragBlockKind.Used,
              FileName: "Scan Data", Classification: DefragBlockClass.Normal);
          }

          pos = scanDataEnd;
          continue; // Skip the normal pos advance below

        default:
          yield return new DefragBlockInfo(markerStart, segmentTotalLen, DefragBlockKind.MetadataReserved,
            FileName: $"Marker 0x{marker:X2}");
          break;
      }

      pos += segLen;
    }
  }

  private static bool IsExif(ReadOnlySpan<byte> payload) {
    if (payload.Length < ExifHeader.Length) return false;
    return payload[..ExifHeader.Length].SequenceEqual(ExifHeader);
  }

  private static bool IsXmp(ReadOnlySpan<byte> payload) {
    if (payload.Length < XmpHeader.Length) return false;
    return payload[..XmpHeader.Length].SequenceEqual(XmpHeader);
  }
}
