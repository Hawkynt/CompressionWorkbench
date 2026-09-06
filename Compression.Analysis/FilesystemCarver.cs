using Compression.Analysis.Scanning;
using Compression.Core.DiskImage;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Analysis;

/// <summary>
/// Filesystem-aware carver. Scans arbitrary binary data (raw SD-card dumps, firmware blobs,
/// broken disk images with trashed/tampered partition tables) for known filesystem superblocks.
/// Candidates are validated through the common filesystem-driver contract rather than requiring
/// an archive projection, and strong content evidence can survive a failed mount probe as a damaged
/// forensic candidate.
/// <para>
/// Complements <see cref="FileCarver"/> — that class carves individual files (PhotoRec-style);
/// this one identifies embedded filesystems so callers can recurse into intact candidates via
/// <see cref="FilesystemExtractor"/> while still reporting damaged ones for manual recovery.
/// </para>
/// </summary>
public sealed class FilesystemCarver {

  private const int WindowSize = 1 * 1024 * 1024;
  private const int WindowOverlap = 128 * 1024;
  private const int FilesystemPrefixProbeSize = 0x20000; // covers Btrfs superblock at 0x10020 and peers

  /// <summary>Behavioural knobs.</summary>
  public FsCarveOptions Options { get; init; } = new();

  /// <summary>Carves embedded filesystems out of a readable, seekable stream.</summary>
  public IReadOnlyList<CarvedFilesystem> CarveStream(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
    if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));

    FormatRegistration.EnsureInitialized();

    var length = stream.Length;
    if (length <= 0) return [];

    var formatFilter = this.Options.FormatIds is { Count: > 0 } f
      ? new HashSet<string>(f, StringComparer.OrdinalIgnoreCase)
      : null;

    var fsDescriptors = FormatRegistry.FilesystemFormatIds
      .Select(FormatRegistry.GetById)
      .OfType<IFormatDescriptor>()
      .Where(d => formatFilter is null || formatFilter.Contains(d.Id))
      .ToArray();
    if (fsDescriptors.Length == 0) return [];

    var descriptorsById = fsDescriptors.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
    var allowedIds = descriptorsById.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var results = new List<CarvedFilesystem>();
    var seen = new HashSet<(long Offset, string Id)>();

    // A valid partition table is useful evidence but not required. At known partition starts we can
    // also try filesystems that have no safe raw-carving magic at all because the candidate count is
    // tiny compared with probing every sector of the whole image.
    if (this.Options.DescendIntoPartitionTables) {
      try {
        var detection = PartitionTableDetector.Detect(stream);
        foreach (var part in detection.Partitions) {
          if (results.Count >= this.Options.MaxHits) break;
          if (part.StartOffset < 0 || part.StartOffset >= length) continue;

          var available = Math.Min(part.Size, length - part.StartOffset);
          if (available <= 0) continue;

          foreach (var candidate in ProbeKnownStart(stream, part.StartOffset, available, fsDescriptors)) {
            if (seen.Add((candidate.ByteOffset, candidate.FormatId)))
              results.Add(candidate);
            if (results.Count >= this.Options.MaxHits) break;
          }
        }
      } catch {
        // A corrupt partition table is expected forensic input. The raw superblock scan below is
        // deliberately independent and still runs.
      }
    }

    // Raw superblock scan: SignatureScanner already converts non-zero signature offsets back to the
    // filesystem start (ext, APFS, Btrfs, FAT label fallback, ...), so partition metadata is irrelevant.
    var buffer = new byte[WindowSize];
    long windowStart = 0;
    var step = WindowSize - WindowOverlap;

    while (windowStart < length && results.Count < this.Options.MaxHits) {
      stream.Position = windowStart;
      var toRead = (int)Math.Min(buffer.Length, length - windowStart);
      var read = ReadExactlyOrEof(stream, buffer, 0, toRead);
      if (read <= 0) break;

      var scanResults = SignatureScanner.Scan(
        buffer.AsSpan(0, read),
        maxResults: 4000,
        headerProbeAlignment: 0);

      foreach (var hit in scanResults) {
        if (results.Count >= this.Options.MaxHits) break;
        if (hit.Confidence < this.Options.MinConfidence) continue;
        if (!allowedIds.Contains(hit.FormatName)) continue;

        var globalOffset = windowStart + hit.Offset;
        if (windowStart > 0 && hit.Offset < WindowOverlap) continue;
        if (globalOffset < 0 || globalOffset >= length) continue;
        if (!seen.Add((globalOffset, hit.FormatName))) continue;

        var desc = descriptorsById[hit.FormatName];
        var available = length - globalOffset;
        var candidate = ProbeOne(
          stream,
          globalOffset,
          available,
          desc,
          hit.Confidence,
          hasContentEvidence: true);
        if (candidate is not null)
          results.Add(candidate);
        else
          seen.Remove((globalOffset, hit.FormatName));
      }

      if (read < toRead) break;
      windowStart += step;
    }

    return results
      .OrderBy(r => r.ByteOffset)
      .ThenByDescending(r => r.DriverValidated)
      .ThenByDescending(r => r.Confidence)
      .ToList();
  }

  private IReadOnlyList<CarvedFilesystem> ProbeKnownStart(
    Stream stream,
    long offset,
    long available,
    IReadOnlyList<IFormatDescriptor> descriptors) {
    var result = new List<CarvedFilesystem>();
    var prefixLength = (int)Math.Min(FilesystemPrefixProbeSize, available);
    if (prefixLength <= 0) return result;

    var prefix = new byte[prefixLength];
    stream.Position = offset;
    var read = ReadExactlyOrEof(stream, prefix, 0, prefixLength);
    if (read <= 0) return result;
    var span = prefix.AsSpan(0, read);

    foreach (var desc in descriptors) {
      var bestEvidence = BestSignatureConfidence(desc.Id, span);

      // No fixed signature is not a reason to exclude a filesystem at a known partition boundary:
      // let its native/derived driver decide. A successful probe is itself structural evidence.
      var seedConfidence = bestEvidence > 0 ? bestEvidence : this.Options.KnownBoundaryProbeConfidence;
      if (seedConfidence < this.Options.MinConfidence && bestEvidence > 0) continue;

      var candidate = ProbeOne(
        stream,
        offset,
        available,
        desc,
        seedConfidence,
        hasContentEvidence: bestEvidence > 0);
      if (candidate is not null)
        result.Add(candidate);
    }

    return result;
  }

  private static double BestSignatureConfidence(string formatId, ReadOnlySpan<byte> prefix) {
    var best = 0d;
    foreach (var entry in SignatureDatabase.Entries) {
      if (!string.Equals(entry.FormatName, formatId, StringComparison.OrdinalIgnoreCase)) continue;
      if (!MatchesAt(prefix, entry.Offset, entry.Magic, entry.Mask)) continue;
      best = Math.Max(best, entry.Confidence);
    }
    return best;
  }

  private CarvedFilesystem? ProbeOne(
    Stream stream,
    long offset,
    long available,
    IFormatDescriptor desc,
    double confidence,
    bool hasContentEvidence) {
    if (available <= 0) return null;

    var sub = new SubStream(stream, offset, available);
    try {
      var profile = FormatRegistry.ProbeFilesystem(desc.Id, sub, password: null);
      if (!profile.CanMount || !ResolvesAVolume(desc, sub, out var estimatedSize))
        return Damaged(offset, desc, confidence, hasContentEvidence, profile.ProfileName, profile.Limitations);

      return new CarvedFilesystem(
        ByteOffset: offset,
        FormatId: desc.Id,
        Confidence: Math.Min(1, Math.Max(confidence, this.Options.ValidatedConfidenceFloor)),
        EstimatedSize: estimatedSize,
        DriverValidated: true,
        CanMount: true,
        ProfileName: profile.ProfileName,
        Limitations: profile.Limitations);
    } catch (Exception error) when (hasContentEvidence && this.Options.KeepDamagedCandidates) {
      // A parser failing after a strong superblock/signature hit is evidence of damage, not evidence
      // that the signature vanished. Keep it visible at reduced confidence for forensic workflows.
      return Damaged(
        offset,
        desc,
        confidence,
        hasContentEvidence: true,
        profileName: null,
        limitations: [$"Driver probe failed: {error.GetType().Name}: {error.Message}"]);
    } catch {
      // Without independent content evidence (e.g. trying every driver at a partition boundary), a
      // failed probe is just a non-match and must not become a false positive.
      return null;
    }
  }

  /// <summary>
  /// Builds the reduced-confidence record for a candidate whose driver would not resolve a volume,
  /// or <c>null</c> when it carries too little evidence to be worth reporting.
  /// </summary>
  private CarvedFilesystem? Damaged(
    long offset,
    IFormatDescriptor desc,
    double confidence,
    bool hasContentEvidence,
    string? profileName,
    IReadOnlyList<string>? limitations) {
    if (!hasContentEvidence || !this.Options.KeepDamagedCandidates)
      return null;

    // A weak magic that no driver could corroborate is noise, not a damaged volume. Holding damaged
    // candidates to the same floor as scan hits keeps a single stray byte value from turning every
    // one of its thousands of occurrences in arbitrary data into a reported filesystem.
    var degraded = confidence * this.Options.DamagedConfidenceFactor;
    if (degraded < this.Options.MinConfidence)
      return null;

    return new CarvedFilesystem(
      ByteOffset: offset,
      FormatId: desc.Id,
      Confidence: degraded,
      EstimatedSize: 0,
      DriverValidated: false,
      CanMount: false,
      ProfileName: profileName,
      Limitations: limitations);
  }

  /// <summary>
  /// Confirms that a mountable probe actually resolved a filesystem at this offset, and reports the
  /// summed size of its entries.
  /// </summary>
  /// <remarks>
  /// A format with its own filesystem driver has already proven this by probing. A format that is
  /// only projected through its archive view has not: that projection reports success whenever
  /// listing merely fails to throw, so an empty listing would promote every weak magic hit in
  /// arbitrary data to a driver-validated volume.
  /// </remarks>
  private static bool ResolvesAVolume(IFormatDescriptor desc, Stream image, out long estimatedSize) {
    // The listing runs either way so a native driver keeps its size estimate; only the verdict
    // differs. A driver of its own has already proven the volume, so an archive view that declines
    // to enumerate cannot overrule it.
    var enumerated = TryListEntries(desc, image, out estimatedSize);
    return enumerated
      || desc is IFilesystemDriverProvider
      || FormatRegistry.GetFilesystemDriver(desc.Id) is not null;
  }

  private static bool TryListEntries(IFormatDescriptor desc, Stream image, out long estimatedSize) {
    estimatedSize = 0;
    if (desc is not IArchiveFormatOperations archiveOps || !image.CanSeek)
      return false;

    try {
      image.Position = 0;
      var entries = archiveOps.List(image, password: null);
      if (entries is not { Count: > 0 })
        return false;

      var total = 0L;
      foreach (var entry in entries) {
        if (entry.IsDirectory || entry.OriginalSize <= 0) continue;
        checked { total += entry.OriginalSize; }
      }
      estimatedSize = total;
      return true;
    } catch (OverflowException) {
      estimatedSize = long.MaxValue;
      return true;
    } catch {
      return false;
    }
  }

  private static bool MatchesAt(ReadOnlySpan<byte> span, int offset, byte[] pattern, byte[]? mask) {
    if (offset < 0 || pattern.Length == 0 || offset > span.Length - pattern.Length) return false;
    if (mask is null) return span.Slice(offset, pattern.Length).SequenceEqual(pattern);
    if (mask.Length != pattern.Length) return false;

    for (var i = 0; i < pattern.Length; ++i)
      if ((span[offset + i] & mask[i]) != (pattern[i] & mask[i]))
        return false;
    return true;
  }

  private static int ReadExactlyOrEof(Stream stream, byte[] buffer, int offset, int count) {
    var total = 0;
    while (total < count) {
      var read = stream.Read(buffer, offset + total, count - total);
      if (read <= 0) break;
      total += read;
    }
    return total;
  }
}

/// <summary>Knobs controlling <see cref="FilesystemCarver.CarveStream"/>.</summary>
public sealed record FsCarveOptions {
  /// <summary>Scanner-confidence floor (0..1). Hits below are dropped.</summary>
  public double MinConfidence { get; init; } = 0.5;

  /// <summary>Restrict to specific FS format IDs (null = all registered filesystem descriptors).</summary>
  public IReadOnlyList<string>? FormatIds { get; init; }

  /// <summary>Honour MBR/GPT partition tables when present and probe every partition start.</summary>
  public bool DescendIntoPartitionTables { get; init; } = true;

  /// <summary>Keep signature-backed candidates whose filesystem driver rejects the damaged image.</summary>
  public bool KeepDamagedCandidates { get; init; } = true;

  /// <summary>Confidence multiplier when strong content evidence exists but structural probing fails.</summary>
  public double DamagedConfidenceFactor { get; init; } = 0.75;

  /// <summary>Initial confidence for a driver-only probe at a known partition boundary.</summary>
  public double KnownBoundaryProbeConfidence { get; init; } = 0.60;

  /// <summary>Minimum confidence assigned after a filesystem driver successfully validates a candidate.</summary>
  public double ValidatedConfidenceFloor { get; init; } = 0.90;

  /// <summary>Safety cap on total hits returned.</summary>
  public int MaxHits { get; init; } = 256;
}

/// <summary>One carved filesystem located inside the host stream.</summary>
public sealed record CarvedFilesystem(
  long ByteOffset,
  string FormatId,
  double Confidence,
  long EstimatedSize,
  bool DriverValidated = false,
  bool CanMount = false,
  string? ProfileName = null,
  IReadOnlyList<string>? Limitations = null
);
