using Compression.Analysis.Scanning;

namespace Compression.Analysis;

/// <summary>
/// PhotoRec / foremost / scalpel-style file carver. Scans arbitrary binary data
/// (raw disk images, SD card dumps, partially corrupted firmware, unknown blobs)
/// for known content signatures and package-native header detectors.
/// <para>
/// Unlike <see cref="PayloadCarver"/>, this class is stream-aware: it walks the
/// input in a sliding 1 MB window so multi-gigabyte images don't have to be
/// materialised in memory. Only the subset of data covered by each hit is
/// loaded into a <see cref="CarvedFile"/> (and only when <see cref="CarveOptions.ExtractData"/>
/// is set).
/// </para>
/// </summary>
public sealed class FileCarver {

  private const int WindowSize = 1 * 1024 * 1024;      // 1 MB sliding window
  private const int WindowOverlap = 64 * 1024;         // 64 KB overlap for magics straddling boundaries

  /// <summary>Behavioural knobs for the carver.</summary>
  public CarveOptions Options { get; init; } = new();

  /// <summary>
  /// Carves files out of the provided stream. The stream must be seekable —
  /// carving requires random-access reads for length inference.
  /// </summary>
  public IReadOnlyList<CarvedFile> CarveStream(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    var length = stream.Length;
    if (length <= 0) return [];

    // Phase 1: scan in overlapping windows to find candidate hits. Fixed signatures are scanned
    // byte-granular; structure-only package detectors run at the configured alignment.
    var hits = new List<RawHit>();
    var buffer = new byte[WindowSize];
    long windowStart = 0;
    long step = WindowSize - WindowOverlap;
    if (step <= 0) step = WindowSize;

    while (windowStart < length) {
      stream.Position = windowStart;
      var toRead = (int)Math.Min(buffer.Length, length - windowStart);
      var read = ReadExactlyOrEof(stream, buffer, 0, toRead);
      if (read <= 0) break;

      var span = buffer.AsSpan(0, read);
      var scanResults = SignatureScanner.Scan(
        span,
        maxResults: 2000,
        headerProbeAlignment: this.Options.HeaderProbeAlignment);
      foreach (var r in scanResults) {
        if (r.Confidence < this.Options.MinConfidence) continue;
        var globalOffset = windowStart + r.Offset;
        // Skip hits inside the overlap region when they're already discovered
        // by the previous window (overlap region = first WindowOverlap bytes
        // of all windows except the very first).
        if (windowStart > 0 && r.Offset < WindowOverlap) continue;
        hits.Add(new RawHit(globalOffset, r.FormatName, r.Confidence));
      }

      if (read < toRead) break;
      windowStart += step;
    }

    if (hits.Count == 0) return [];

    // Dedupe (same offset + format can be hit by two overlapping scans or by both a descriptor
    // signature and a package-native detector). Keep the strongest evidence.
    hits = hits
      .GroupBy(h => (h.Offset, h.FormatId))
      .Select(g => g.OrderByDescending(x => x.Confidence).First())
      .OrderBy(h => h.Offset)
      .ToList();

    var formatFilter = this.Options.Formats is { Count: > 0 } f
      ? new HashSet<string>(f, StringComparer.OrdinalIgnoreCase)
      : null;

    // Phase 2: infer lengths. For each hit, read enough of the stream to let
    // PayloadLengthProbe do its job, then fall back to the next-hit distance.
    var allOffsets = hits.Select(h => h.Offset).OrderBy(o => o).ToArray();
    var carved = new List<CarvedFile>(hits.Count);
    foreach (var h in hits) {
      if (formatFilter != null && !formatFilter.Contains(h.FormatId)) continue;

      var probed = ProbeLength(stream, h.Offset, h.FormatId, length);
      var nextHit = NextGreater(allOffsets, h.Offset, length);
      var fallbackLen = nextHit - h.Offset;
      long carveLen;
      double confidence = h.Confidence;

      if (probed is { } pl) {
        carveLen = pl;
      } else {
        carveLen = fallbackLen;
        confidence *= 0.7;  // less certain without explicit end marker
      }
      // Cap by buffer end and options.
      if (carveLen <= 0) continue;
      if (h.Offset + carveLen > length) {
        carveLen = length - h.Offset;
        confidence *= 0.8;  // truncated at buffer boundary
      }
      if (carveLen > this.Options.MaxFileSize) carveLen = this.Options.MaxFileSize;
      if (carveLen < this.Options.MinFileSize) continue;

      byte[]? data = null;
      if (this.Options.ExtractData)
        data = ReadRange(stream, h.Offset, carveLen);

      carved.Add(new CarvedFile(
        Offset: h.Offset,
        Length: carveLen,
        FormatId: h.FormatId,
        Extension: DefaultExtension(h.FormatId),
        Confidence: confidence,
        Data: data));
    }

    // Phase 3: optional de-overlap. Preserve the outermost file in any
    // wholly-contained pair (e.g. a JPEG thumbnail embedded inside a larger JPEG).
    if (this.Options.SkipOverlapping)
      carved = DeduplicateOverlapping(carved);

    return carved;
  }

  /// <summary>Convenience overload for in-memory buffers.</summary>
  public IReadOnlyList<CarvedFile> CarveBuffer(ReadOnlySpan<byte> buffer) {
    var tmp = buffer.ToArray();
    using var ms = new MemoryStream(tmp, writable: false);
    return CarveStream(ms);
  }

  // ── helpers ────────────────────────────────────────────────────────

  private readonly record struct RawHit(long Offset, string FormatId, double Confidence);

  private static long? ProbeLength(Stream stream, long offset, string formatId, long streamLength) {
    // Read a bounded window around the hit — enough to find an end marker for
    // most formats without pulling in a whole 500 MB file.
    const int probeWindow = 8 * 1024 * 1024;
    var windowSize = (int)Math.Min(probeWindow, streamLength - offset);
    if (windowSize <= 0) return null;
    var buf = new byte[windowSize];
    stream.Position = offset;
    var read = ReadExactlyOrEof(stream, buf, 0, windowSize);
    if (read <= 0) return null;
    // PayloadLengthProbe expects offset relative to the buffer start.
    return PayloadLengthProbe.TryProbe(buf.AsSpan(0, read), 0, formatId);
  }

  private static long NextGreater(long[] sortedOffsets, long offset, long fallback) {
    var lo = 0;
    var hi = sortedOffsets.Length;
    while (lo < hi) {
      var mid = (lo + hi) >>> 1;
      if (sortedOffsets[mid] > offset) hi = mid; else lo = mid + 1;
    }
    return lo < sortedOffsets.Length ? sortedOffsets[lo] : fallback;
  }

  private static byte[] ReadRange(Stream stream, long offset, long length) {
    var buf = new byte[length];
    stream.Position = offset;
    var total = 0;
    while (total < length) {
      var r = stream.Read(buf, total, (int)(length - total));
      if (r <= 0) break;
      total += r;
    }
    if (total < length) Array.Resize(ref buf, total);
    return buf;
  }

  private static int ReadExactlyOrEof(Stream stream, byte[] buf, int offset, int count) {
    var total = 0;
    while (total < count) {
      var r = stream.Read(buf, offset + total, count - total);
      if (r <= 0) break;
      total += r;
    }
    return total;
  }

  private static List<CarvedFile> DeduplicateOverlapping(List<CarvedFile> raw) {
    // Sort by offset ascending, then length descending. Walk and drop any hit
    // wholly contained inside an earlier (outer) one.
    var sorted = raw
      .OrderBy(c => c.Offset)
      .ThenByDescending(c => c.Length)
      .ToList();
    var kept = new List<CarvedFile>(sorted.Count);
    foreach (var c in sorted) {
      var containedBy = kept.FirstOrDefault(k =>
        k.Offset <= c.Offset && k.Offset + k.Length >= c.Offset + c.Length);
      if (containedBy is null) kept.Add(c);
    }
    return kept;
  }

  /// <summary>Canonical extension from the central descriptor/package detection metadata.</summary>
  internal static string DefaultExtension(string formatId)
    => SignatureDatabase.GetDefaultExtension(formatId);
}

/// <summary>
/// Knobs controlling <see cref="FileCarver.CarveStream"/>.
/// </summary>
/// <remarks>
/// Defaults are tuned for disk-image recovery: skip very small fragments
/// (likely false positives), cap single-file carves at 500 MB, extract data
/// only when explicitly requested so scans stay fast.
/// </remarks>
public sealed record CarveOptions {
  /// <summary>Skip carved files smaller than this (bytes).</summary>
  public int MinFileSize { get; init; } = 128;
  /// <summary>Safety cap per carved file (bytes).</summary>
  public int MaxFileSize { get; init; } = 512_000_000;
  /// <summary>Scanner-confidence floor (0..1).</summary>
  public double MinConfidence { get; init; } = 0.5;
  /// <summary>Drop inner files that fall wholly inside an outer file.</summary>
  public bool SkipOverlapping { get; init; } = true;
  /// <summary>Restrict to specific format IDs (null = all registered/package formats).</summary>
  public IReadOnlyList<string>? Formats { get; init; }
  /// <summary>Populate <see cref="CarvedFile.Data"/>. Keep <c>false</c> for pure scanning.</summary>
  public bool ExtractData { get; init; }
  /// <summary>
  /// Alignment for package-native structure-only header probes. 512 matches common sectors and is
  /// the forensic default; 1 is exhaustive but deliberately expensive; 0 probes only offset zero.
  /// </summary>
  public int HeaderProbeAlignment { get; init; } = 512;
}

/// <summary>
/// One carved payload. <see cref="Data"/> is only populated when the carver
/// was run with <see cref="CarveOptions.ExtractData"/> = true.
/// </summary>
public sealed record CarvedFile(
  long Offset,
  long Length,
  string FormatId,
  string Extension,
  double Confidence,
  byte[]? Data);
