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
  /// <param name="integrityTimeoutMs">Timeout for integrity checks (default 2000ms).</param>
  public FormatProber(ValidationLevel maxLevel = ValidationLevel.Integrity, int integrityTimeoutMs = 2000) {
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
      try {
        stream.Seek(offset, SeekOrigin.Begin);
        var subStream = offset > 0 ? new SubStream(stream, offset, stream.Length - offset) : stream;
        using var cts = new CancellationTokenSource(_integrityTimeoutMs);
        var integrityResult = validator.ValidateIntegrity(subStream);
        MergeResult(integrityResult, ref confidence, ref health, ref highestLevel, issues, ref validEntries, ref totalEntries);
      } catch (Exception ex) {
        issues.Add(new ValidationIssue(ValidationLevel.Integrity, IssueSeverity.Warning,
          "INTEGRITY_EXCEPTION", $"Integrity validation threw: {ex.Message}"));
        if (health == FormatHealth.Unknown || health == FormatHealth.Perfect || health == FormatHealth.Good)
          health = FormatHealth.Degraded;
      }
    }

    return new ProbeResult(formatId, offset, confidence, health, highestLevel, issues, validEntries, totalEntries);
  }

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
