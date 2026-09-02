namespace Compression.Analysis.Video;

/// <summary>
/// Outcome of walking one MPEG-1/2 video elementary stream, plus the parse diagnostics a
/// caller needs to judge how much of the frame metadata was actually derived from the
/// bitstream rather than defaulted.
/// </summary>
/// <param name="Frames">One entry per coded frame, in coded (decode) order.</param>
/// <param name="SequenceHeaderCount">Number of <c>sequence_header_code</c> occurrences.</param>
/// <param name="GroupOfPicturesCount">Number of <c>group_start_code</c> occurrences.</param>
/// <param name="ClosedGopCount">Number of GOP headers whose <c>closed_gop</c> flag was set.</param>
/// <param name="BrokenLinkCount">Number of GOP headers whose <c>broken_link</c> flag was set.</param>
/// <param name="FieldPairsMerged">
/// Number of complementary MPEG-2 field-picture pairs that were combined into a single frame.
/// </param>
/// <param name="DecodeOrderPresentationCount">
/// Number of frames whose <see cref="VideoFrameSample.PresentationIndex"/> could not be derived
/// from <c>temporal_reference</c> and was therefore left equal to
/// <see cref="VideoFrameSample.DecodeIndex"/>.
/// </param>
public sealed record Mpeg12VideoFrameParseResult(
  IReadOnlyList<VideoFrameSample> Frames,
  int SequenceHeaderCount,
  int GroupOfPicturesCount,
  int ClosedGopCount,
  int BrokenLinkCount,
  int FieldPairsMerged,
  int DecodeOrderPresentationCount);

/// <summary>
/// Parses an MPEG-1 (ISO/IEC 11172-2) or MPEG-2 (ISO/IEC 13818-2 / ITU-T H.262) <b>video
/// elementary stream</b> into per-picture <see cref="VideoFrameSample"/> metadata, without
/// reconstructing a single pixel.
/// </summary>
/// <remarks>
/// <para>
/// Only four start codes matter here: <c>picture_start_code</c> (0x00), <c>sequence_header_code</c>
/// (0xB3), <c>group_start_code</c> (0xB8) and <c>sequence_end_code</c> (0xB7). Everything else —
/// slices, user data, extensions other than the picture coding extension — is skipped, and the one
/// header body that is bit-parsed is the picture header, whose first two bytes carry
/// <c>temporal_reference</c> and <c>picture_coding_type</c>.
/// </para>
/// <para>
/// MPEG-1/2 video has no emulation-prevention mechanism and needs none. The guarantee is
/// structural rather than an escaping scheme: forbidden code values, marker bits and VLC table
/// design together uphold the blanket rule that start codes "do not otherwise occur in the video
/// stream", so scanning for the byte pattern <c>00 00 01</c> is sound. <c>next_start_code()</c>
/// permits any number of zero stuffing bytes immediately ahead of a prefix, so the scanner locates
/// the <em>last</em> <c>00 00 01</c> triple before the start-code value byte; leading stuffing
/// zeros are therefore attributed to the preceding picture, which is what a coded-size figure
/// should report.
/// </para>
/// <para>What this parser deliberately does not do:</para>
/// <list type="bullet">
///   <item>
///     No timestamps. A video elementary stream carries none — PTS/DTS live in the PES layer — so
///     <see cref="VideoFrameSample.DecodeTimestamp"/> and
///     <see cref="VideoFrameSample.PresentationTimestamp"/> are always left null. The
///     <c>vbv_delay</c> field is a buffer occupancy hint, not a presentation time, and is not
///     reinterpreted as one.
///   </item>
///   <item>
///     No sequence-header body parsing, so no resolution, aspect ratio, frame rate or bit rate.
///     None of those are needed for temporal-structure analysis.
///   </item>
///   <item>
///     Field pictures are paired into frames (see below) but no per-field detail is exposed:
///     <c>top_field_first</c>, <c>repeat_first_field</c>, <c>progressive_frame</c> and individual
///     field sizes are read past, and 3:2 pulldown is not unrolled.
///   </item>
///   <item>
///     No slice-level work, so no quantiser summary and no detection of a picture whose slices are
///     truncated or missing.
///   </item>
/// </list>
/// </remarks>
public static class Mpeg12VideoFrameParser {

  /// <summary>The <c>picture_start_code</c> value that follows a 0x000001 start-code prefix.</summary>
  public const byte PictureStartCode = 0x00;

  /// <summary>The <c>sequence_header_code</c> value that follows a 0x000001 start-code prefix.</summary>
  public const byte SequenceHeaderCode = 0xB3;

  /// <summary>The <c>extension_start_code</c> value that follows a 0x000001 start-code prefix.</summary>
  public const byte ExtensionStartCode = 0xB5;

  /// <summary>The <c>sequence_end_code</c> value that follows a 0x000001 start-code prefix.</summary>
  public const byte SequenceEndCode = 0xB7;

  /// <summary>The <c>group_start_code</c> value that follows a 0x000001 start-code prefix.</summary>
  public const byte GroupStartCode = 0xB8;

  /// <summary>The <c>extension_start_code_identifier</c> value of a picture coding extension.</summary>
  private const int PictureCodingExtensionId = 0x8;

  private const int PictureStructureTopField = 1;
  private const int PictureStructureBottomField = 2;
  private const int PictureStructureFrame = 3;

  /// <summary>Reads a complete MPEG-1/2 video elementary stream into the buffer and parses it.</summary>
  /// <param name="stream">The elementary stream to read. Read to its end; not disposed.</param>
  /// <returns>The coded pictures and parse diagnostics.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
  public static Mpeg12VideoFrameParseResult Parse(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);

    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
  }

  /// <summary>Parses an in-memory MPEG-1/2 video elementary stream.</summary>
  /// <param name="data">The elementary-stream bytes.</param>
  /// <returns>The coded pictures and parse diagnostics.</returns>
  public static Mpeg12VideoFrameParseResult Parse(ReadOnlySpan<byte> data) {
    var pictures = new List<CodedPicture>();

    var sequenceHeaderCount = 0;
    var groupCount = 0;
    var closedGopCount = 0;
    var brokenLinkCount = 0;

    var sawSequenceHeader = false;
    var sequenceHeaderPending = false;
    var groupHeaderPending = false;
    var groupOrdinal = 0;
    var awaitingPictureCodingExtension = false;

    for (var i = 0; i + 3 < data.Length;) {
      if (data[i] != 0x00 || data[i + 1] != 0x00 || data[i + 2] != 0x01) {
        ++i;
        continue;
      }

      var startCodeOffset = i;
      var startCode = data[i + 3];

      // Resume at the value byte rather than past it: in "00 00 01 00 00 01 xx" the second prefix
      // begins one byte into the first start code, and skipping four would step over it.
      i += 3;

      var bodyStart = i + 1;
      if (startCode == ExtensionStartCode) {
        if (awaitingPictureCodingExtension) {
          awaitingPictureCodingExtension = false;
          ReadPictureCodingExtension(data[bodyStart..], pictures[^1]);
        }

        continue;
      }

      awaitingPictureCodingExtension = false;
      if (startCode is not (PictureStartCode or SequenceHeaderCode or GroupStartCode or SequenceEndCode))
        continue;

      // Every picture ends where the next picture, GOP header, sequence header or sequence end
      // code begins.
      if (pictures.Count > 0 && pictures[^1].EndOffset < 0)
        pictures[^1].EndOffset = startCodeOffset;

      switch (startCode) {
        case SequenceHeaderCode:
          ++sequenceHeaderCount;
          sawSequenceHeader = true;
          sequenceHeaderPending = true;
          break;

        case GroupStartCode:
          ++groupCount;
          ++groupOrdinal;
          groupHeaderPending = true;
          // group_of_pictures_header(): time_code (25), closed_gop (1), broken_link (1).
          if (bodyStart + 4 <= data.Length) {
            if ((data[bodyStart + 3] & 0x40) != 0)
              ++closedGopCount;
            if ((data[bodyStart + 3] & 0x20) != 0)
              ++brokenLinkCount;
          }

          break;

        case PictureStartCode:
          var picture = ReadPictureHeader(data[bodyStart..], startCodeOffset, groupOrdinal);

          // An I picture is a random access point when the decoder can start there: it must be
          // introduced by a sequence header, or by a GOP header in a stream whose sequence header
          // has already been seen. broken_link marks the B pictures that follow the I picture as
          // undecodable, not the I picture itself, so it does not clear this flag; it is reported
          // separately instead.
          picture.IsRandomAccess = picture.CodingType == 1
            && (sequenceHeaderPending || (groupHeaderPending && sawSequenceHeader));

          pictures.Add(picture);
          sequenceHeaderPending = false;
          groupHeaderPending = false;
          awaitingPictureCodingExtension = true;
          break;
      }
    }

    if (pictures.Count > 0 && pictures[^1].EndOffset < 0)
      pictures[^1].EndOffset = data.Length;

    var frames = BuildFrames(pictures, out var fieldPairsMerged, out var decodeOrderPresentationCount);

    return new Mpeg12VideoFrameParseResult(
      Frames: frames,
      SequenceHeaderCount: sequenceHeaderCount,
      GroupOfPicturesCount: groupCount,
      ClosedGopCount: closedGopCount,
      BrokenLinkCount: brokenLinkCount,
      FieldPairsMerged: fieldPairsMerged,
      DecodeOrderPresentationCount: decodeOrderPresentationCount);
  }

  /// <summary>
  /// Reads the fixed head of <c>picture_header()</c>: <c>temporal_reference</c> (10 bits) followed
  /// by <c>picture_coding_type</c> (3 bits).
  /// </summary>
  private static CodedPicture ReadPictureHeader(ReadOnlySpan<byte> body, long offset, int groupOrdinal) {
    var picture = new CodedPicture {
      Offset = offset,
      GroupOrdinal = groupOrdinal,
      PictureStructure = PictureStructureFrame,
    };

    if (body.Length < 2) {
      // Truncated stream: neither the display order nor the picture kind is knowable.
      picture.IsCorrupt = true;
      return picture;
    }

    picture.TemporalReference = (body[0] << 2) | (body[1] >> 6);
    picture.CodingType = (body[1] >> 3) & 0x07;
    // 0 is forbidden and 5..7 are reserved; both mean this is not a picture we can characterise.
    picture.IsCorrupt = picture.CodingType is 0 or > 4;
    return picture;
  }

  /// <summary>
  /// Reads <c>picture_structure</c> out of an MPEG-2 <c>picture_coding_extension()</c>, which is
  /// the first extension permitted after a picture header. MPEG-1 has no extensions, so its
  /// pictures keep the frame-picture default.
  /// </summary>
  private static void ReadPictureCodingExtension(ReadOnlySpan<byte> body, CodedPicture picture) {
    // extension_start_code_identifier (4), f_code[0][0] (4), f_code[0][1] (4), f_code[1][0] (4),
    // f_code[1][1] (4), intra_dc_precision (2), picture_structure (2).
    if (body.Length < 3 || body[0] >> 4 != PictureCodingExtensionId)
      return;

    picture.PictureStructure = body[2] & 0x03;
  }

  /// <summary>
  /// Turns coded pictures into frame samples: pairs complementary field pictures, then assigns
  /// decode and presentation indices.
  /// </summary>
  private static VideoFrameSample[] BuildFrames(
    List<CodedPicture> pictures,
    out int fieldPairsMerged,
    out int decodeOrderPresentationCount) {
    fieldPairsMerged = 0;
    decodeOrderPresentationCount = 0;

    // Two field pictures of one frame share a temporal_reference, so emitting both would hand the
    // analyzer duplicate presentation indices. Combine them into the frame they encode.
    var merged = new List<CodedPicture>(pictures.Count);
    for (var i = 0; i < pictures.Count; ++i) {
      var picture = pictures[i];
      if (IsFieldPicture(picture.PictureStructure) && i + 1 < pictures.Count) {
        var second = pictures[i + 1];
        if (IsFieldPicture(second.PictureStructure)
            && second.PictureStructure != picture.PictureStructure
            && second.GroupOrdinal == picture.GroupOrdinal
            && second.TemporalReference == picture.TemporalReference) {
          // The frame is entered on its first field, so that field supplies the picture kind; the
          // second field's coded bytes are folded into the frame's size.
          picture.EndOffset = second.EndOffset;
          picture.IsRandomAccess |= second.IsRandomAccess;
          picture.IsCorrupt |= second.IsCorrupt;
          ++fieldPairsMerged;
          ++i;
        }
      }

      merged.Add(picture);
    }

    var frames = new VideoFrameSample[merged.Count];
    var offsets = new int[merged.Count];

    for (var start = 0; start < merged.Count;) {
      var end = start;
      var ordinal = merged[start].GroupOrdinal;
      while (end < merged.Count && merged[end].GroupOrdinal == ordinal)
        ++end;

      var count = end - start;
      var derived = TryDerivePresentationOffsets(merged, start, count, offsets);
      if (!derived)
        decodeOrderPresentationCount += count;

      for (var k = 0; k < count; ++k) {
        var picture = merged[start + k];
        var decodeIndex = start + k;
        frames[decodeIndex] = new VideoFrameSample(
          DecodeIndex: decodeIndex,
          PresentationIndex: derived ? start + offsets[k] : decodeIndex,
          Kind: ToKind(picture.CodingType),
          SizeBytes: (int)Math.Min(picture.EndOffset - picture.Offset, int.MaxValue),
          Offset: picture.Offset,
          IsRandomAccess: picture.IsRandomAccess,
          // I and P pictures are stored for prediction; B pictures never are, and MPEG-1 D
          // pictures are DC-only and may not be referenced either.
          IsReference: picture.CodingType is 1 or 2,
          IsCorrupt: picture.IsCorrupt);
      }

      start = end;
    }

    return frames;
  }

  /// <summary>
  /// Derives display positions within one group of pictures from <c>temporal_reference</c>, which
  /// counts pictures in display order and resets to zero after each GOP header.
  /// </summary>
  /// <returns>
  /// <see langword="true"/> when the group's temporal references are distinct and span exactly as
  /// many positions as the group has frames, so the derived order is trustworthy; otherwise
  /// <see langword="false"/>, and the caller falls back to coded order.
  /// </returns>
  private static bool TryDerivePresentationOffsets(
    List<CodedPicture> pictures,
    int start,
    int count,
    int[] offsets) {
    var minimum = int.MaxValue;
    var maximum = int.MinValue;

    for (var k = 0; k < count; ++k) {
      var picture = pictures[start + k];
      if (picture.IsCorrupt || picture.TemporalReference < 0)
        return false;

      minimum = Math.Min(minimum, picture.TemporalReference);
      maximum = Math.Max(maximum, picture.TemporalReference);
    }

    // A group truncated at the head starts at a non-zero reference, which is still usable; a
    // group whose references are sparse, repeated or wrapped past 1023 is not. A low_delay stream
    // containing big pictures also lands here, because there temporal_reference may step by more
    // than one and the first frame after a GOP header is not necessarily zero.
    if (maximum - minimum != count - 1)
      return false;

    var seen = new bool[count];
    for (var k = 0; k < count; ++k) {
      var candidate = pictures[start + k].TemporalReference - minimum;
      if (seen[candidate])
        return false;

      seen[candidate] = true;
      offsets[k] = candidate;
    }

    return true;
  }

  private static bool IsFieldPicture(int pictureStructure)
    => pictureStructure is PictureStructureTopField or PictureStructureBottomField;

  private static VideoFrameKind ToKind(int pictureCodingType) => pictureCodingType switch {
    1 => VideoFrameKind.I,
    2 => VideoFrameKind.P,
    3 => VideoFrameKind.B,
    // D pictures carry DC coefficients alone. ISO/IEC 11172-2 defines them; ISO/IEC 13818-2
    // forbids the value, so encountering one also says the stream is MPEG-1.
    4 => VideoFrameKind.S,
    // Negative means the header was truncated before the type could be read at all.
    < 0 => VideoFrameKind.Unknown,
    _ => VideoFrameKind.Other,
  };

  /// <summary>One coded picture as found in the bitstream, before field pairing and indexing.</summary>
  private sealed class CodedPicture {
    public long Offset;
    public long EndOffset = -1;
    public int GroupOrdinal;
    public int TemporalReference = -1;
    public int CodingType = -1;
    public int PictureStructure;
    public bool IsRandomAccess;
    public bool IsCorrupt;
  }
}
