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

  private const int SegmentHeaderSize = 13;

  // Segment-type constants per PGS spec.
  /// <summary>
  /// Defines the seg palette definition constant value.
  /// </summary>
  public const byte SegPaletteDefinition = 0x14;
  /// <summary>
  /// Defines the seg object definition constant value.
  /// </summary>
  public const byte SegObjectDefinition = 0x15;
  /// <summary>
  /// Defines the seg presentation composition constant value.
  /// </summary>
  public const byte SegPresentationComposition = 0x16;
  /// <summary>
  /// Defines the seg window definition constant value.
  /// </summary>
  public const byte SegWindowDefinition = 0x17;
  /// <summary>
  /// Defines the seg end constant value.
  /// </summary>
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
  /// Non-owning description of one segment. The body remains in the caller's source span;
  /// this record only identifies its location and size.
  /// </summary>
  internal readonly record struct SegmentLayout(
    uint PtsRaw,
    uint DtsRaw,
    byte Type,
    int FileOffset,
    int BodyOffset,
    int BodyLength);

  /// <summary>
  /// Non-owning description of one PCS-to-END epoch as a contiguous range of the source span.
  /// </summary>
  internal readonly record struct EpochLayout(
    uint StartPtsRaw,
    uint EndPtsRaw,
    int SegmentCount,
    int RawOffset,
    int RawLength);

  /// <summary>
  /// Parsed SUP structure without copied segment or epoch payloads.
  /// </summary>
  internal sealed record StreamLayout(
    IReadOnlyList<SegmentLayout> Segments,
    IReadOnlyList<EpochLayout> Epochs);

  /// <summary>
  /// Parses an entire <c>.sup</c> stream. Stops at first malformed segment without throwing,
  /// so partially-recovered files still yield their leading well-formed epochs.
  /// </summary>
  public static Stream Read(ReadOnlySpan<byte> data) => Materialize(data, ReadLayoutCore(data, strict: false));

  /// <summary>
  /// Parses an entire <c>.sup</c> stream and rejects any malformed or trailing bytes.
  /// This is the validation path used before muxing/remuxing data back to disk.
  /// </summary>
  public static Stream ReadStrict(ReadOnlySpan<byte> data) => Materialize(data, ReadLayoutCore(data, strict: true));

  /// <summary>
  /// Parses SUP structure without taking ownership of segment bodies or epoch byte ranges.
  /// The returned offsets are valid only for the source span supplied to this call.
  /// </summary>
  internal static StreamLayout ReadLayout(ReadOnlySpan<byte> data) => ReadLayoutCore(data, strict: false);

  /// <summary>
  /// Strict counterpart to <see cref="ReadLayout(ReadOnlySpan{byte})"/>.
  /// </summary>
  internal static StreamLayout ReadLayoutStrict(ReadOnlySpan<byte> data) => ReadLayoutCore(data, strict: true);

  private static StreamLayout ReadLayoutCore(ReadOnlySpan<byte> data, bool strict) {
    if (data.Length < SegmentHeaderSize)
      throw new InvalidDataException($"PGS: file shorter than minimum {SegmentHeaderSize}-byte header.");
    if (data[0] != (byte)'P' || data[1] != (byte)'G')
      throw new InvalidDataException($"PGS: expected magic 'PG' at offset 0, got 0x{data[0]:X2}{data[1]:X2}.");

    var segments = new List<SegmentLayout>();
    var pos = 0;
    while (pos + SegmentHeaderSize <= data.Length) {
      if (data[pos] != (byte)'P' || data[pos + 1] != (byte)'G') {
        if (strict)
          throw new InvalidDataException($"PGS: expected magic 'PG' at offset {pos}.");
        break;
      }

      var pts = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 2)..]);
      var dts = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 6)..]);
      var type = data[pos + 10];
      var size = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 11)..]);
      var bodyOffset = pos + SegmentHeaderSize;

      if (size > data.Length - bodyOffset) {
        if (strict)
          throw new InvalidDataException($"PGS: segment at offset {pos} declares {size} body bytes beyond end of stream.");
        break;
      }

      segments.Add(new SegmentLayout(
        PtsRaw: pts,
        DtsRaw: dts,
        Type: type,
        FileOffset: pos,
        BodyOffset: bodyOffset,
        BodyLength: size));
      pos = bodyOffset + size;
    }

    if (strict && pos != data.Length)
      throw new InvalidDataException($"PGS: {data.Length - pos} trailing byte(s) remain after the last complete segment at offset {pos}.");

    return new StreamLayout(segments, GroupEpochs(segments));
  }

  private static Stream Materialize(ReadOnlySpan<byte> data, StreamLayout layout) {
    var segments = new List<Segment>(layout.Segments.Count);
    foreach (var segment in layout.Segments)
      segments.Add(new Segment(
        PtsRaw: segment.PtsRaw,
        DtsRaw: segment.DtsRaw,
        Type: segment.Type,
        Body: data.Slice(segment.BodyOffset, segment.BodyLength).ToArray(),
        FileOffset: segment.FileOffset));

    var epochs = new List<Epoch>(layout.Epochs.Count);
    foreach (var epoch in layout.Epochs)
      epochs.Add(new Epoch(
        StartPtsRaw: epoch.StartPtsRaw,
        EndPtsRaw: epoch.EndPtsRaw,
        SegmentCount: epoch.SegmentCount,
        RawBytes: data.Slice(epoch.RawOffset, epoch.RawLength).ToArray()));

    return new Stream(segments, epochs);
  }

  /// <summary>
  /// Groups segment layouts into "epochs" — runs starting at a Presentation Composition Segment
  /// (PCS) and ending at the next End-of-Display-Set segment (END), inclusive.
  /// Segments that arrive before the first PCS are dropped (they have no display context).
  /// </summary>
  private static List<EpochLayout> GroupEpochs(IReadOnlyList<SegmentLayout> segments) {
    var epochs = new List<EpochLayout>();
    var startIdx = -1;
    var startPts = 0u;
    for (var i = 0; i < segments.Count; i++) {
      var segment = segments[i];
      if (segment.Type == SegPresentationComposition && startIdx < 0) {
        startIdx = i;
        startPts = segment.PtsRaw;
      }
      if (segment.Type == SegEnd && startIdx >= 0) {
        var beginOffset = segments[startIdx].FileOffset;
        var endOffset = segment.BodyOffset + segment.BodyLength;
        epochs.Add(new EpochLayout(
          StartPtsRaw: startPts,
          EndPtsRaw: segment.PtsRaw,
          SegmentCount: i - startIdx + 1,
          RawOffset: beginOffset,
          RawLength: endOffset - beginOffset));
        startIdx = -1;
      }
    }
    return epochs;
  }
}
