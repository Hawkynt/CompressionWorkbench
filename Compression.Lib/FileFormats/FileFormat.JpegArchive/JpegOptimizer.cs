#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.JpegArchive;

/// <summary>
/// JPEG marker reordering optimizer. Moves the EXIF APP1 segment to the first
/// position after SOI for faster metadata/thumbnail access. All other segments
/// retain their original relative order.
/// </summary>
public static class JpegOptimizer {

  private static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();

  /// <summary>
  /// Optimizes a JPEG by moving EXIF (APP1) to be the first segment after SOI.
  /// The stream is rewritten in place (overwritten from position 0, then truncated).
  /// </summary>
  public static void Optimize(Stream stream) => OptimizeCore(stream, moveExifToFront: true);

#if NET10_0_OR_GREATER
  /// <summary>
  /// Optimizes a JPEG with an optional metadata placement profile.
  /// When profile says APP1 → AfterData, the EXIF segment is not moved to front.
  /// </summary>
  public static void Optimize(Stream stream, Compression.Registry.MetadataPlacementProfile? profile) {
    var skipMove = profile?.GetZone("APP1") == Compression.Registry.PlacementZone.AfterData;
    OptimizeCore(stream, moveExifToFront: !skipMove);
  }
#endif

  private static void OptimizeCore(Stream stream, bool moveExifToFront) {
    ArgumentNullException.ThrowIfNull(stream);
    stream.Position = 0;

    var data = new byte[stream.Length];
    var totalRead = 0;
    while (totalRead < data.Length) {
      var n = stream.Read(data, totalRead, data.Length - totalRead);
      if (n == 0) break;
      totalRead += n;
    }

    if (totalRead < 2 || data[0] != 0xFF || data[1] != 0xD8)
      return; // Not a JPEG — nothing to do.

    // Parse all segments into (start, length, isExif) tuples.
    var segments = new List<SegmentInfo>();
    int? exifIndex = null;

    var pos = 2; // past SOI
    while (pos < totalRead - 1) {
      if (data[pos] != 0xFF)
        break;

      var markerStart = pos;

      // Skip padding 0xFF bytes.
      while (pos < totalRead && data[pos] == 0xFF)
        pos++;
      if (pos >= totalRead)
        break;

      var marker = data[pos];
      pos++;

      // Standalone markers (no payload).
      if (marker == 0xD9 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) {
        segments.Add(new SegmentInfo(markerStart, pos - markerStart, marker, false));
        if (marker == 0xD9)
          break;
        continue;
      }

      // Markers with length field.
      if (pos + 1 >= totalRead)
        break;

      var segLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
      if (segLen < 2 || pos + segLen > totalRead)
        break;

      var payloadStart = pos + 2;
      var payloadLen = segLen - 2;
      var segmentTotalLen = (pos - markerStart) + segLen;

      var isExif = marker == 0xE1 && payloadLen >= ExifHeader.Length &&
                   data.AsSpan(payloadStart, ExifHeader.Length).SequenceEqual(ExifHeader);

      segments.Add(new SegmentInfo(markerStart, segmentTotalLen, marker, isExif));

      if (isExif && exifIndex == null)
        exifIndex = segments.Count - 1;

      // SOS: the rest (entropy data + EOI) is treated as one blob.
      if (marker == 0xDA) {
        var tailStart = pos + segLen;
        if (tailStart < totalRead) {
          segments.Add(new SegmentInfo(tailStart, totalRead - tailStart, 0xDA, false)); // scan data + remaining markers/EOI
        }
        break;
      }

      pos += segLen;
    }

    // If there's no EXIF or it's already first, nothing to do.
    if (exifIndex is null or 0)
      return;

    // If the caller says not to move EXIF to front, bail out.
    if (!moveExifToFront)
      return;

    // Reorder: EXIF first, then everything else in original order.
    var exifSeg = segments[exifIndex.Value];
    segments.RemoveAt(exifIndex.Value);
    segments.Insert(0, exifSeg);

    // Write back.
    stream.Position = 0;
    stream.Write(data, 0, 2); // SOI

    foreach (var seg in segments)
      stream.Write(data, seg.Start, seg.Length);

    stream.SetLength(stream.Position);
  }

  private readonly record struct SegmentInfo(int Start, int Length, byte Marker, bool IsExif);
}
