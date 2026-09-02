#pragma warning disable CS1591
using Compression.Analysis.Video;

namespace Compression.Tests.Analysis;

/// <summary>
/// Streams are assembled byte by byte from the ISO/IEC 11172-2 and ISO/IEC 13818-2 syntax tables so
/// every expectation is traceable to the specification rather than to a captured file.
/// </summary>
[TestFixture]
public class Mpeg12VideoFrameParserTests {

  private const int PictureTypeI = 1;
  private const int PictureTypeP = 2;
  private const int PictureTypeB = 3;
  private const int PictureTypeD = 4;

  [Test]
  public void IntraOnlyStream_YieldsOneRandomAccessReferenceFrame() {
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    var pictureOffset = builder.Picture(temporalReference: 0, PictureTypeI);
    builder.Slice(1, 64);

    var result = Mpeg12VideoFrameParser.Parse(builder.ToArray());

    Assert.That(result.Frames, Has.Count.EqualTo(1));
    var frame = result.Frames[0];
    Assert.Multiple(() => {
      Assert.That(frame.Kind, Is.EqualTo(VideoFrameKind.I));
      Assert.That(frame.DecodeIndex, Is.Zero);
      Assert.That(frame.PresentationIndex, Is.Zero);
      Assert.That(frame.Offset, Is.EqualTo(pictureOffset));
      Assert.That(frame.SizeBytes, Is.EqualTo(builder.Length - pictureOffset));
      Assert.That(frame.IsRandomAccess, Is.True);
      Assert.That(frame.IsReference, Is.True);
      Assert.That(frame.IsCorrupt, Is.False);
      Assert.That(frame.DecodeTimestamp, Is.Null, "an elementary stream carries no PES timing");
      Assert.That(frame.PresentationTimestamp, Is.Null);
      Assert.That(result.SequenceHeaderCount, Is.EqualTo(1));
      Assert.That(result.GroupOfPicturesCount, Is.EqualTo(1));
      Assert.That(result.DecodeOrderPresentationCount, Is.Zero);
    });
  }

  [Test]
  public void IbbpGop_ReportsKindsReferenceFlagsAndTemporalReferenceReorder() {
    // Display order I B B P; coded order I P B B, which is exactly what temporal_reference
    // 0, 3, 1, 2 encodes.
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    builder.Picture(0, PictureTypeI);
    builder.Slice(1, 96);
    builder.Picture(3, PictureTypeP);
    builder.Slice(1, 48);
    builder.Picture(1, PictureTypeB);
    builder.Slice(1, 24);
    builder.Picture(2, PictureTypeB);
    builder.Slice(1, 24);

    var frames = Mpeg12VideoFrameParser.Parse(builder.ToArray()).Frames;

    Assert.Multiple(() => {
      Assert.That(frames.Select(frame => frame.Kind), Is.EqualTo(new[] {
        VideoFrameKind.I, VideoFrameKind.P, VideoFrameKind.B, VideoFrameKind.B,
      }));
      Assert.That(frames.Select(frame => frame.DecodeIndex), Is.EqualTo(new[] { 0, 1, 2, 3 }));
      Assert.That(frames.Select(frame => frame.PresentationIndex), Is.EqualTo(new[] { 0, 3, 1, 2 }));
      Assert.That(frames.Select(frame => frame.IsReference), Is.EqualTo(new[] { true, true, false, false }));
      Assert.That(frames.Select(frame => frame.IsRandomAccess), Is.EqualTo(new[] { true, false, false, false }));
    });
  }

  [Test]
  public void IntraPictureWithoutSequenceOrGopHeader_IsNotARandomAccessPoint() {
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    builder.Picture(0, PictureTypeI);
    builder.Slice(1, 64);
    builder.Picture(1, PictureTypeP);
    builder.Slice(1, 32);
    // A second intra picture spliced in mid-GOP: intra coded, but no decoder can start here
    // because no sequence or GOP header re-introduces it.
    builder.Picture(2, PictureTypeI);
    builder.Slice(1, 64);

    var result = Mpeg12VideoFrameParser.Parse(builder.ToArray());
    var report = VideoFrameStructureAnalyzer.Analyze(result.Frames);

    Assert.Multiple(() => {
      Assert.That(result.Frames[2].Kind, Is.EqualTo(VideoFrameKind.I));
      Assert.That(result.Frames[2].IsRandomAccess, Is.False);
      Assert.That(report.IntraWithoutRandomAccessCount, Is.EqualTo(1));
      Assert.That(report.RandomAccessNonIntraCount, Is.Zero);
    });
  }

  [Test]
  public void GopHeaderReintroducesRandomAccess_WhenASequenceHeaderWasSeenEarlier() {
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    builder.Picture(0, PictureTypeI);
    builder.Slice(1, 64);
    // Second GOP without repeating the sequence header — the common broadcast layout.
    builder.GopHeader();
    builder.Picture(0, PictureTypeI);
    builder.Slice(1, 64);

    var result = Mpeg12VideoFrameParser.Parse(builder.ToArray());

    Assert.Multiple(() => {
      Assert.That(result.Frames.Select(frame => frame.IsRandomAccess), Is.EqualTo(new[] { true, true }));
      // temporal_reference restarts at each GOP header, so the second GOP's base must advance.
      Assert.That(result.Frames.Select(frame => frame.PresentationIndex), Is.EqualTo(new[] { 0, 1 }));
      Assert.That(result.GroupOfPicturesCount, Is.EqualTo(2));
    });
  }

  [Test]
  public void SizeBytes_SpanEachPictureUpToTheNextBoundaryIncludingTheLastPicture() {
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    var first = builder.Picture(0, PictureTypeI);
    builder.Slice(1, 100);
    var second = builder.Picture(1, PictureTypeP);
    builder.Slice(1, 40);
    // A GOP header, not a picture, terminates the second picture.
    builder.GopHeader();
    var third = builder.Picture(0, PictureTypeI);
    builder.Slice(1, 70);
    var endOfLastPicture = builder.Length;
    builder.SequenceEnd();

    var frames = Mpeg12VideoFrameParser.Parse(builder.ToArray()).Frames;

    Assert.Multiple(() => {
      Assert.That(frames.Select(frame => frame.Offset), Is.EqualTo(new long[] { first, second, third }));
      Assert.That(frames[0].SizeBytes, Is.EqualTo(second - first));
      Assert.That(frames[1].SizeBytes, Is.EqualTo(third - second - EsBuilder.GopHeaderLength));
      // sequence_end_code closes the stream; it is not part of the last picture's coded bytes.
      Assert.That(frames[2].SizeBytes, Is.EqualTo(endOfLastPicture - third));
    });
  }

  [Test]
  public void SliceExtensionAndUserDataStartCodes_DoNotSplitAPicture() {
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    var pictureOffset = builder.Picture(0, PictureTypeI);
    builder.PictureCodingExtension(EsBuilder.FramePicture);
    builder.UserData(16);
    builder.Slice(0x01, 40);
    builder.Slice(0x02, 40);
    builder.Slice(0xAF, 40);

    var result = Mpeg12VideoFrameParser.Parse(builder.ToArray());

    Assert.Multiple(() => {
      Assert.That(result.Frames, Has.Count.EqualTo(1), "slice/extension/user-data codes are not picture boundaries");
      Assert.That(result.Frames[0].Offset, Is.EqualTo(pictureOffset));
      Assert.That(result.Frames[0].SizeBytes, Is.EqualTo(builder.Length - pictureOffset));
    });
  }

  [Test]
  public void NearMissBytePatternsInCodedData_AreNotTreatedAsStartCodes() {
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    var pictureOffset = builder.Picture(0, PictureTypeI);
    builder.StartCode(0x01);
    // Patterns one byte away from a start-code prefix on every side.
    builder.Raw(0x00, 0x00, 0x02, 0x11);
    builder.Raw(0x00, 0x01, 0x00, 0x22);
    builder.Raw(0x01, 0x00, 0x00, 0x33);
    builder.Raw(0x00, 0x00, 0x00, 0x02, 0x44);
    builder.Raw(0x00, 0x02, 0x01, 0x55);

    var result = Mpeg12VideoFrameParser.Parse(builder.ToArray());

    Assert.Multiple(() => {
      Assert.That(result.Frames, Has.Count.EqualTo(1));
      Assert.That(result.Frames[0].Offset, Is.EqualTo(pictureOffset));
      Assert.That(result.Frames[0].SizeBytes, Is.EqualTo(builder.Length - pictureOffset));
    });
  }

  [Test]
  public void ZeroByteStuffingBeforeAStartCodePrefix_IsAccepted() {
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    var first = builder.Picture(0, PictureTypeI);
    builder.Slice(1, 32);
    // "00 00 00 01 00": one stuffing zero ahead of the prefix. The start code is the last
    // 00 00 01 triple, so the stuffing byte belongs to the picture that precedes it.
    builder.Raw(0x00);
    var second = builder.Picture(1, PictureTypeP);
    builder.Slice(1, 32);
    // Three stuffing zeros ahead of the next prefix.
    builder.Raw(0x00, 0x00, 0x00);
    var third = builder.Picture(2, PictureTypeB);
    builder.Slice(1, 16);

    var frames = Mpeg12VideoFrameParser.Parse(builder.ToArray()).Frames;

    Assert.Multiple(() => {
      Assert.That(frames.Select(frame => frame.Kind), Is.EqualTo(new[] {
        VideoFrameKind.I, VideoFrameKind.P, VideoFrameKind.B,
      }));
      Assert.That(frames.Select(frame => frame.Offset), Is.EqualTo(new long[] { first, second, third }));
      Assert.That(frames[0].SizeBytes, Is.EqualTo(second - first), "the stuffing zero counts against the previous picture");
      Assert.That(frames[1].SizeBytes, Is.EqualTo(third - second));
    });
  }

  [Test]
  public void TwoGopsOfIbbpbbp_FeedTheStructureAnalyzerEndToEnd() {
    // Display order per GOP: I B B P B B P, so coded order is I P B B P B B with
    // temporal_reference 0, 3, 1, 2, 6, 4, 5.
    int[] temporalReferences = [0, 3, 1, 2, 6, 4, 5];
    int[] codingTypes = [PictureTypeI, PictureTypeP, PictureTypeB, PictureTypeB, PictureTypeP, PictureTypeB, PictureTypeB];

    var builder = new EsBuilder();
    builder.SequenceHeader();
    for (var gop = 0; gop < 2; ++gop) {
      builder.GopHeader();
      for (var i = 0; i < temporalReferences.Length; ++i) {
        builder.Picture(temporalReferences[i], codingTypes[i]);
        builder.Slice(1, codingTypes[i] switch { PictureTypeI => 200, PictureTypeP => 100, _ => 40 });
      }
    }

    builder.SequenceEnd();

    var result = Mpeg12VideoFrameParser.Parse(builder.ToArray());
    var report = VideoFrameStructureAnalyzer.Analyze(result.Frames);

    Assert.Multiple(() => {
      Assert.That(result.Frames, Has.Count.EqualTo(14));
      Assert.That(result.DecodeOrderPresentationCount, Is.Zero, "every GOP's temporal references were usable");
      Assert.That(report.GopPatterns, Is.EqualTo(new[] { new VideoGopPattern("IBBPBBP", 2) }));
      Assert.That(report.MaxConsecutiveBFrames, Is.EqualTo(2));
      Assert.That(report.MaxReorderDepthFrames, Is.EqualTo(2));
      Assert.That(report.IToI.Frames.MinimumFrames, Is.EqualTo(7));
      Assert.That(report.RandomAccessToRandomAccess.Frames.MinimumFrames, Is.EqualTo(7));
      Assert.That(report.IntraWithoutRandomAccessCount, Is.Zero);
      Assert.That(report.RandomAccessNonIntraCount, Is.Zero);
      Assert.That(report.FrameKinds.Single(kind => kind.Kind == VideoFrameKind.B).Count, Is.EqualTo(8));
      // Coded sizes come straight out of the start-code boundaries: an I picture carries a 200
      // byte slice, a P picture 100 and a B picture 40.
      Assert.That(report.FrameKinds.Single(kind => kind.Kind == VideoFrameKind.I).MinimumSizeBytes, Is.EqualTo(212));
      Assert.That(report.FrameKinds.Single(kind => kind.Kind == VideoFrameKind.P).MaximumSizeBytes, Is.EqualTo(112));
      Assert.That(report.FrameKinds.Single(kind => kind.Kind == VideoFrameKind.B).MaximumSizeBytes, Is.EqualTo(52));
    });
  }

  [Test]
  public void ComplementaryFieldPictures_AreMergedIntoOneFrame() {
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    var frameOffset = builder.Picture(0, PictureTypeI);
    builder.PictureCodingExtension(EsBuilder.TopField);
    builder.Slice(1, 60);
    builder.Picture(0, PictureTypeP);
    builder.PictureCodingExtension(EsBuilder.BottomField);
    builder.Slice(1, 60);
    var endOfFrame = builder.Length;
    builder.Picture(1, PictureTypeP);
    builder.PictureCodingExtension(EsBuilder.FramePicture);
    builder.Slice(1, 50);

    var result = Mpeg12VideoFrameParser.Parse(builder.ToArray());

    Assert.Multiple(() => {
      Assert.That(result.FieldPairsMerged, Is.EqualTo(1));
      Assert.That(result.Frames, Has.Count.EqualTo(2), "the two fields sharing temporal_reference 0 are one frame");
      // The frame is entered on its first field, so that field supplies the kind.
      Assert.That(result.Frames[0].Kind, Is.EqualTo(VideoFrameKind.I));
      Assert.That(result.Frames[0].Offset, Is.EqualTo(frameOffset));
      Assert.That(result.Frames[0].SizeBytes, Is.EqualTo(endOfFrame - frameOffset), "both fields' bytes");
      Assert.That(result.Frames.Select(frame => frame.PresentationIndex), Is.EqualTo(new[] { 0, 1 }));
      // Without the merge these two would collide on presentation index 0.
      Assert.That(() => VideoFrameStructureAnalyzer.Analyze(result.Frames), Throws.Nothing);
    });
  }

  [Test]
  public void ClosedGopAndBrokenLinkFlags_AreCountedIndependently() {
    // The two flags are adjacent bits, so the counts are kept deliberately asymmetric: reading
    // one where the other was meant would swap 3 and 1 and fail.
    var builder = new EsBuilder();
    builder.SequenceHeader();
    foreach (var (closedGop, brokenLink) in new[] { (true, false), (true, false), (true, true), (false, false) }) {
      builder.GopHeader(closedGop, brokenLink);
      // temporal_reference restarts at zero after every GOP header.
      builder.Picture(0, PictureTypeI);
      builder.Slice(1, 32);
    }

    var result = Mpeg12VideoFrameParser.Parse(builder.ToArray());

    Assert.Multiple(() => {
      Assert.That(result.GroupOfPicturesCount, Is.EqualTo(4));
      Assert.That(result.ClosedGopCount, Is.EqualTo(3));
      Assert.That(result.BrokenLinkCount, Is.EqualTo(1));
      // broken_link marks the following B pictures as undecodable, not the I picture itself, so
      // every one of these remains a random access point.
      Assert.That(result.Frames.Select(frame => frame.IsRandomAccess),
        Is.EqualTo(new[] { true, true, true, true }));
    });
  }

  [Test]
  public void PictureStartCodeTruncatedAtEndOfStream_IsFlaggedRatherThanGuessed() {
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    builder.Picture(0, PictureTypeI);
    builder.Slice(1, 32);
    // A start code with no picture header behind it: neither kind nor display order is knowable.
    var truncated = builder.StartCode(Mpeg12VideoFrameParser.PictureStartCode);

    var result = Mpeg12VideoFrameParser.Parse(builder.ToArray());

    Assert.Multiple(() => {
      Assert.That(result.Frames, Has.Count.EqualTo(2));
      Assert.That(result.Frames[1].Kind, Is.EqualTo(VideoFrameKind.Unknown));
      Assert.That(result.Frames[1].IsCorrupt, Is.True);
      Assert.That(result.Frames[1].Offset, Is.EqualTo(truncated));
      // One unusable picture makes the whole group's temporal references untrustworthy.
      Assert.That(result.DecodeOrderPresentationCount, Is.EqualTo(2));
      Assert.That(() => VideoFrameStructureAnalyzer.Analyze(result.Frames), Throws.Nothing);
    });
  }

  [Test]
  public void UnusableTemporalReferences_FallBackToCodedOrderInsteadOfGuessing() {
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    builder.Picture(0, PictureTypeI);
    builder.Slice(1, 32);
    // A repeated temporal_reference cannot describe a display order.
    builder.Picture(0, PictureTypeP);
    builder.Slice(1, 32);
    builder.Picture(5, PictureTypeP);
    builder.Slice(1, 32);

    var result = Mpeg12VideoFrameParser.Parse(builder.ToArray());

    Assert.Multiple(() => {
      Assert.That(result.DecodeOrderPresentationCount, Is.EqualTo(3));
      Assert.That(result.Frames.Select(frame => frame.PresentationIndex), Is.EqualTo(new[] { 0, 1, 2 }));
      Assert.That(() => VideoFrameStructureAnalyzer.Analyze(result.Frames), Throws.Nothing);
    });
  }

  [Test]
  public void GopTruncatedAtItsHead_StillDerivesADisplayOrder() {
    // Capture started mid-GOP: temporal_reference 0..1 were never received.
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    builder.Picture(4, PictureTypeP);
    builder.Slice(1, 32);
    builder.Picture(2, PictureTypeB);
    builder.Slice(1, 16);
    builder.Picture(3, PictureTypeB);
    builder.Slice(1, 16);

    var result = Mpeg12VideoFrameParser.Parse(builder.ToArray());

    Assert.Multiple(() => {
      Assert.That(result.DecodeOrderPresentationCount, Is.Zero);
      Assert.That(result.Frames.Select(frame => frame.PresentationIndex), Is.EqualTo(new[] { 2, 0, 1 }));
    });
  }

  [Test]
  public void DPictures_MapToTheSKindAndAreNotReferences() {
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    builder.Picture(0, PictureTypeD);
    builder.Slice(1, 16);

    var frame = Mpeg12VideoFrameParser.Parse(builder.ToArray()).Frames[0];

    Assert.Multiple(() => {
      Assert.That(frame.Kind, Is.EqualTo(VideoFrameKind.S));
      Assert.That(frame.IsReference, Is.False);
      Assert.That(frame.IsRandomAccess, Is.False, "only picture_coding_type 1 is intra coded");
      Assert.That(frame.IsCorrupt, Is.False);
    });
  }

  [Test]
  public void ReservedPictureCodingType_IsReportedAsOtherAndCorrupt() {
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    builder.Picture(0, codingType: 6);
    builder.Slice(1, 16);

    var frame = Mpeg12VideoFrameParser.Parse(builder.ToArray()).Frames[0];

    Assert.Multiple(() => {
      Assert.That(frame.Kind, Is.EqualTo(VideoFrameKind.Other));
      Assert.That(frame.IsCorrupt, Is.True);
    });
  }

  [Test]
  public void EmptyAndHeaderOnlyStreams_YieldNoFrames() {
    var headerOnly = new EsBuilder();
    headerOnly.SequenceHeader();
    headerOnly.GopHeader();
    headerOnly.SequenceEnd();

    Assert.Multiple(() => {
      Assert.That(Mpeg12VideoFrameParser.Parse(ReadOnlySpan<byte>.Empty).Frames, Is.Empty);
      Assert.That(Mpeg12VideoFrameParser.Parse(headerOnly.ToArray()).Frames, Is.Empty);
      Assert.That(Mpeg12VideoFrameParser.Parse(new byte[] { 0x00, 0x00, 0x01 }).Frames, Is.Empty);
    });
  }

  [Test]
  public void StreamOverload_MatchesTheSpanOverload() {
    var builder = new EsBuilder();
    builder.SequenceHeader();
    builder.GopHeader();
    builder.Picture(0, PictureTypeI);
    builder.Slice(1, 48);
    builder.Picture(1, PictureTypeP);
    builder.Slice(1, 24);
    var bytes = builder.ToArray();

    using var stream = new MemoryStream(bytes, writable: false);
    var fromStream = Mpeg12VideoFrameParser.Parse(stream);
    var fromSpan = Mpeg12VideoFrameParser.Parse(bytes);

    Assert.Multiple(() => {
      Assert.That(fromStream.Frames, Is.EqualTo(fromSpan.Frames));
      Assert.That(fromStream.SequenceHeaderCount, Is.EqualTo(fromSpan.SequenceHeaderCount));
      Assert.That(fromStream.GroupOfPicturesCount, Is.EqualTo(fromSpan.GroupOfPicturesCount));
      Assert.That(fromStream.FieldPairsMerged, Is.EqualTo(fromSpan.FieldPairsMerged));
      Assert.That(fromStream.DecodeOrderPresentationCount, Is.EqualTo(fromSpan.DecodeOrderPresentationCount));
    });
  }

  [Test]
  public void NullStream_IsRejected()
    => Assert.That(() => Mpeg12VideoFrameParser.Parse((Stream)null!), Throws.ArgumentNullException);

  /// <summary>
  /// Builds MPEG-1/2 video elementary streams. Every body byte is chosen non-zero unless a test is
  /// deliberately exercising zero-byte stuffing, so no accidental 00 00 01 prefix is produced.
  /// </summary>
  private sealed class EsBuilder {

    public const int TopField = 1;
    public const int BottomField = 2;
    public const int FramePicture = 3;

    /// <summary>Start-code prefix plus value, then time_code, closed_gop, broken_link.</summary>
    public const int GopHeaderLength = 8;

    private readonly List<byte> _bytes = [];

    public int Length => this._bytes.Count;

    public byte[] ToArray() => this._bytes.ToArray();

    public void Raw(params byte[] values) => this._bytes.AddRange(values);

    public int StartCode(byte value) {
      var offset = this._bytes.Count;
      this._bytes.Add(0x00);
      this._bytes.Add(0x00);
      this._bytes.Add(0x01);
      this._bytes.Add(value);
      return offset;
    }

    public void SequenceHeader() {
      this.StartCode(Mpeg12VideoFrameParser.SequenceHeaderCode);
      // horizontal_size_value, vertical_size_value, aspect_ratio_information, frame_rate_code,
      // bit_rate_value, marker_bit, vbv_buffer_size_value, and both load_*_quantiser_matrix flags
      // cleared. The parser skips the body, so only its length and byte values matter here.
      this.Raw(0x16, 0x01, 0x20, 0x13, 0xFF, 0xFF, 0xE0, 0x18);
    }

    public void GopHeader(bool closedGop = true, bool brokenLink = false) {
      this.StartCode(Mpeg12VideoFrameParser.GroupStartCode);
      // time_code (25 bits), closed_gop (1), broken_link (1), then 5 unused bits.
      var flags = (byte)(0x80 | (closedGop ? 0x40 : 0x00) | (brokenLink ? 0x20 : 0x00));
      this.Raw(0x11, 0x22, 0x33, flags);
    }

    public void SequenceEnd() => this.StartCode(Mpeg12VideoFrameParser.SequenceEndCode);

    /// <summary>Emits a picture header and returns the offset of its start code.</summary>
    public int Picture(int temporalReference, int codingType) {
      var offset = this.StartCode(Mpeg12VideoFrameParser.PictureStartCode);
      // temporal_reference (10), picture_coding_type (3), vbv_delay (16).
      this._bytes.Add((byte)((temporalReference >> 2) & 0xFF));
      this._bytes.Add((byte)(((temporalReference & 0x03) << 6) | ((codingType & 0x07) << 3) | 0x07));
      this.Raw(0xFF, 0xF8);
      return offset;
    }

    public void PictureCodingExtension(int pictureStructure) {
      this.StartCode(Mpeg12VideoFrameParser.ExtensionStartCode);
      // extension_start_code_identifier (4) = 1000, four 4-bit f_code fields,
      // intra_dc_precision (2), picture_structure (2), then top_field_first and the rest.
      this.Raw(0x8F, 0xFF, (byte)(0xF0 | (pictureStructure & 0x03)), 0x81, 0xFF);
    }

    public void UserData(int length) {
      this.StartCode(0xB2);
      for (var i = 0; i < length; ++i)
        this._bytes.Add((byte)('a' + i % 26));
    }

    public void Slice(byte sliceNumber, int length) {
      this.StartCode(sliceNumber);
      for (var i = 0; i < length; ++i)
        this._bytes.Add((byte)(0x40 + i % 0x50));
    }
  }
}
