#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Sup;

/// <summary>
/// Reader for Blu-ray PGS (Presentation Graphic Stream) subtitle bitmap streams (<c>.sup</c>).
/// </summary>
/// <remarks>
/// Each segment carries a 13-byte header: <c>"PG"</c> magic (2 bytes), 4-byte BE PTS (90 kHz),
/// 4-byte BE DTS (90 kHz), 1-byte segment type, 2-byte BE segment size, followed by the
/// segment body. Reference: <c>https://blog.thescorpius.com/index.php/2017/07/15/presentation-graphic-stream-sup-files-bluray-subtitle-format/</c>.
/// </remarks>
public sealed class SupReader {

  // Segment-type constants per PGS spec.
  public const byte SegPaletteDefinition = 0x14;
  public const byte SegObjectDefinition = 0x15;
  public const byte SegPresentationComposition = 0x16;
  public const byte SegWindowDefinition = 0x17;
  public const byte SegEnd = 0x80;

  /// <summary>A single PGS segment: header fields plus the raw body bytes.</summary>
  public sealed record Segment(
    uint PtsRaw,            // 90 kHz ticks
    uint DtsRaw,            // 90 kHz ticks
    byte Type,
    byte[] Body,
    int FileOffset);

  /// <summary>A subtitle "epoch" — a PCS segment through the next END segment, inclusive.</summary>
  public sealed record Epoch(
    uint StartPtsRaw,
    uint EndPtsRaw,
    int SegmentCount,
    byte[] RawBytes);

  /// <summary>The full parsed file: every segment plus the derived epoch grouping.</summary>
  public sealed record Stream(
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<Epoch> Epochs);

  /// <summary>
  /// Parses an entire <c>.sup</c> stream. Stops at first malformed segment without throwing,
  /// so partially-recovered files still yield their leading well-formed epochs.
  /// </summary>
  public static Stream Read(ReadOnlySpan<byte> data) {
    if (data.Length < 13) throw new InvalidDataException("PGS: file shorter than minimum 13-byte header.");
    if (data[0] != (byte)'P' || data[1] != (byte)'G')
      throw new InvalidDataException($"PGS: expected magic 'PG' at offset 0, got 0x{data[0]:X2}{data[1]:X2}.");

    var segments = new List<Segment>();
    var pos = 0;
    while (pos + 13 <= data.Length) {
      // Magic check — silently stop on garbage tail rather than throwing mid-stream.
      if (data[pos] != (byte)'P' || data[pos + 1] != (byte)'G') break;

      var pts = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 2)..]);
      var dts = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 6)..]);
      var type = data[pos + 10];
      var size = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 11)..]);

      if (pos + 13 + size > data.Length) break;
      var body = data.Slice(pos + 13, size).ToArray();
      segments.Add(new Segment(pts, dts, type, body, pos));
      pos += 13 + size;
    }

    return new Stream(segments, GroupEpochs(segments, data));
  }

  /// <summary>
  /// Groups segments into "epochs" — runs starting at a Presentation Composition Segment
  /// (PCS) and ending at the next End-of-Display-Set segment (END), inclusive.
  /// Segments that arrive before the first PCS are dropped (they have no display context).
  /// </summary>
  private static List<Epoch> GroupEpochs(IReadOnlyList<Segment> segments, ReadOnlySpan<byte> data) {
    var epochs = new List<Epoch>();
    var startIdx = -1;
    var startPts = 0u;
    for (var i = 0; i < segments.Count; i++) {
      var s = segments[i];
      if (s.Type == SegPresentationComposition && startIdx < 0) {
        startIdx = i;
        startPts = s.PtsRaw;
      }
      if (s.Type == SegEnd && startIdx >= 0) {
        var beginOffset = segments[startIdx].FileOffset;
        var endOffset = s.FileOffset + 13 + s.Body.Length;
        var raw = data.Slice(beginOffset, endOffset - beginOffset).ToArray();
        epochs.Add(new Epoch(
          StartPtsRaw: startPts,
          EndPtsRaw: s.PtsRaw,
          SegmentCount: i - startIdx + 1,
          RawBytes: raw));
        startIdx = -1;
      }
    }
    return epochs;
  }
}
