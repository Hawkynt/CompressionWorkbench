#pragma warning disable CS1591
using Compression.Analysis.Video;

namespace Compression.Tests.Analysis;

[TestFixture]
public class VideoFrameStructureAnalyzerTests {

  [Test]
  public void H262StyleReorderedSequence_ComputesI2IP2PB2BAndGopPatterns() {
    // ITU-T H.262's canonical display-order example:
    //   display: I B B P B B P B B I B B P
    //   coded:   I P B B P B B I B B P B B
    // Keep both indices: using decode order for GOP spacing would be wrong.
    var frames = new[] {
      Frame(presentation: 0, decode: 0, VideoFrameKind.I, randomAccess: true),
      Frame(presentation: 1, decode: 2, VideoFrameKind.B),
      Frame(presentation: 2, decode: 3, VideoFrameKind.B),
      Frame(presentation: 3, decode: 1, VideoFrameKind.P, reference: true),
      Frame(presentation: 4, decode: 5, VideoFrameKind.B),
      Frame(presentation: 5, decode: 6, VideoFrameKind.B),
      Frame(presentation: 6, decode: 4, VideoFrameKind.P, reference: true),
      Frame(presentation: 7, decode: 8, VideoFrameKind.B),
      Frame(presentation: 8, decode: 9, VideoFrameKind.B),
      Frame(presentation: 9, decode: 7, VideoFrameKind.I, randomAccess: true),
      Frame(presentation: 10, decode: 11, VideoFrameKind.B),
      Frame(presentation: 11, decode: 12, VideoFrameKind.B),
      Frame(presentation: 12, decode: 10, VideoFrameKind.P, reference: true),
    };

    var report = VideoFrameStructureAnalyzer.Analyze(frames.Reverse());

    Assert.Multiple(() => {
      Assert.That(report.FrameCount, Is.EqualTo(13));
      Assert.That(report.IToI.Frames.SampleCount, Is.EqualTo(1));
      Assert.That(report.IToI.Frames.MinimumFrames, Is.EqualTo(9));
      Assert.That(report.IToI.Frames.MaximumFrames, Is.EqualTo(9));
      Assert.That(report.IToI.Time.Mean, Is.EqualTo(TimeSpan.FromMilliseconds(360)));

      Assert.That(report.PToP.Frames.SampleCount, Is.EqualTo(2));
      Assert.That(report.PToP.Frames.MinimumFrames, Is.EqualTo(3));
      Assert.That(report.PToP.Frames.MaximumFrames, Is.EqualTo(6));
      Assert.That(report.PToP.Frames.MeanFrames, Is.EqualTo(4.5));
      Assert.That(report.PToP.Frames.MedianFrames, Is.EqualTo(4.5));

      Assert.That(report.BToB.Frames.SampleCount, Is.EqualTo(7));
      Assert.That(report.BToB.Frames.MinimumFrames, Is.EqualTo(1));
      Assert.That(report.BToB.Frames.MaximumFrames, Is.EqualTo(2));
      Assert.That(report.MaxConsecutiveBFrames, Is.EqualTo(2));
      Assert.That(report.MaxReorderDepthFrames, Is.EqualTo(2));

      Assert.That(report.RandomAccessToRandomAccess.Frames.MinimumFrames, Is.EqualTo(9));
      Assert.That(report.IntraWithoutRandomAccessCount, Is.Zero);
      Assert.That(report.RandomAccessNonIntraCount, Is.Zero);
    });

    Assert.That(report.GopPatterns, Is.EquivalentTo(new[] {
      new VideoGopPattern("IBBPBBPBB", 1),
      new VideoGopPattern("IBBP", 1),
    }));
  }

  [Test]
  public void FrameKindStatistics_ReportCodedSizeDistribution() {
    var frames = new[] {
      Frame(0, 0, VideoFrameKind.I, sizeBytes: 1000, randomAccess: true),
      Frame(1, 1, VideoFrameKind.P, sizeBytes: 500),
      Frame(2, 2, VideoFrameKind.P, sizeBytes: 700),
      Frame(3, 3, VideoFrameKind.B, sizeBytes: 200),
    };

    var report = VideoFrameStructureAnalyzer.Analyze(frames);
    var p = report.FrameKinds.Single(item => item.Kind == VideoFrameKind.P);

    Assert.Multiple(() => {
      Assert.That(p.Count, Is.EqualTo(2));
      Assert.That(p.TotalBytes, Is.EqualTo(1200));
      Assert.That(p.MinimumSizeBytes, Is.EqualTo(500));
      Assert.That(p.MaximumSizeBytes, Is.EqualTo(700));
      Assert.That(p.MeanSizeBytes, Is.EqualTo(600));
      Assert.That(p.MedianSizeBytes, Is.EqualTo(600));
    });
  }

  [Test]
  public void ExplicitRandomAccess_IsNotAssumedEquivalentToIntraCoding() {
    var frames = new[] {
      Frame(0, 0, VideoFrameKind.I),
      Frame(1, 1, VideoFrameKind.P, randomAccess: true),
      Frame(2, 2, VideoFrameKind.B),
    };

    var report = VideoFrameStructureAnalyzer.Analyze(frames);

    Assert.Multiple(() => {
      Assert.That(report.IntraWithoutRandomAccessCount, Is.EqualTo(1));
      Assert.That(report.RandomAccessNonIntraCount, Is.EqualTo(1));
      Assert.That(report.GopPatterns, Is.EqualTo(new[] { new VideoGopPattern("PB", 1) }));
    });
  }

  [Test]
  public void MissingTimestamps_StillProducesFrameDistances() {
    var frames = new[] {
      new VideoFrameSample(0, 0, VideoFrameKind.I, 100, IsRandomAccess: true),
      new VideoFrameSample(1, 4, VideoFrameKind.I, 100, IsRandomAccess: true),
    };

    var report = VideoFrameStructureAnalyzer.Analyze(frames);

    Assert.Multiple(() => {
      Assert.That(report.IToI.Frames.MinimumFrames, Is.EqualTo(4));
      Assert.That(report.IToI.Time.SampleCount, Is.Zero);
      Assert.That(report.IToI.Time.Mean, Is.Null);
    });
  }

  [Test]
  public void DuplicatePresentationIndex_IsRejected() {
    var frames = new[] {
      new VideoFrameSample(0, 0, VideoFrameKind.I, 100),
      new VideoFrameSample(1, 0, VideoFrameKind.P, 100),
    };

    Assert.That(
      () => VideoFrameStructureAnalyzer.Analyze(frames),
      Throws.ArgumentException.With.Message.Contains("Duplicate presentation index"));
  }

  [Test]
  public void EmptySequence_ProducesEmptyReport() {
    var report = VideoFrameStructureAnalyzer.Analyze([]);

    Assert.Multiple(() => {
      Assert.That(report.FrameCount, Is.Zero);
      Assert.That(report.FrameKinds, Is.Empty);
      Assert.That(report.IToI.Frames.SampleCount, Is.Zero);
      Assert.That(report.GopPatterns, Is.Empty);
      Assert.That(report.MaxConsecutiveBFrames, Is.Zero);
      Assert.That(report.MaxReorderDepthFrames, Is.Zero);
    });
  }

  private static VideoFrameSample Frame(
    int presentation,
    int decode,
    VideoFrameKind kind,
    int sizeBytes = 300,
    bool randomAccess = false,
    bool reference = false) => new(
      DecodeIndex: decode,
      PresentationIndex: presentation,
      Kind: kind,
      SizeBytes: sizeBytes,
      Offset: presentation * 1000L,
      DecodeTimestamp: TimeSpan.FromMilliseconds(decode * 40),
      PresentationTimestamp: TimeSpan.FromMilliseconds(presentation * 40),
      IsRandomAccess: randomAccess,
      IsReference: reference);
}
