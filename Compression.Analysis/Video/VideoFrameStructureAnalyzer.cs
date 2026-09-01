namespace Compression.Analysis.Video;

/// <summary>Canonical picture kinds used by codec-neutral frame-structure analysis.</summary>
public enum VideoFrameKind {
  Unknown,
  I,
  P,
  B,
  S,
  SI,
  SP,
  Other,
}

/// <summary>
/// Minimal coded-picture metadata required for GOP and temporal-structure analysis.
/// Decode and presentation indices are deliberately separate because codecs with B pictures
/// commonly transmit pictures in a different order from the order in which they are shown.
/// </summary>
public readonly record struct VideoFrameSample(
  int DecodeIndex,
  int PresentationIndex,
  VideoFrameKind Kind,
  int SizeBytes,
  long Offset = 0,
  TimeSpan? DecodeTimestamp = null,
  TimeSpan? PresentationTimestamp = null,
  bool IsRandomAccess = false,
  bool IsReference = false,
  bool IsCorrupt = false);

/// <summary>Frame-count distances between successive pictures selected by one criterion.</summary>
public readonly record struct VideoFrameDistanceStatistics(
  int SampleCount,
  int? MinimumFrames,
  int? MaximumFrames,
  double? MeanFrames,
  double? MedianFrames);

/// <summary>Presentation-time distances between successive pictures selected by one criterion.</summary>
public readonly record struct VideoTimeDistanceStatistics(
  int SampleCount,
  TimeSpan? Minimum,
  TimeSpan? Maximum,
  TimeSpan? Mean,
  TimeSpan? Median);

/// <summary>Combined frame-position and presentation-time spacing.</summary>
public readonly record struct VideoSpacingStatistics(
  VideoFrameDistanceStatistics Frames,
  VideoTimeDistanceStatistics Time);

/// <summary>Per-picture-kind population and coded-size statistics.</summary>
public readonly record struct VideoFrameKindStatistics(
  VideoFrameKind Kind,
  int Count,
  long TotalBytes,
  int? MinimumSizeBytes,
  int? MaximumSizeBytes,
  double? MeanSizeBytes,
  double? MedianSizeBytes);

/// <summary>A compact GOP/picture pattern and the number of times it occurs.</summary>
public readonly record struct VideoGopPattern(string Pattern, int Count);

/// <summary>Codec-neutral temporal-structure report for one video stream.</summary>
public sealed record VideoFrameStructureReport(
  int FrameCount,
  IReadOnlyList<VideoFrameKindStatistics> FrameKinds,
  VideoSpacingStatistics IToI,
  VideoSpacingStatistics PToP,
  VideoSpacingStatistics BToB,
  VideoSpacingStatistics RandomAccessToRandomAccess,
  int MaxConsecutiveBFrames,
  int MaxReorderDepthFrames,
  int IntraWithoutRandomAccessCount,
  int RandomAccessNonIntraCount,
  IReadOnlyList<VideoGopPattern> GopPatterns);

/// <summary>
/// Computes GSpot-style GOP/frame statistics from codec-independent frame metadata.
/// No pixel reconstruction is required; elementary-stream parsers only need to expose
/// picture type/order/timestamps and coded sizes.
/// </summary>
public static class VideoFrameStructureAnalyzer {

  /// <summary>Analyzes a sequence of coded-picture metadata.</summary>
  public static VideoFrameStructureReport Analyze(IEnumerable<VideoFrameSample> frames) {
    ArgumentNullException.ThrowIfNull(frames);

    var source = frames.ToArray();
    Validate(source);

    var presentationOrder = source
      .OrderBy(static frame => frame.PresentationIndex)
      .ThenBy(static frame => frame.DecodeIndex)
      .ToArray();

    var kindStatistics = Enum.GetValues<VideoFrameKind>()
      .Select(kind => BuildKindStatistics(presentationOrder, kind))
      .Where(static statistics => statistics.Count > 0)
      .ToArray();

    return new VideoFrameStructureReport(
      FrameCount: source.Length,
      FrameKinds: kindStatistics,
      IToI: BuildSpacing(presentationOrder, static frame => frame.Kind == VideoFrameKind.I),
      PToP: BuildSpacing(presentationOrder, static frame => frame.Kind == VideoFrameKind.P),
      BToB: BuildSpacing(presentationOrder, static frame => frame.Kind == VideoFrameKind.B),
      RandomAccessToRandomAccess: BuildSpacing(presentationOrder, static frame => frame.IsRandomAccess),
      MaxConsecutiveBFrames: FindMaxConsecutiveBFrames(presentationOrder),
      MaxReorderDepthFrames: FindMaxReorderDepth(source),
      IntraWithoutRandomAccessCount: source.Count(static frame => IsIntra(frame.Kind) && !frame.IsRandomAccess),
      RandomAccessNonIntraCount: source.Count(static frame => frame.IsRandomAccess && !IsIntra(frame.Kind)),
      GopPatterns: BuildGopPatterns(presentationOrder));
  }

  private static void Validate(ReadOnlySpan<VideoFrameSample> frames) {
    var decodeIndices = new HashSet<int>();
    var presentationIndices = new HashSet<int>();

    foreach (var frame in frames) {
      if (frame.DecodeIndex < 0)
        throw new ArgumentOutOfRangeException(nameof(frames), "Decode indices must be non-negative.");
      if (frame.PresentationIndex < 0)
        throw new ArgumentOutOfRangeException(nameof(frames), "Presentation indices must be non-negative.");
      if (frame.SizeBytes < 0)
        throw new ArgumentOutOfRangeException(nameof(frames), "Coded frame sizes must be non-negative.");
      if (!decodeIndices.Add(frame.DecodeIndex))
        throw new ArgumentException($"Duplicate decode index {frame.DecodeIndex}.", nameof(frames));
      if (!presentationIndices.Add(frame.PresentationIndex))
        throw new ArgumentException($"Duplicate presentation index {frame.PresentationIndex}.", nameof(frames));
    }
  }

  private static VideoFrameKindStatistics BuildKindStatistics(
    IReadOnlyList<VideoFrameSample> frames,
    VideoFrameKind kind) {
    var sizes = frames
      .Where(frame => frame.Kind == kind)
      .Select(static frame => frame.SizeBytes)
      .Order()
      .ToArray();

    if (sizes.Length == 0)
      return new VideoFrameKindStatistics(kind, 0, 0, null, null, null, null);

    return new VideoFrameKindStatistics(
      Kind: kind,
      Count: sizes.Length,
      TotalBytes: sizes.Sum(static size => (long)size),
      MinimumSizeBytes: sizes[0],
      MaximumSizeBytes: sizes[^1],
      MeanSizeBytes: sizes.Average(),
      MedianSizeBytes: Median(sizes));
  }

  private static VideoSpacingStatistics BuildSpacing(
    IReadOnlyList<VideoFrameSample> presentationOrder,
    Func<VideoFrameSample, bool> predicate) {
    var selected = presentationOrder.Where(predicate).ToArray();
    if (selected.Length < 2)
      return new VideoSpacingStatistics(
        new VideoFrameDistanceStatistics(0, null, null, null, null),
        new VideoTimeDistanceStatistics(0, null, null, null, null));

    var frameDistances = new int[selected.Length - 1];
    var timeDistances = new List<long>(selected.Length - 1);

    for (var i = 1; i < selected.Length; ++i) {
      var previous = selected[i - 1];
      var current = selected[i];
      frameDistances[i - 1] = current.PresentationIndex - previous.PresentationIndex;

      if (previous.PresentationTimestamp is { } previousTimestamp &&
          current.PresentationTimestamp is { } currentTimestamp)
        timeDistances.Add((currentTimestamp - previousTimestamp).Ticks);
    }

    Array.Sort(frameDistances);
    timeDistances.Sort();

    var frameStatistics = new VideoFrameDistanceStatistics(
      SampleCount: frameDistances.Length,
      MinimumFrames: frameDistances[0],
      MaximumFrames: frameDistances[^1],
      MeanFrames: frameDistances.Average(),
      MedianFrames: Median(frameDistances));

    var timeStatistics = timeDistances.Count == 0
      ? new VideoTimeDistanceStatistics(0, null, null, null, null)
      : new VideoTimeDistanceStatistics(
        SampleCount: timeDistances.Count,
        Minimum: TimeSpan.FromTicks(timeDistances[0]),
        Maximum: TimeSpan.FromTicks(timeDistances[^1]),
        Mean: TimeSpan.FromTicks((long)Math.Round(timeDistances.Average(static ticks => (double)ticks))),
        Median: TimeSpan.FromTicks((long)Math.Round(Median(timeDistances))));

    return new VideoSpacingStatistics(frameStatistics, timeStatistics);
  }

  private static int FindMaxConsecutiveBFrames(IEnumerable<VideoFrameSample> presentationOrder) {
    var current = 0;
    var maximum = 0;

    foreach (var frame in presentationOrder) {
      if (frame.Kind == VideoFrameKind.B) {
        ++current;
        maximum = Math.Max(maximum, current);
      } else {
        current = 0;
      }
    }

    return maximum;
  }

  private static int FindMaxReorderDepth(IEnumerable<VideoFrameSample> frames) =>
    frames.Select(static frame => Math.Abs(frame.DecodeIndex - frame.PresentationIndex)).DefaultIfEmpty().Max();

  private static IReadOnlyList<VideoGopPattern> BuildGopPatterns(IReadOnlyList<VideoFrameSample> presentationOrder) {
    if (presentationOrder.Count == 0)
      return [];

    var useExplicitRandomAccess = presentationOrder.Any(static frame => frame.IsRandomAccess);
    var starts = presentationOrder
      .Select((frame, index) => (frame, index))
      .Where(item => useExplicitRandomAccess ? item.frame.IsRandomAccess : IsIntra(item.frame.Kind))
      .Select(static item => item.index)
      .ToArray();

    if (starts.Length == 0)
      return [];

    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
    for (var i = 0; i < starts.Length; ++i) {
      var start = starts[i];
      var end = i + 1 < starts.Length ? starts[i + 1] : presentationOrder.Count;
      var pattern = string.Concat(presentationOrder.Skip(start).Take(end - start).Select(static frame => KindSymbol(frame.Kind)));
      counts[pattern] = counts.GetValueOrDefault(pattern) + 1;
    }

    return counts
      .OrderByDescending(static pair => pair.Value)
      .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
      .Select(static pair => new VideoGopPattern(pair.Key, pair.Value))
      .ToArray();
  }

  private static bool IsIntra(VideoFrameKind kind) => kind is VideoFrameKind.I or VideoFrameKind.SI;

  private static char KindSymbol(VideoFrameKind kind) => kind switch {
    VideoFrameKind.I => 'I',
    VideoFrameKind.P => 'P',
    VideoFrameKind.B => 'B',
    VideoFrameKind.S => 'S',
    VideoFrameKind.SI => 'i',
    VideoFrameKind.SP => 'p',
    VideoFrameKind.Other => 'O',
    _ => '?',
  };

  private static double Median(IReadOnlyList<int> sortedValues) {
    var middle = sortedValues.Count / 2;
    return (sortedValues.Count & 1) != 0
      ? sortedValues[middle]
      : ((double)sortedValues[middle - 1] + sortedValues[middle]) / 2;
  }

  private static double Median(IReadOnlyList<long> sortedValues) {
    var middle = sortedValues.Count / 2;
    return (sortedValues.Count & 1) != 0
      ? sortedValues[middle]
      : sortedValues[middle - 1] / 2.0 + sortedValues[middle] / 2.0;
  }
}
