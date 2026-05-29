using Compression.Registry;

namespace Compression.Lib.FsConversion;

/// <summary>
/// Crash-safe file migrator from one filesystem image to another. Files are
/// copied one-by-one, source-deletes happen only after the destination flush
/// has committed the new copy, and a persistent <see cref="ConversionManifest"/>
/// records the per-file status so a crash anywhere can be resumed without
/// losing or duplicating files.
///
/// <para>
/// Invariants (the whole point of this class):
/// <list type="bullet">
///   <item>After any crash, every original file is on EXACTLY ONE of
///   {source, destination}. Never zero, never both.</item>
///   <item>The manifest tells <see cref="Resume"/> which file is in flight
///   so it can re-converge that file safely.</item>
///   <item>Every state change is followed by an explicit
///   <see cref="Stream.Flush()"/> on the underlying disk image.</item>
/// </list>
/// </para>
///
/// <para>
/// Usage: open the source filesystem stream and the (freshly-formatted, empty)
/// destination filesystem stream, identify them by their <see cref="FormatRegistry"/>
/// IDs, and call <see cref="Run"/>. To resume after a crash, call
/// <see cref="Resume"/> instead — it reads the manifest from the destination
/// and converges in-flight entries before continuing the remaining work.
/// </para>
/// </summary>
public sealed class MigrationConverter {

  private readonly Stream _src;
  private readonly Stream _dst;
  private readonly IArchiveFormatOperations _srcOps;
  private readonly IArchiveModifiable _srcMod;
  private readonly IArchiveFormatOperations _dstOps;
  private readonly IArchiveModifiable _dstMod;

  /// <summary>
  /// Opens a migration session over the supplied source and destination
  /// filesystem streams. Both must already exist and be initialised
  /// (source as a populated filesystem, destination as a freshly-formatted
  /// empty one). The descriptors must implement both
  /// <see cref="IArchiveFormatOperations"/> (for list/extract) and
  /// <see cref="IArchiveModifiable"/> (for add/remove).
  /// </summary>
  /// <param name="src">Source filesystem stream — read + write (write needed
  /// for in-place file deletion as each file is migrated).</param>
  /// <param name="dst">Destination filesystem stream — read + write.</param>
  /// <param name="srcFormatId">Format ID of the source FS (e.g. "Fat").</param>
  /// <param name="dstFormatId">Format ID of the destination FS (e.g. "ExFat").</param>
  public MigrationConverter(Stream src, Stream dst, string srcFormatId, string dstFormatId) {
    ArgumentNullException.ThrowIfNull(src);
    ArgumentNullException.ThrowIfNull(dst);
    ArgumentException.ThrowIfNullOrEmpty(srcFormatId);
    ArgumentException.ThrowIfNullOrEmpty(dstFormatId);

    FormatRegistration.EnsureInitialized();

    this._src = src;
    this._dst = dst;

    this._srcOps = FormatRegistry.GetArchiveOps(srcFormatId)
      ?? throw new InvalidOperationException($"Source format '{srcFormatId}' has no archive operations registered.");
    this._srcMod = this._srcOps as IArchiveModifiable
      ?? throw new InvalidOperationException($"Source format '{srcFormatId}' is not modifiable — cannot delete files after copy.");

    this._dstOps = FormatRegistry.GetArchiveOps(dstFormatId)
      ?? throw new InvalidOperationException($"Destination format '{dstFormatId}' has no archive operations registered.");
    this._dstMod = this._dstOps as IArchiveModifiable
      ?? throw new InvalidOperationException($"Destination format '{dstFormatId}' is not modifiable — cannot write files to it.");
  }

  /// <summary>
  /// Performs the full source→destination migration from a clean start.
  /// Any pre-existing manifest on the destination is honoured so a partial
  /// previous run is correctly resumed — callers that explicitly want a
  /// fresh attempt should delete the manifest from the destination first.
  /// </summary>
  public void Run() => this.Resume();

  /// <summary>
  /// Resumes (or starts) a migration. Reads any existing manifest from the
  /// destination filesystem, reconciles per-entry status against the actual
  /// source/destination contents, then completes the remaining work.
  /// </summary>
  /// <remarks>
  /// Reconciliation rules at entry to this method:
  /// <list type="bullet">
  ///   <item><see cref="ConversionEntryStatus.Done"/>: verify file actually
  ///   exists on dst. If not (manifest write succeeded but somehow the file
  ///   isn't there), demote to <see cref="ConversionEntryStatus.Pending"/>.</item>
  ///   <item><see cref="ConversionEntryStatus.Copying"/>: file was in flight
  ///   when we crashed. If it is now on dst, finish by deleting from src
  ///   (idempotent — Remove no-ops when src already lacks it) and mark Done.
  ///   Otherwise restart the copy from src.</item>
  ///   <item><see cref="ConversionEntryStatus.Pending"/>: no work has begun
  ///   yet; copy normally.</item>
  /// </list>
  /// </remarks>
  public void Resume() {
    var manifest = this.LoadOrCreateManifest();

    // First pass: reconcile in-flight entries before resuming forward progress.
    foreach (var entry in manifest.Entries) {
      switch (entry.Status) {
        case ConversionEntryStatus.Done:
          // If the file isn't actually on dst, the previous run's flush
          // never reached disk — treat as if the copy never happened.
          if (!this.DstHasFile(entry.SourcePath))
            entry.Status = ConversionEntryStatus.Pending;
          break;

        case ConversionEntryStatus.Copying:
          if (this.DstHasFile(entry.SourcePath)) {
            // dst copy succeeded; just need to finish the src.delete +
            // manifest update. Remove() is safe even if src already lacks
            // the file (it returns silently in the modifiable contract).
            this.SafeRemoveFromSrc(entry.SourcePath);
            this.FlushSrc();
            entry.Status = ConversionEntryStatus.Done;
            this.WriteManifest(manifest);
          } else {
            // dst write never landed; bring it back to Pending so the
            // forward pass re-copies it from src.
            entry.Status = ConversionEntryStatus.Pending;
          }
          break;
      }
    }
    this.WriteManifest(manifest);

    // Second pass: forward progress.
    foreach (var entry in manifest.Entries) {
      if (entry.Status == ConversionEntryStatus.Done) continue;

      // === BEFORE COPY ===
      // Mark Copying and flush manifest BEFORE the copy. If we crash mid-copy,
      // Resume sees Copying and inspects dst to decide whether to restart or
      // finalize. If we crashed AFTER reading src but BEFORE writing dst,
      // the file is still on src and dst lacks it → restart copy.
      entry.Status = ConversionEntryStatus.Copying;
      this.WriteManifest(manifest);

      // === COPY src → dst ===
      var data = this.ExtractFromSrc(entry.SourcePath);

      // If the destination already has this file (from a partial earlier
      // attempt that wrote the data but never updated the manifest), don't
      // duplicate it.
      if (!this.DstHasFile(entry.SourcePath))
        this.AddToDst(entry.SourcePath, data);
      this.FlushDst();

      // === DELETE FROM src ===
      // After dst.flush, dst has the file. If we crash between here and the
      // manifest update, Resume sees Copying + dst-has-it and finishes the
      // src delete (idempotently) + manifest update.
      this.SafeRemoveFromSrc(entry.SourcePath);
      this.FlushSrc();

      // === AFTER DELETE ===
      // The file is now on dst, not on src. Update manifest to Done and flush.
      entry.Status = ConversionEntryStatus.Done;
      this.WriteManifest(manifest);
    }

    // All entries are Done — drop the manifest. If we crash between the
    // remove and the flush, Resume sees no manifest and treats the migration
    // as already complete (which it is — every file is on dst, none on src).
    this.SafeRemoveFromDst(ConversionManifest.FileName);
    this.FlushDst();
  }

  // ── Manifest read/write through the destination FS ─────────────────────

  private ConversionManifest LoadOrCreateManifest() {
    var existing = this.TryReadManifestFromDst();
    if (existing is null) {
      // No manifest yet (fresh migration). Enumerate src, build the worklist,
      // and persist it so a crash before the first file copy is recoverable.
      var fresh = new ConversionManifest();
      foreach (var src in this.ListSrc()) {
        if (src.IsDirectory) continue;
        if (src.Name == ConversionManifest.FileName) continue; // never migrate our own marker
        fresh.Entries.Add(new ConversionManifestEntry {
          SourcePath = src.Name,
          Status = ConversionEntryStatus.Pending,
          Size = src.OriginalSize,
        });
      }
      this.WriteManifest(fresh);
      return fresh;
    }

    // Merge: any file present on src but not in the existing manifest
    // (e.g. it was added between runs) gets appended as Pending. The
    // existing entries are kept as-is so their statuses survive resume.
    var known = new HashSet<string>(existing.Entries.Select(e => e.SourcePath), StringComparer.Ordinal);
    foreach (var src in this.ListSrc()) {
      if (src.IsDirectory) continue;
      if (src.Name == ConversionManifest.FileName) continue;
      if (!known.Contains(src.Name))
        existing.Entries.Add(new ConversionManifestEntry {
          SourcePath = src.Name,
          Status = ConversionEntryStatus.Pending,
          Size = src.OriginalSize,
        });
    }
    return existing;
  }

  private ConversionManifest? TryReadManifestFromDst() {
    var entry = this.ListDst().FirstOrDefault(e => e.Name == ConversionManifest.FileName);
    if (entry is null) return null;

    // Extract to a temp file, read it back, delete temp. The IArchiveFormatOperations
    // contract is dir-based extraction; this is the cheapest way to round-trip.
    var tempDir = Path.Combine(Path.GetTempPath(), "cw-migrate-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDir);
    try {
      this._dst.Position = 0;
      this._dstOps.Extract(this._dst, tempDir, password: null, files: [ConversionManifest.FileName]);
      var candidate = Path.Combine(tempDir, ConversionManifest.FileName);
      if (!File.Exists(candidate)) {
        // Some FS extractors normalise leading dots; look for any file too.
        var any = Directory.GetFiles(tempDir).FirstOrDefault();
        if (any is null) return null;
        candidate = any;
      }
      var bytes = File.ReadAllBytes(candidate);
      return ConversionManifest.TryParse(bytes);
    } finally {
      try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
    }
  }

  private void WriteManifest(ConversionManifest manifest) {
    var bytes = manifest.Serialize();

    // Stage in a temp file so the dst.Add() call (which the descriptor will
    // open with ReadAllBytes) sees a complete, CRC-valid blob. Remove any
    // existing manifest first so Add doesn't no-op or duplicate.
    this.SafeRemoveFromDst(ConversionManifest.FileName);

    var tempPath = Path.Combine(Path.GetTempPath(),
      "cw-migrate-mf-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
    try {
      File.WriteAllBytes(tempPath, bytes);
      this._dst.Position = 0;
      this._dstMod.Add(this._dst, [new ArchiveInputInfo(tempPath, ConversionManifest.FileName, false)]);
      this.FlushDst();
    } finally {
      AtomicFileWriter.TryDelete(tempPath);
    }
  }

  // ── Source ops (list, extract, remove) ─────────────────────────────────

  private List<ArchiveEntryInfo> ListSrc() {
    this._src.Position = 0;
    return this._srcOps.List(this._src, password: null);
  }

  private byte[] ExtractFromSrc(string name) {
    this._src.Position = 0;
    var tempDir = Path.Combine(Path.GetTempPath(), "cw-migrate-src-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDir);
    try {
      this._srcOps.Extract(this._src, tempDir, password: null, files: [name]);
      var candidate = Path.Combine(tempDir, name);
      if (!File.Exists(candidate)) {
        var any = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories).FirstOrDefault();
        if (any is null)
          throw new IOException($"Source extraction produced no file for '{name}'.");
        candidate = any;
      }
      return File.ReadAllBytes(candidate);
    } finally {
      try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
    }
  }

  private void SafeRemoveFromSrc(string name) {
    try {
      this._src.Position = 0;
      this._srcMod.Remove(this._src, [name]);
    } catch (FileNotFoundException) {
      // already gone — idempotent
    } catch (InvalidOperationException) {
      // some FS modifiers throw on missing entries; treat as idempotent
    }
  }

  private void FlushSrc() => this._src.Flush();

  // ── Destination ops (list, add, remove) ────────────────────────────────

  private List<ArchiveEntryInfo> ListDst() {
    this._dst.Position = 0;
    return this._dstOps.List(this._dst, password: null);
  }

  private bool DstHasFile(string name) {
    try {
      return this.ListDst().Any(e => e.Name == name);
    } catch {
      return false;
    }
  }

  private void AddToDst(string name, byte[] data) {
    var tempPath = Path.Combine(Path.GetTempPath(),
      "cw-migrate-add-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
    try {
      File.WriteAllBytes(tempPath, data);
      this._dst.Position = 0;
      this._dstMod.Add(this._dst, [new ArchiveInputInfo(tempPath, name, false)]);
    } finally {
      AtomicFileWriter.TryDelete(tempPath);
    }
  }

  private void SafeRemoveFromDst(string name) {
    try {
      this._dst.Position = 0;
      this._dstMod.Remove(this._dst, [name]);
    } catch (FileNotFoundException) {
      // already gone — idempotent
    } catch (InvalidOperationException) {
      // some FS modifiers throw on missing entries; treat as idempotent
    }
  }

  private void FlushDst() => this._dst.Flush();
}
