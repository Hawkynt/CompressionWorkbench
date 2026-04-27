using Compression.Analysis.Scanning;
using Compression.Core.DiskImage;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Analysis;

/// <summary>
/// Filesystem-aware carver. Scans arbitrary binary data (raw SD-card dumps,
/// firmware blobs, broken disk images with trashed partition tables) for
/// known filesystem superblock signatures and validates each candidate by
/// asking the matching <see cref="IArchiveFormatOperations.List"/> to walk it.
/// <para>
/// Complements <see cref="FileCarver"/> — that class carves individual files
/// (photorec-style); this one mounts embedded filesystems so callers can
/// recurse into them via <see cref="FilesystemExtractor"/> and recover entire
/// directory trees, not just loose payloads.
/// </para>
/// </summary>
/// <remarks>
/// Unlike <see cref="FileCarver"/>, this class does not require a valid
/// partition table to work: it finds the superblock directly via magic scan
/// at offset-aware positions (ext at +1080, FAT at +510, SquashFS at 0,
/// APFS at +32, Btrfs at +0x10020, etc.). When a partition table IS present
/// and <see cref="FsCarveOptions.DescendIntoPartitionTables"/> is on, the
/// carver first walks the MBR/GPT partitions and probes each; otherwise it
/// just sliding-window scans the whole stream.
/// </remarks>
public sealed class FilesystemCarver {

  private const int WindowSize = 1 * 1024 * 1024;      // 1 MB sliding window
  private const int WindowOverlap = 128 * 1024;        // 128 KB overlap (larger than FileCarver because FS magics can sit up to 0x10020 from header start)

  /// <summary>
  /// Extra magic signatures for filesystems whose descriptors deliberately
  /// leave <see cref="IFormatDescriptor.MagicSignatures"/> empty (usually
  /// because their boot signature is too generic — e.g. the FAT boot-sig
  /// 0x55 0xAA at offset 510 overlaps every MBR on earth). We scan for the
  /// FAT BS_FilSysType label strings which are much less ambiguous.
  /// <para>
  /// The magic is matched at <c>MagicOffset</c> within the FS and the FS
  /// is presumed to start at <c>MagicOffset - MagicOffset</c> (= absolute
  /// offset - MagicOffset). Matches are then validated by
  /// <see cref="IArchiveFormatOperations.List"/>.
  /// </para>
  /// </summary>
  private static readonly (string FormatId, byte[] Magic, int MagicOffset, double Confidence)[] BuiltinSignatures = [
    // FAT32 BS_FilSysType at offset 82 from FS start
    ("Fat", "FAT32   "u8.ToArray(), 82, 0.90),
    // FAT12/16 BS_FilSysType at offset 54
    ("Fat", "FAT12   "u8.ToArray(), 54, 0.90),
    ("Fat", "FAT16   "u8.ToArray(), 54, 0.90),
  ];

  /// <summary>Behavioural knobs.</summary>
  public FsCarveOptions Options { get; init; } = new();

  /// <summary>
  /// Carves embedded filesystems out of the provided stream.
  /// Stream must be seekable.
  /// </summary>
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

    // IDs of formats for which we carry a builtin signature (FAT today) —
    // these descriptors are intentionally empty on the magic-sig side
    // because their boot signature is too generic to add to the shared
    // registry, but we still want to include them here so the synthetic
    // scan can find them.
    var builtinFormatIds = new HashSet<string>(
      BuiltinSignatures.Select(s => s.FormatId),
      StringComparer.OrdinalIgnoreCase);

    // Only consider descriptors that (1) live in a FileSystem.* namespace and
    // (2) can actually list entries. A descriptor is scannable if it either
    // carries its own magic signatures OR is covered by our builtin table.
    var fsDescriptors = FormatRegistry.All
      .Where(d => IsFilesystemDescriptor(d))
      .Where(d => d is IArchiveFormatOperations)
      .Where(d => d.MagicSignatures.Count > 0 || builtinFormatIds.Contains(d.Id))
      .Where(d => formatFilter is null || formatFilter.Contains(d.Id))
      .ToList();

    if (fsDescriptors.Count == 0) return [];

    var allowedIds = new HashSet<string>(fsDescriptors.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);

    var results = new List<CarvedFilesystem>();
    var seenOffsets = new HashSet<(long Offset, string Id)>();

    // ── Partition-table descent ──────────────────────────────────────
    // When the caller opts in and an MBR/GPT is present, probe each
    // partition individually — that gives deterministic starts for
    // in-partition filesystems without relying on sliding-window luck.
    if (this.Options.DescendIntoPartitionTables) {
      try {
        var detection = PartitionTableDetector.Detect(stream);
        if (detection.Partitions.Count > 0) {
          foreach (var part in detection.Partitions) {
            if (part.StartOffset < 0 || part.StartOffset >= length) continue;
            if (results.Count >= this.Options.MaxHits) break;

            var partSize = Math.Min(part.Size, length - part.StartOffset);
            if (partSize <= 0) continue;

            var carved = TryValidateAt(stream, part.StartOffset, partSize, fsDescriptors, allowedIds);
            foreach (var c in carved) {
              if (seenOffsets.Add((c.ByteOffset, c.FormatId)))
                results.Add(c);
              if (results.Count >= this.Options.MaxHits) break;
            }
          }
        }
      } catch {
        // Partition-table parsing failure is fine — we still do the magic scan.
      }
    }

    // ── Magic-byte scan ───────────────────────────────────────────────
    // Slide a 1 MB window with 128 KB overlap so superblock magics that
    // straddle window boundaries are still found. We use SignatureScanner
    // which already honours per-descriptor offset (e.g. ext at +1080).
    var buffer = new byte[WindowSize];
    long windowStart = 0;
    long step = WindowSize - WindowOverlap;
    if (step <= 0) step = WindowSize;

    while (windowStart < length && results.Count < this.Options.MaxHits) {
      stream.Position = windowStart;
      var toRead = (int)Math.Min(buffer.Length, length - windowStart);
      var read = ReadExactlyOrEof(stream, buffer, 0, toRead);
      if (read <= 0) break;

      var span = buffer.AsSpan(0, read);
      var scanResults = SignatureScanner.Scan(span, maxResults: 2000);

      // Builtin scan for FS that don't carry magic signatures in their
      // descriptors (FAT especially). Appends synthetic ScanResults so the
      // normal validation path below applies.
      var synthetic = ScanBuiltins(span, windowStart, length, allowedIds);

      foreach (var sr in synthetic.Concat(scanResults)) {
        if (sr.Confidence < this.Options.MinConfidence) continue;
        if (!allowedIds.Contains(sr.FormatName)) continue;

        var globalOffset = windowStart + sr.Offset;
        // Skip hits in the overlap tail of all-but-first windows — the
        // previous window already emitted them.
        if (windowStart > 0 && sr.Offset < WindowOverlap && globalOffset < windowStart + WindowOverlap)
          continue;

        if (globalOffset < 0 || globalOffset >= length) continue;
        if (seenOffsets.Contains((globalOffset, sr.FormatName))) continue;

        var desc = fsDescriptors.FirstOrDefault(d => string.Equals(d.Id, sr.FormatName, StringComparison.OrdinalIgnoreCase));
        if (desc is null) continue;

        var available = length - globalOffset;
        var carved = TryValidateOne(stream, globalOffset, available, desc, sr.Confidence);
        if (carved is null) continue;

        if (seenOffsets.Add((carved.ByteOffset, carved.FormatId)))
          results.Add(carved);

        if (results.Count >= this.Options.MaxHits) break;
      }

      if (read < toRead) break;
      windowStart += step;
    }

    // Dedupe formats that both match the same offset (e.g. Fat12/Fat16
    // variants, HFS/HFS+ at the same volume header). Keep the highest
    // confidence per offset.
    return results
      .GroupBy(r => r.ByteOffset)
      .SelectMany(g => g.OrderByDescending(x => x.Confidence))
      .ToList();
  }

  /// <summary>
  /// Probe a single offset against all FS descriptors (used for partition
  /// table starts, where we know the FS header sits at offset 0 of the
  /// partition). Returns every descriptor that successfully validates.
  /// </summary>
  private IReadOnlyList<CarvedFilesystem> TryValidateAt(
    Stream stream,
    long offset,
    long available,
    IReadOnlyList<IFormatDescriptor> fsDescriptors,
    HashSet<string> allowedIds
  ) {
    var carved = new List<CarvedFilesystem>();

    // Read the first 64 KB of the candidate region for magic matching.
    // Most FS superblocks live in the first few KB; Btrfs at 0x10020 is the
    // outlier and needs a bigger prefix — honour that when the available
    // region is large.
    var prefixLen = (int)Math.Min(Math.Max(65536, 0x11000), available);
    if (prefixLen < 512) return carved;
    var prefix = new byte[prefixLen];
    stream.Position = offset;
    var read = ReadExactlyOrEof(stream, prefix, 0, prefixLen);
    if (read < 512) return carved;

    var span = prefix.AsSpan(0, read);
    foreach (var desc in fsDescriptors) {
      if (!allowedIds.Contains(desc.Id)) continue;

      // Any magic must match at its expected offset (relative to FS start).
      double bestConf = 0;
      foreach (var magic in desc.MagicSignatures) {
        if (magic.Offset + magic.Bytes.Length > span.Length) continue;
        if (!MatchesAt(span, magic.Offset, magic.Bytes)) continue;
        if (magic.Confidence > bestConf) bestConf = magic.Confidence;
      }

      // Fall back to builtin sigs for FS without descriptor-level magic (FAT).
      if (bestConf <= 0) {
        foreach (var (formatId, magic, magicOffset, conf) in BuiltinSignatures) {
          if (!string.Equals(formatId, desc.Id, StringComparison.OrdinalIgnoreCase)) continue;
          if (magicOffset + magic.Length > span.Length) continue;
          if (!MatchesAt(span, magicOffset, magic)) continue;
          if (conf > bestConf) bestConf = conf;
        }
      }

      if (bestConf < this.Options.MinConfidence) continue;

      var c = TryValidateOne(stream, offset, available, desc, bestConf);
      if (c is not null) carved.Add(c);
    }

    return carved;
  }

  /// <summary>
  /// Open a <see cref="SubStream"/> at the candidate's starting offset and
  /// ask the descriptor's <see cref="IArchiveFormatOperations.List"/> to
  /// walk it. If that succeeds without throwing and returns at least one
  /// entry, emit a <see cref="CarvedFilesystem"/>.
  /// </summary>
  private CarvedFilesystem? TryValidateOne(
    Stream stream,
    long offset,
    long available,
    IFormatDescriptor desc,
    double confidence
  ) {
    if (desc is not IArchiveFormatOperations archiveOps) return null;
    if (available <= 0) return null;

    var sub = new SubStream(stream, offset, available);
    try {
      var entries = archiveOps.List(sub, password: null);
      if (entries == null || entries.Count == 0) return null;

      var estimated = TryEstimateSize(entries);
      return new CarvedFilesystem(
        ByteOffset: offset,
        FormatId: desc.Id,
        Confidence: confidence,
        EstimatedSize: estimated
      );
    } catch {
      // List() threw — this is NOT a valid FS at this offset.
      return null;
    }
  }

  private static long TryEstimateSize(IReadOnlyList<ArchiveEntryInfo> entries) {
    // Conservative estimate: sum of per-entry sizes, ignoring negatives and
    // directories. Most FS readers don't know the image's total size — the
    // caller who wants precise bounds should use the partition table.
    var total = 0L;
    foreach (var e in entries) {
      if (e.IsDirectory) continue;
      if (e.OriginalSize <= 0) continue;
      total += e.OriginalSize;
    }
    return total;
  }

  /// <summary>
  /// A descriptor is considered a filesystem if its concrete type lives in
  /// a <c>FileSystem.*</c> namespace. We intentionally don't use
  /// <see cref="FormatCategory"/> because all FS descriptors currently use
  /// <see cref="FormatCategory.Archive"/> (there is no dedicated enum
  /// member) — namespace-based discrimination is the cleanest signal.
  /// </summary>
  private static bool IsFilesystemDescriptor(IFormatDescriptor desc)
    => desc.GetType().Namespace?.StartsWith("FileSystem.", StringComparison.Ordinal) == true;

  /// <summary>
  /// Scan the current window for builtin FS signatures (<see cref="BuiltinSignatures"/>)
  /// and synthesise <see cref="Scanning.ScanResult"/> entries for each hit,
  /// so the shared validation path treats them uniformly. Skips entries whose
  /// format is outside <paramref name="allowedIds"/>.
  /// </summary>
  private IEnumerable<Scanning.ScanResult> ScanBuiltins(
    ReadOnlySpan<byte> span,
    long windowStart,
    long streamLength,
    HashSet<string> allowedIds
  ) {
    var results = new List<Scanning.ScanResult>();
    foreach (var (formatId, magic, magicOffset, conf) in BuiltinSignatures) {
      if (!allowedIds.Contains(formatId)) continue;
      if (conf < this.Options.MinConfidence) continue;

      // Walk the span looking for `magic`. Each hit at local index `i` means
      // the FS header starts at window-local offset `i - magicOffset`. Emit
      // only when that lands inside the current window and is non-negative.
      for (var i = 0; i + magic.Length <= span.Length; ++i) {
        if (span[i] != magic[0]) continue;
        var ok = true;
        for (var j = 1; j < magic.Length; ++j) {
          if (span[i + j] != magic[j]) { ok = false; break; }
        }
        if (!ok) continue;

        var headerLocalOffset = i - magicOffset;
        if (headerLocalOffset < 0) continue;   // header sits before this window

        var headerAbsolute = windowStart + headerLocalOffset;
        if (headerAbsolute < 0 || headerAbsolute >= streamLength) continue;

        // Emit as a ScanResult so the shared loop picks it up. The `Offset`
        // on ScanResult is window-local — the caller adds windowStart.
        results.Add(new Scanning.ScanResult(
          Offset: headerLocalOffset,
          FormatName: formatId,
          Confidence: conf,
          MagicLength: magic.Length,
          HeaderPreview: string.Empty));
      }
    }
    return results;
  }

  private static bool MatchesAt(ReadOnlySpan<byte> span, int offset, byte[] pattern) {
    if (offset < 0 || offset + pattern.Length > span.Length) return false;
    for (var i = 0; i < pattern.Length; ++i)
      if (span[offset + i] != pattern[i]) return false;
    return true;
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
}

/// <summary>Knobs controlling <see cref="FilesystemCarver.CarveStream"/>.</summary>
public sealed record FsCarveOptions {
  /// <summary>Scanner-confidence floor (0..1). Hits below are dropped.</summary>
  public double MinConfidence { get; init; } = 0.5;

  /// <summary>Restrict to specific FS format IDs (null = all FS descriptors).</summary>
  public IReadOnlyList<string>? FormatIds { get; init; }

  /// <summary>Honour MBR/GPT partition tables when present — probe each partition start.</summary>
  public bool DescendIntoPartitionTables { get; init; } = true;

  /// <summary>Safety cap on total hits returned.</summary>
  public int MaxHits { get; init; } = 256;
}

/// <summary>
/// One carved filesystem located inside the host stream.
/// </summary>
/// <param name="ByteOffset">Byte offset where the FS starts in the source stream.</param>
/// <param name="FormatId">Format descriptor ID (e.g. "Ext", "Fat", "Btrfs").</param>
/// <param name="Confidence">Magic-match confidence [0..1] (propagated from <see cref="Scanning.SignatureScanner"/>).</param>
/// <param name="EstimatedSize">Sum of listed file sizes (best-effort, 0 if not derivable).</param>
public sealed record CarvedFilesystem(
  long ByteOffset,
  string FormatId,
  double Confidence,
  long EstimatedSize
);
