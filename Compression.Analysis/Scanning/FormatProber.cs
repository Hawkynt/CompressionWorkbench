using Compression.Registry;

namespace Compression.Analysis.Scanning;

/// <summary>
/// Deep-probes format candidates beyond magic byte matching.
/// Takes signature scan results and progressively validates each candidate
/// through header, structure, and integrity checks using <see cref="IFormatValidator"/>.
/// </summary>
public sealed class FormatProber {

  private readonly ValidationLevel _maxLevel;
  private readonly int _integrityTimeoutMs;

  /// <param name="maxLevel">Deepest validation level to attempt.</param>
  /// <param name="integrityTimeoutMs">
  /// Time budget for the integrity check of one candidate (default 2000 ms), or
  /// <see cref="Timeout.Infinite"/> for none. The check reads its input through a stream that
  /// stops serving data once the budget is spent; the candidate is then reported as not
  /// integrity-checked (<c>INTEGRITY_TIMEOUT</c>) rather than damaged.
  /// </param>
  /// <exception cref="ArgumentOutOfRangeException">The budget is negative and not <see cref="Timeout.Infinite"/>.</exception>
  public FormatProber(ValidationLevel maxLevel = ValidationLevel.Integrity, int integrityTimeoutMs = 2000) {
    if (integrityTimeoutMs < 0 && integrityTimeoutMs != Timeout.Infinite)
      throw new ArgumentOutOfRangeException(nameof(integrityTimeoutMs), integrityTimeoutMs, "Must be non-negative or Timeout.Infinite.");
    _maxLevel = maxLevel;
    _integrityTimeoutMs = integrityTimeoutMs;
  }

  /// <summary>
  /// Probes all scan results, running progressive validation on each candidate.
  /// Returns results sorted by confidence descending.
  /// </summary>
  public List<ProbeResult> Probe(ReadOnlySpan<byte> data, IReadOnlyList<ScanResult> scanResults) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var results = new List<ProbeResult>();

    foreach (var scan in scanResults) {
      var desc = FormatRegistry.GetById(scan.FormatName);
      if (desc is not IFormatValidator validator) {
        // No validator — pass through with magic-only confidence
        results.Add(new ProbeResult(scan.FormatName, scan.Offset, scan.Confidence,
          FormatHealth.Unknown, ValidationLevel.Magic, []));
        continue;
      }

      var probe = ProbeCandidate(data, scan, validator);
      results.Add(probe);
    }

    results.Sort((a, b) => {
      var c = b.Confidence.CompareTo(a.Confidence);
      return c != 0 ? c : a.Offset.CompareTo(b.Offset);
    });

    return results;
  }

  /// <summary>
  /// Probes a single format at a specific offset in the given stream.
  /// </summary>
  public ProbeResult? ProbeFormat(Stream stream, string formatId, long offset = 0) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var desc = FormatRegistry.GetById(formatId);
    if (desc is not IFormatValidator validator) return null;

    var issues = new List<ValidationIssue>();
    var confidence = 0.0;
    var health = FormatHealth.Unknown;
    var highestLevel = ValidationLevel.Magic;
    int? validEntries = null;
    int? totalEntries = null;

    // Check magic from descriptor signatures
    foreach (var sig in desc.MagicSignatures) {
      confidence = Math.Max(confidence, sig.Confidence);
    }

    // Level 1: Header validation
    if (_maxLevel >= ValidationLevel.Header && stream.CanSeek) {
      stream.Seek(offset, SeekOrigin.Begin);
      var headerBuf = new byte[(int)Math.Min(4096, stream.Length - offset)];
      var read = stream.Read(headerBuf, 0, headerBuf.Length);
      var headerResult = validator.ValidateHeader(headerBuf.AsSpan(0, read), stream.Length - offset);
      MergeResult(headerResult, ref confidence, ref health, ref highestLevel, issues, ref validEntries, ref totalEntries);

      if (!headerResult.IsValid)
        return new ProbeResult(formatId, offset, confidence, health, highestLevel, issues, validEntries, totalEntries);
    }

    // Level 2: Structure validation
    if (_maxLevel >= ValidationLevel.Structure && stream.CanSeek) {
      try {
        stream.Seek(offset, SeekOrigin.Begin);
        var subStream = offset > 0 ? new SubStream(stream, offset, stream.Length - offset) : stream;
        var structResult = validator.ValidateStructure(subStream);
        MergeResult(structResult, ref confidence, ref health, ref highestLevel, issues, ref validEntries, ref totalEntries);

        if (!structResult.IsValid)
          return new ProbeResult(formatId, offset, confidence, health, highestLevel, issues, validEntries, totalEntries);
      } catch (Exception ex) {
        issues.Add(new ValidationIssue(ValidationLevel.Structure, IssueSeverity.Error,
          "STRUCTURE_EXCEPTION", $"Structure validation threw: {ex.Message}"));
        health = FormatHealth.Damaged;
      }
    }

    // Level 3: Integrity validation
    if (_maxLevel >= ValidationLevel.Integrity && stream.CanSeek) {
      DeadlineStream? budgeted = null;
      try {
        stream.Seek(offset, SeekOrigin.Begin);
        var subStream = offset > 0 ? new SubStream(stream, offset, stream.Length - offset) : stream;
        budgeted = new DeadlineStream(subStream, _integrityTimeoutMs);
        var integrityResult = validator.ValidateIntegrity(budgeted);
        // Validators turn a failing read into "damaged"; a spent budget says nothing about the data.
        if (budgeted.Expired)
          AddTimeoutIssue(issues, _integrityTimeoutMs);
        else
          MergeResult(integrityResult, ref confidence, ref health, ref highestLevel, issues, ref validEntries, ref totalEntries);
      } catch (Exception) when (budgeted is { Expired: true }) {
        AddTimeoutIssue(issues, _integrityTimeoutMs);
      } catch (Exception ex) {
        issues.Add(new ValidationIssue(ValidationLevel.Integrity, IssueSeverity.Warning,
          "INTEGRITY_EXCEPTION", $"Integrity validation threw: {ex.Message}"));
        if (health == FormatHealth.Unknown || health == FormatHealth.Perfect || health == FormatHealth.Good)
          health = FormatHealth.Degraded;
      }
    }

    return new ProbeResult(formatId, offset, confidence, health, highestLevel, issues, validEntries, totalEntries);
  }

  private static void AddTimeoutIssue(List<ValidationIssue> issues, int timeoutMs)
    => issues.Add(new ValidationIssue(ValidationLevel.Integrity, IssueSeverity.Info,
      "INTEGRITY_TIMEOUT", $"Integrity validation exceeded its {timeoutMs} ms budget and was abandoned."));

  private ProbeResult ProbeCandidate(ReadOnlySpan<byte> data, ScanResult scan, IFormatValidator validator) {
    var issues = new List<ValidationIssue>();
    var confidence = scan.Confidence;
    var health = FormatHealth.Unknown;
    var highestLevel = ValidationLevel.Magic;
    int? validEntries = null;
    int? totalEntries = null;

    // Level 1: Header validation
    if (_maxLevel >= ValidationLevel.Header) {
      var headerSlice = data.Length > (int)scan.Offset
        ? data[(int)scan.Offset..]
        : ReadOnlySpan<byte>.Empty;

      if (headerSlice.Length > 0) {
        var headerResult = validator.ValidateHeader(headerSlice, data.Length - scan.Offset);
        MergeResult(headerResult, ref confidence, ref health, ref highestLevel, issues, ref validEntries, ref totalEntries);

        if (!headerResult.IsValid)
          return new ProbeResult(scan.FormatName, scan.Offset, confidence, health, highestLevel, issues, validEntries, totalEntries);
      }
    }

    // Levels 2-3 require a stream — callers should use ProbeFormat() for those
    return new ProbeResult(scan.FormatName, scan.Offset, confidence, health, highestLevel, issues, validEntries, totalEntries);
  }

  private static void MergeResult(ValidationResult vr, ref double confidence, ref FormatHealth health,
      ref ValidationLevel highestLevel, List<ValidationIssue> issues,
      ref int? validEntries, ref int? totalEntries) {
    confidence = Math.Max(confidence, vr.Confidence);
    // Health: higher validation levels supersede lower ones since they're more definitive.
    // At the same level, take the worse health.
    if (health == FormatHealth.Unknown || vr.Level > highestLevel)
      health = vr.Health;
    else if (vr.Health > health)
      health = vr.Health;
    if (vr.Level > highestLevel)
      highestLevel = vr.Level;
    if (vr.ValidEntries.HasValue)
      validEntries = vr.ValidEntries;
    if (vr.TotalEntries.HasValue)
      totalEntries = vr.TotalEntries;
    issues.AddRange(vr.Issues);
  }

  /// <summary>
  /// Passes reads and seeks through until the time budget is spent, then fails every further
  /// read or seek with <see cref="TimeoutException"/>. Validators take no cancellation token, but
  /// all of them pull their input through the stream, so this bounds them without a thread.
  /// </summary>
  private sealed class DeadlineStream(Stream inner, int timeoutMs) : Stream {
    private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>True once an operation was refused because the budget was spent.</summary>
    public bool Expired { get; private set; }

    private void Check() {
      if (timeoutMs == Timeout.Infinite) return;
      if (this.Expired || System.Diagnostics.Stopwatch.GetElapsedTime(this._started).TotalMilliseconds >= timeoutMs) {
        this.Expired = true;
        throw new TimeoutException($"Integrity validation exceeded {timeoutMs} ms.");
      }
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position {
      get => inner.Position;
      set { this.Check(); inner.Position = value; }
    }
    public override int Read(byte[] buffer, int off, int count) { this.Check(); return inner.Read(buffer, off, count); }
    public override int Read(Span<byte> buffer) { this.Check(); return inner.Read(buffer); }
    public override int ReadByte() { this.Check(); return inner.ReadByte(); }
    public override long Seek(long off, SeekOrigin origin) { this.Check(); return inner.Seek(off, origin); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int off, int count) => throw new NotSupportedException();
    // The wrapped stream belongs to the caller.
    protected override void Dispose(bool disposing) { }
  }

  /// <summary>Wraps a region of a stream as a substream for offset-based probing.</summary>
  private sealed class SubStream(Stream inner, long offset, long length) : Stream {
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position {
      get => _position;
      set { _position = value; inner.Seek(offset + value, SeekOrigin.Begin); }
    }
    public override int Read(byte[] buffer, int off, int count) {
      var remaining = (int)Math.Min(count, length - _position);
      if (remaining <= 0) return 0;
      inner.Seek(offset + _position, SeekOrigin.Begin);
      var read = inner.Read(buffer, off, remaining);
      _position += read;
      return read;
    }
    public override long Seek(long off, SeekOrigin origin) {
      _position = origin switch {
        SeekOrigin.Begin => off,
        SeekOrigin.Current => _position + off,
        SeekOrigin.End => length + off,
        _ => _position
      };
      return _position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int off, int count) => throw new NotSupportedException();
  }
}
