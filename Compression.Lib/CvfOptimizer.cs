using System.Diagnostics;
using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Re-encodes a CVF (DoubleSpace / DriveSpace / DriveSpace 3 Compressed Volume
/// File) using the highest-effort compression method the descriptor publishes,
/// letting the writer's per-cluster shrink-or-store fallback transparently
/// handle incompressible runs. Commits via <see cref="AtomicFileWriter"/>.
/// <para>
/// <b>Design rationale.</b> CVF effort tiers are monotone: <c>ds-lz77++</c>
/// always beats (or ties) <c>ds-lz77+</c> which always beats <c>ds-lz77</c>
/// on compressible data. Trying every method at the container level wastes
/// CPU. Worse, the "stored vs compressed" decision is already made
/// <em>per-cluster</em> inside the writer (see
/// <see cref="FileSystem.DoubleSpace.DsCompression"/>): if the encoded
/// payload would not fit the 12-bit CVF size cap or is no smaller than the
/// raw input, a stored CVF run is emitted. So the right optimisation knob
/// is "always pick the highest-effort method; let the per-cluster fallback
/// handle the rest". That is what this class does.
/// </para>
/// </summary>
public static class CvfOptimizer {

  /// <summary>
  /// Result of a single CVF optimization pass.
  /// </summary>
  /// <param name="MethodUsed">The non-stored method id that was applied
  /// (e.g. <c>ds-lz77++</c>, <c>ms-lzh</c>, or <c>stored</c> if the
  /// descriptor only publishes a stored method).</param>
  /// <param name="OriginalSize">Source CVF size in bytes before re-encoding.</param>
  /// <param name="OptimizedSize">Re-encoded CVF size in bytes after the
  /// atomic commit.</param>
  /// <param name="FilesStoredVerbatim">Count of clusters emitted as stored
  /// runs by the writer (MDFAT flag = 1). <c>-1</c> if the descriptor's
  /// reader doesn't expose MDFAT iteration.</param>
  /// <param name="FilesCompressed">Count of clusters emitted as compressed
  /// runs by the writer (MDFAT flag = 2). <c>-1</c> if the descriptor's
  /// reader doesn't expose MDFAT iteration.</param>
  /// <param name="Elapsed">Wall-clock duration of the re-encode pass.</param>
  public sealed record OptimizeResult(
    string MethodUsed,
    long OriginalSize,
    long OptimizedSize,
    int FilesStoredVerbatim,
    int FilesCompressed,
    TimeSpan Elapsed);

  /// <summary>
  /// Re-encodes the CVF at <paramref name="sourcePath"/> in place using the
  /// highest-effort non-stored compression method <paramref name="descriptor"/>
  /// publishes. The descriptor must implement
  /// <see cref="IArchiveFormatOperations"/> + <see cref="IArchiveCreatable"/>.
  /// The atomic write protocol stages the new image in a sibling temp file
  /// and renames it into place on success; the original is left untouched on
  /// any error.
  /// </summary>
  /// <exception cref="ArgumentException">If <paramref name="descriptor"/> is
  /// not an <see cref="IArchiveFormatOperations"/> + <see cref="IArchiveCreatable"/>.</exception>
  /// <exception cref="FileNotFoundException">If <paramref name="sourcePath"/>
  /// does not exist.</exception>
  public static OptimizeResult Optimize(string sourcePath, IFormatDescriptor descriptor) {
    ArgumentException.ThrowIfNullOrEmpty(sourcePath);
    ArgumentNullException.ThrowIfNull(descriptor);

    if (descriptor is not IArchiveFormatOperations ops)
      throw new ArgumentException(
        $"Descriptor '{descriptor.Id}' does not support archive operations.", nameof(descriptor));
    if (descriptor is not IArchiveCreatable creatable)
      throw new ArgumentException(
        $"Descriptor '{descriptor.Id}' is not creatable.", nameof(descriptor));

    if (!File.Exists(sourcePath))
      throw new FileNotFoundException("CVF source not found.", sourcePath);

    var sw = Stopwatch.StartNew();
    var originalSize = new FileInfo(sourcePath).Length;

    // Step 1 — pick the most-'+' non-stored method published by the descriptor.
    // Fall back order: highest-effort non-stored → first non-stored → "stored".
    var bestMethod = PickBestMethod(descriptor);

    // Step 2 — extract entries via List + Extract through a scratch directory
    // we own. We could also use ArchiveOperations.ExtractAll, but going
    // through the descriptor directly avoids registry lookups for the
    // (already-known) source format.
    var inputs = ExtractAllToMemory(sourcePath, ops);

    // Step 3 — re-encode into a fresh byte buffer via descriptor.Create with
    // the highest-effort method. The writer's per-cluster shrink-or-store
    // fallback handles incompressible clusters transparently.
    byte[] rebuilt;
    using (var output = new MemoryStream()) {
      creatable.Create(output, inputs, new FormatCreateOptions { MethodName = bestMethod });
      rebuilt = output.ToArray();
    }

    // Step 4 — count stored vs compressed clusters in the new image, when
    // the descriptor exposes an extent map. Best-effort: a missing IFilesystemExtentMap
    // surfaces as (-1, -1) and the size delta remains the primary feedback.
    var (storedClusters, compressedClusters) = CountClusterFlags(rebuilt, descriptor);

    // Step 5 — commit atomically. AtomicFileWriter stages a sibling temp
    // file, flushes to disk, then renames into place; on any exception the
    // original is left untouched and the temp is best-effort deleted.
    AtomicFileWriter.WriteAllBytesAtomic(sourcePath, rebuilt);

    sw.Stop();
    return new OptimizeResult(
      MethodUsed: bestMethod,
      OriginalSize: originalSize,
      OptimizedSize: rebuilt.LongLength,
      FilesStoredVerbatim: storedClusters,
      FilesCompressed: compressedClusters,
      Elapsed: sw.Elapsed);
  }

  // =========================================================================
  //                              Internals
  // =========================================================================

  private static string PickBestMethod(IFormatDescriptor descriptor) {
    // Non-stored methods ordered by '+' count desc — Zopfli-style effort
    // tiers are monotone on compressible data, so picking the most '+' is
    // always the right call.
    var nonStored = descriptor.Methods
      .Where(m => !m.Name.Equals("stored", StringComparison.OrdinalIgnoreCase))
      .OrderByDescending(m => m.Name.Count(c => c == '+'))
      .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();

    if (nonStored.Count > 0)
      return nonStored[0].Name;

    // Pathological: descriptor only publishes "stored". Re-encode into
    // stored runs anyway so the operation still emits a fresh image; the
    // delta will be 0 (modulo metadata padding tweaks).
    return "stored";
  }

  private static IReadOnlyList<ArchiveInputInfo> ExtractAllToMemory(
      string sourcePath, IArchiveFormatOperations ops) {
    using var src = File.OpenRead(sourcePath);
    var entries = ops.List(src, password: null);

    var inputs = new List<ArchiveInputInfo>(entries.Count);
    var tmpDir = Path.Combine(Path.GetTempPath(),
      "CvfOptimize_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      // Extract to a scratch directory, then slurp each non-directory entry
      // back into a memory-backed ArchiveInputInfo. The descriptor's Extract
      // is the simplest portable way to materialise every entry — going
      // through the registry's in-memory extract path would require every
      // CVF descriptor to opt in to IArchiveInMemoryExtract.
      src.Position = 0;
      ops.Extract(src, tmpDir, password: null, files: null);

      foreach (var e in entries) {
        if (e.IsDirectory) continue;
        var diskPath = Path.Combine(tmpDir, NormalizePath(e.Name));
        if (!File.Exists(diskPath)) continue;
        var bytes = File.ReadAllBytes(diskPath);
        inputs.Add(ArchiveInputInfo.InMemory(e.Name, bytes));
      }
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    return inputs;
  }

  private static string NormalizePath(string name)
    => name.Replace('\\', '/').TrimStart('/');

  private static (int Stored, int Compressed) CountClusterFlags(
      byte[] image, IFormatDescriptor descriptor) {
    if (descriptor is not IFilesystemExtentMap extentMap)
      return (-1, -1);

    // We don't actually need EnumerateExtents for the flag count — the
    // MDFAT entries directly encode (flags=1: stored, flags=2: compressed).
    // Walk the MDBPB-declared MDFAT region in place; this is O(cluster count)
    // and avoids re-parsing the directory tree.
    if (image.Length < 80) return (-1, -1);

    // MDBPB offsets are spec-shared across the CVF family.
    var mdfatStartSector = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(44));
    var mdfatLenSectors = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(48));
    var bytesPerSector = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    if (bytesPerSector is 0 or > 4096) bytesPerSector = 512;

    if (mdfatStartSector <= 0 || mdfatLenSectors <= 0) return (-1, -1);

    var baseOffset = mdfatStartSector * bytesPerSector;
    var entryCount = mdfatLenSectors * bytesPerSector / 4;

    var stored = 0;
    var compressed = 0;
    for (var i = 0; i < entryCount; i++) {
      var off = baseOffset + i * 4;
      if (off + 4 > image.Length) break;
      var entry = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off));
      var flags = (int)((entry >> 28) & 0xFu);
      switch (flags) {
        case 1: stored++; break;
        case 2: compressed++; break;
      }
    }

    // Silence the unused-variable warning when the descriptor implements the
    // extent map interface but we resolved by direct MDFAT walk instead.
    _ = extentMap;
    return (stored, compressed);
  }
}
