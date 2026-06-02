using F = Compression.Lib.FormatDetector.Format;

namespace Compression.Lib;

/// <summary>
/// Unified operations across all archive and stream formats.
/// Dispatches to format descriptors via the FormatRegistry.
/// </summary>
public static class ArchiveOperations {

  public static List<ArchiveEntry> List(string path, string? password) {
    var format = FormatDetector.Detect(path);

    // Handle SFX: extract embedded archive info and delegate to the inner format
    if (format == F.Sfx) {
      var sfxInfo = FormatDetector.GetSfxArchiveInfo(path);
      if (sfxInfo == null) throw new InvalidOperationException("SFX file contains no detectable archive.");
      using var fs2 = File.OpenRead(path);
      using var sub = new SubStream(fs2, sfxInfo.Value.Offset, sfxInfo.Value.Length);
      return ListArchiveStream(sfxInfo.Value.ArchiveFormat, sub, password);
    }

    using var fs = File.OpenRead(path);
    return ListArchiveStream(format, fs, password, path);
  }

  private static List<ArchiveEntry> ListArchiveStream(F format, Stream fs, string? password, string? pathHint = null) {
    FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());
    if (ops != null)
      return ops.List(fs, password).Select(e =>
        new ArchiveEntry(e.Index, e.Name, e.OriginalSize, e.CompressedSize, e.Method, e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();

    // Stream formats show as a single entry
    if (pathHint != null && FormatDetector.IsStreamFormat(format))
      return [new(0, StripCompressionExtension(pathHint), new FileInfo(pathHint).Length, new FileInfo(pathHint).Length, format.ToString(), false, false, null)];

    throw new NotSupportedException($"Cannot list format: {format}");
  }

  public static void Extract(string path, string outputDir, string? password, string[]? files) {
    var format = FormatDetector.Detect(path);
    Directory.CreateDirectory(outputDir);

    // Handle SFX: extract embedded archive info and delegate to the inner format
    if (format == F.Sfx) {
      var sfxInfo = FormatDetector.GetSfxArchiveInfo(path);
      if (sfxInfo == null) throw new InvalidOperationException("SFX file contains no detectable archive.");
      using var fs2 = File.OpenRead(path);
      using var sub = new SubStream(fs2, sfxInfo.Value.Offset, sfxInfo.Value.Length);
      ExtractWithStream(sfxInfo.Value.ArchiveFormat, sub, outputDir, password, files);
      return;
    }

    // Prefer archive ops when available (e.g. FLAC exposes per-channel WAVs
    // as an archive view even though it also supports stream decompression).
    FormatRegistration.EnsureInitialized();
    var archiveOps = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());
    if (archiveOps != null) {
      using var fs = File.OpenRead(path);
      archiveOps.Extract(fs, outputDir, password, files);
      return;
    }

    if (FormatDetector.IsStreamFormat(format)) {
      ExtractStream(path, outputDir, format);
      return;
    }

    using var fs3 = File.OpenRead(path);
    ExtractWithStream(format, fs3, outputDir, password, files);
  }

  private static void ExtractWithStream(F format, Stream fs, string outputDir, string? password, string[]? files) {
    FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());
    if (ops != null) { ops.Extract(fs, outputDir, password, files); return; }

    throw new NotSupportedException($"Cannot extract format: {format}");
  }

  /// <summary>
  /// Creates a new archive at <paramref name="outputPath"/>. The format is
  /// inferred from the output extension; for ambiguous extensions
  /// (e.g. <c>.img</c> claimed by FAT/NTFS/ext/Btrfs/etc.), use the overload
  /// that takes an explicit <see cref="FormatDetector.Format"/>.
  /// </summary>
  public static void Create(string outputPath, IReadOnlyList<ArchiveInput> inputs, CompressionOptions opts)
    => Create(outputPath, inputs, opts, FormatDetector.DetectByExtension(outputPath));

  /// <summary>
  /// Creates a new archive in the explicitly-requested <paramref name="format"/>.
  /// Bypasses extension-based detection so the caller can disambiguate
  /// extensions claimed by multiple formats.
  /// </summary>
  /// <remarks>
  /// Fail-safe: the archive is staged to a sibling <c>.tmp</c> file, flushed
  /// to disk, then atomically renamed over <paramref name="outputPath"/>. A
  /// crash mid-write never leaves a partial archive in place.
  /// </remarks>
  public static void Create(string outputPath, IReadOnlyList<ArchiveInput> inputs,
                            CompressionOptions opts, FormatDetector.Format format)
    => Create(outputPath, inputs, opts, format, formatSpecific: null);

  /// <summary>
  /// Creates a new archive with caller-supplied format-specific tunables
  /// (collected from an <see cref="Compression.Registry.IFormatOptionsSchema"/>
  /// — e.g. <c>FatType=FAT16</c>, <c>ClusterSize=4096</c>). Threads the dict
  /// straight into <see cref="Compression.Registry.FormatCreateOptions.FormatSpecific"/>
  /// so target writers see it on the other side of the registry boundary.
  /// </summary>
  public static void Create(string outputPath, IReadOnlyList<ArchiveInput> inputs,
                            CompressionOptions opts, FormatDetector.Format format,
                            IReadOnlyDictionary<string, string>? formatSpecific) {
    var method = opts.Method.Name == null ? MethodSpec.Default : opts.Method;
    var password = opts.Password;
    if (format == F.Unknown)
      throw new NotSupportedException($"Cannot determine format from extension: {Path.GetExtension(outputPath)}");

    // Build the set of incompressible file paths for entropy-aware formats
    HashSet<string>? incompressible = null;
    if (!opts.ForceCompress) {
      incompressible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var i in inputs) {
        if (!i.IsDirectory && !string.IsNullOrEmpty(i.FullPath) && Compression.Registry.EntropyDetector.IsIncompressible(i.FullPath))
          incompressible.Add(i.FullPath);
      }
      if (incompressible.Count == 0) incompressible = null;
    }

    if (FormatDetector.IsStreamFormat(format)) {
      var files = inputs.Where(i => !i.IsDirectory).ToArray();
      if (files.Length != 1)
        throw new ArgumentException("Stream compression formats require exactly one input file.");
      CompressStream(files[0].FullPath, outputPath, format, method);
      return;
    }

    // Every format dispatches through IArchiveCreatable now — the previous
    // hardcoded switch for ZIP/7z/RAR has moved into those descriptors'
    // own Create methods.
    FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());
    if (ops is not Compression.Registry.IArchiveCreatable creator)
      throw new NotSupportedException($"Format {format} has no creatable descriptor");

    var registryInputs = inputs.Select(i =>
      new Compression.Registry.ArchiveInputInfo(i.FullPath, i.EntryName, i.IsDirectory)).ToList();
    var registryOpts = new Compression.Registry.FormatCreateOptions {
      Password = opts.Password,
      MethodName = opts.Method.Name,
      Optimize = opts.Method.Optimize,
      Level = opts.Level,
      DictSize = opts.DictSize,
      WordSize = opts.WordSize,
      Threads = opts.Threads,
      SolidSize = opts.SolidSize,
      ForceCompress = opts.ForceCompress,
      EncryptFilenames = opts.EncryptFilenames,
      EncryptionMethod = opts.ZipEncryption,
      IncompressiblePaths = incompressible,
      // Prefer the explicit per-call formatSpecific bag (threaded by
      // ConvertArchive's target-options dialog); fall back to the one carried
      // on the CompressionOptions for CLI callers that fill it in there.
      FormatSpecific = formatSpecific ?? opts.FormatSpecific,
    };

    AtomicFileWriter.WriteAtomic(outputPath, fs => creator.Create(fs, registryInputs, registryOpts));
  }

  /// <summary>
  /// Converts between formats using a 3-tier strategy:
  /// <list type="bullet">
  ///   <item>Tier 1: Bitstream transfer — same codec, different container, zero decompression</item>
  ///   <item>Tier 2: Container restream — decompress outer wrapper, recompress with new wrapper</item>
  ///   <item>Tier 3: Full recompress — extract all content, re-encode into target format</item>
  /// </list>
  /// Tier 3 is also used when the method is changed (e.g. store→deflate+) or when "+" is requested.
  /// </summary>
  /// <returns>A (strategy description, tier number) tuple.</returns>
  /// <remarks>
  /// Fail-safe: the output is staged to a sibling <c>.tmp</c> file, flushed
  /// to disk, then atomically renamed over <paramref name="outputPath"/>. A
  /// crash mid-conversion never leaves a partial target; the orphan temp
  /// is deleted in a finally block.
  /// </remarks>
  public static (string Strategy, int Tier) Convert(string inputPath, string outputPath,
      string? password, MethodSpec method = default) {
    if (method.Name == null) method = MethodSpec.Default;
    var srcFormat = FormatDetector.Detect(inputPath);
    var dstFormat = FormatDetector.DetectByExtension(outputPath);

    var tempOutputPath = AtomicFileWriter.MakeTempPath(outputPath);
    try {
      var result = ConvertCore(inputPath, tempOutputPath, srcFormat, dstFormat, password, method);
      AtomicFileWriter.ReplaceTarget(tempOutputPath, outputPath);
      return result;
    } catch {
      AtomicFileWriter.TryDelete(tempOutputPath);
      throw;
    }
  }

  /// <summary>
  /// Internal conversion that writes to a freely-chosen output path. The
  /// public <see cref="Convert"/> wraps this with atomic-rename semantics.
  /// </summary>
  private static (string Strategy, int Tier) ConvertCore(string inputPath, string outputPath,
      F srcFormat, F dstFormat, string? password, MethodSpec method) {

    // "+" forces tier 3 (full recompress with optimal encoder)
    if (!method.Optimize && method.IsDefault) {

      // ══════════════════════════════════════════════════════════════════
      // Tier 1: Bitstream transfer — same codec, different container
      // Raw compressed bytes are moved without any decompression.
      // ══════════════════════════════════════════════════════════════════

      // Tier 1a: Deflate family reframe (gz ↔ zlib)
      if (FormatDetector.IsStreamFormat(srcFormat) && FormatDetector.IsStreamFormat(dstFormat)) {
        var t1 = TryDeflateRestream(inputPath, outputPath, srcFormat, dstFormat);
        if (t1 != null) return (t1, 1);
      }

      // Tier 1b: ZIP Deflate → Gzip/Zlib
      if (srcFormat == F.Zip && (dstFormat == F.Gzip || dstFormat == F.Zlib)) {
        var t1 = TryZipToStreamRestream(inputPath, outputPath, dstFormat, password);
        if (t1 != null) return (t1, 1);
      }

      // Tier 1c: Gzip/Zlib → ZIP
      if ((srcFormat == F.Gzip || srcFormat == F.Zlib) && dstFormat == F.Zip) {
        var t1 = TryStreamToZipRestream(inputPath, outputPath, srcFormat, password);
        if (t1 != null) return (t1, 1);
      }

      // ══════════════════════════════════════════════════════════════════
      // Tier 2: Container restream — decompress + recompress the wrapper
      // The inner payload passes through untouched (e.g. raw tar bytes).
      // ══════════════════════════════════════════════════════════════════

      var srcComp = FormatDetector.GetTarCompression(srcFormat);
      var dstComp = FormatDetector.GetTarCompression(dstFormat);

      // Tier 2a: compound tar → compound tar (swap outer compression)
      if (srcComp.HasValue && dstComp.HasValue) {
        using (var inFs = File.OpenRead(inputPath))
        using (var outFs = File.Create(outputPath))
        using (var decompressed = new MemoryStream()) {
          DecompressStreamPair(inFs, decompressed, srcComp.Value);
          decompressed.Position = 0;
          CompressStreamPair(decompressed, outFs, dstComp.Value);
          outFs.Flush(flushToDisk: true);
        }
        return ("tar passthrough, swap outer compression", 2);
      }

      // Tier 2b: compound tar → plain tar (just strip outer compression)
      if (srcComp.HasValue && dstFormat == F.Tar) {
        using (var inFs = File.OpenRead(inputPath))
        using (var outFs = File.Create(outputPath)) {
          DecompressStreamPair(inFs, outFs, srcComp.Value);
          outFs.Flush(flushToDisk: true);
        }
        return ("unwrap outer compression", 2);
      }

      // Tier 2c: plain tar → compound tar (just add outer compression)
      if (srcFormat == F.Tar && dstComp.HasValue) {
        using (var inFs = File.OpenRead(inputPath))
        using (var outFs = File.Create(outputPath)) {
          CompressStreamPair(inFs, outFs, dstComp.Value);
          outFs.Flush(flushToDisk: true);
        }
        return ("wrap with outer compression", 2);
      }

      // Tier 2d: stream → stream with different codec (decompress + recompress content)
      if (FormatDetector.IsStreamFormat(srcFormat) && FormatDetector.IsStreamFormat(dstFormat)) {
        using (var inFs = File.OpenRead(inputPath))
        using (var raw = new MemoryStream()) {
          DecompressStreamPair(inFs, raw, srcFormat);
          raw.Position = 0;
          using var outFs = File.Create(outputPath);
          CompressStreamPair(raw, outFs, dstFormat);
          outFs.Flush(flushToDisk: true);
        }
        return ("restream content with new codec", 2);
      }
    }

    // ══════════════════════════════════════════════════════════════════
    // Tier 3: Full recompress — extract + re-encode
    // Used for: archive↔archive, method changes, "+" optimization.
    // ══════════════════════════════════════════════════════════════════
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_convert_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Extract(inputPath, tempDir, password, null);
      // Build inputs preserving directory structure from temp extraction
      var inputs = new List<ArchiveInput>();
      foreach (var dir in Directory.GetDirectories(tempDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tempDir, dir).Replace('\\', '/');
        inputs.Add(new ArchiveInput("", rel + "/"));
      }
      foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tempDir, file).Replace('\\', '/');
        inputs.Add(new ArchiveInput(file, rel));
      }
      Create(outputPath, inputs, new CompressionOptions { Method = method, Password = password }, dstFormat);
    }
    finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
    var label = method.Optimize ? $"full recompress ({method})" : "full recompress";
    return (label, 3);
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing archive. When the
  /// format's descriptor implements <see cref="Compression.Registry.IArchiveModifiable"/>,
  /// the call is routed through it for true O(touched bytes) random-access I/O
  /// (e.g. retro disk filesystems with the new modifier classes). Otherwise
  /// falls back to extract-add-recreate, which requires <paramref name="opts"/>
  /// to know how to recompress.
  /// </summary>
  public static void Add(string archivePath, IReadOnlyList<ArchiveInput> inputs,
                         CompressionOptions? opts = null) {
    var format = FormatDetector.Detect(archivePath);
    FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());

    if (ops is Compression.Registry.IArchiveModifiable modifier) {
      var registryInputs = inputs.Select(i =>
        new Compression.Registry.ArchiveInputInfo(i.FullPath, i.EntryName, i.IsDirectory)).ToList();
      using var fs = File.Open(archivePath, FileMode.Open, FileAccess.ReadWrite);
      modifier.Add(fs, registryInputs);
      return;
    }

    // Rebuild fallback: extract everything, splat inputs on top, recreate.
    AddViaRebuild(archivePath, inputs, opts ?? new CompressionOptions());
  }

  /// <summary>
  /// Removes named entries from an existing archive. Prefers
  /// <see cref="Compression.Registry.IArchiveModifiable.Remove"/>; falls back to
  /// extract-skip-recreate.
  /// </summary>
  public static void Remove(string archivePath, string[] entryNames,
                            CompressionOptions? opts = null) {
    var format = FormatDetector.Detect(archivePath);
    FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());

    if (ops is Compression.Registry.IArchiveModifiable modifier) {
      using var fs = File.Open(archivePath, FileMode.Open, FileAccess.ReadWrite);
      modifier.Remove(fs, entryNames);
      return;
    }

    RemoveViaRebuild(archivePath, entryNames, opts ?? new CompressionOptions());
  }

  /// <summary>
  /// Replaces an existing entry with the contents of <paramref name="newSourcePath"/>.
  /// Sugar for <see cref="Remove"/> followed by <see cref="Add"/>; uses the
  /// modifier path when available so the operation is O(touched bytes) on the
  /// new file's bytes.
  /// </summary>
  public static void Replace(string archivePath, string entryName, string newSourcePath,
                             CompressionOptions? opts = null) {
    if (!File.Exists(newSourcePath))
      throw new FileNotFoundException("Source file not found.", newSourcePath);
    Remove(archivePath, [entryName], opts);
    Add(archivePath, [new ArchiveInput(newSourcePath, entryName)], opts);
  }

  private static void AddViaRebuild(string archivePath, IReadOnlyList<ArchiveInput> inputs,
                                    CompressionOptions opts) {
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_add_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tempDir);
      Extract(archivePath, tempDir, password: null, files: null);

      // Splat new inputs into the temp tree, preserving their entry names.
      foreach (var i in inputs) {
        if (i.IsDirectory || string.IsNullOrEmpty(i.FullPath)) continue;
        var entryName = string.IsNullOrEmpty(i.EntryName) ? Path.GetFileName(i.FullPath) : i.EntryName;
        var dest = Path.Combine(tempDir, entryName.Replace('/', Path.DirectorySeparatorChar));
        var destDir = Path.GetDirectoryName(dest);
        if (destDir != null) Directory.CreateDirectory(destDir);
        File.Copy(i.FullPath, dest, overwrite: true);
      }

      Create(archivePath, EnumerateTempInputs(tempDir), opts);
    }
    finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
  }

  private static void RemoveViaRebuild(string archivePath, string[] entryNames,
                                       CompressionOptions opts) {
    var skip = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_rm_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tempDir);
      Extract(archivePath, tempDir, password: null, files: null);

      foreach (var path in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tempDir, path).Replace('\\', '/');
        if (skip.Contains(rel) || skip.Contains(Path.GetFileName(rel)))
          File.Delete(path);
      }

      Create(archivePath, EnumerateTempInputs(tempDir), opts);
    }
    finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
  }

  /// <summary>
  /// Builds an <see cref="ArchiveInput"/> list from a temp dir's contents
  /// using paths relative to the dir itself — so entry names don't include
  /// the dir name as a prefix.
  /// </summary>
  private static List<ArchiveInput> EnumerateTempInputs(string tempDir) {
    var inputs = new List<ArchiveInput>();
    foreach (var dir in Directory.GetDirectories(tempDir, "*", SearchOption.AllDirectories)) {
      var rel = Path.GetRelativePath(tempDir, dir).Replace('\\', '/');
      inputs.Add(new ArchiveInput("", rel + "/"));
    }
    foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)) {
      var rel = Path.GetRelativePath(tempDir, file).Replace('\\', '/');
      inputs.Add(new ArchiveInput(file, rel));
    }
    return inputs;
  }

  public static bool Test(string path, string? password) {
    try {
      var format = FormatDetector.Detect(path);
      using var fs = File.OpenRead(path);

      if (FormatDetector.IsStreamFormat(format)) {
        DecompressToNull(fs, format);
        return true;
      }

      // Try extracting each entry to verify integrity
      switch (format) {
        case F.Zip: { var r = new FileFormat.Zip.ZipReader(fs, password: password); foreach (var e in r.Entries) if (!e.IsDirectory) r.ExtractEntry(e); break; }
        case F.Rar: { var r = new FileFormat.Rar.RarReader(fs, password: password); for (var i = 0; i < r.Entries.Count; ++i) if (!r.Entries[i].IsDirectory) r.Extract(i); break; }
        case F.SevenZip: { var r = new FileFormat.SevenZip.SevenZipReader(fs, password: password); for (var i = 0; i < r.Entries.Count; ++i) if (!r.Entries[i].IsDirectory) r.Extract(i); break; }
        default:
          var tempDir = Path.Combine(Path.GetTempPath(), "cwb_test_" + Guid.NewGuid().ToString("N")[..8]);
          try { Extract(path, tempDir, password, null); } finally { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
          break;
      }
      return true;
    }
    catch {
      return false;
    }
  }

  // ── Deflate bitstream restream helpers ───────────────────────────

  /// <summary>Gzip ↔ Zlib: transfer raw Deflate bytes, reframe only.</summary>
  private static string? TryDeflateRestream(string inputPath, string outputPath, F src, F dst) {
    // Both must be Deflate-based wrappers
    if (src != F.Gzip && src != F.Zlib) return null;
    if (dst != F.Gzip && dst != F.Zlib) return null;
    if (src == dst) return null; // same format, no point

    var inputData = File.ReadAllBytes(inputPath);

    if (src == F.Gzip && dst == F.Zlib) {
      var (deflate, crc32, _) = FileFormat.Gzip.GzipRawHelper.Unwrap(inputData);
      // Need Adler-32 of uncompressed data — must decompress for checksum
      var uncompressed = Compression.Core.Deflate.DeflateDecompressor.Decompress(deflate);
      var adler = Compression.Core.Checksums.Adler32.Compute(uncompressed);
      // Write through a flushed FileStream so the outer Convert atomic-rename
      // sees fully-persisted bytes. (Convert already routes us to a temp path.)
      using (var outFs = File.Create(outputPath)) {
        var wrapped = FileFormat.Zlib.ZlibRawHelper.Wrap(deflate, adler);
        outFs.Write(wrapped, 0, wrapped.Length);
        outFs.Flush(flushToDisk: true);
      }
      return "bitstream transfer (Deflate reframe, gz→zlib)";
    }

    if (src == F.Zlib && dst == F.Gzip) {
      var (deflate, _) = FileFormat.Zlib.ZlibRawHelper.Unwrap(inputData);
      // Need CRC-32 + size of uncompressed data
      var uncompressed = Compression.Core.Deflate.DeflateDecompressor.Decompress(deflate);
      var crc32 = Compression.Core.Checksums.Crc32.Compute(uncompressed);
      using (var outFs = File.Create(outputPath)) {
        var wrapped = FileFormat.Gzip.GzipRawHelper.Wrap(deflate, crc32, (uint)uncompressed.Length);
        outFs.Write(wrapped, 0, wrapped.Length);
        outFs.Flush(flushToDisk: true);
      }
      return "bitstream transfer (Deflate reframe, zlib→gz)";
    }

    return null;
  }

  /// <summary>ZIP (single Deflate entry) → Gzip/Zlib: raw Deflate transfer.</summary>
  private static string? TryZipToStreamRestream(string inputPath, string outputPath, F dst, string? password) {
    using var fs = File.OpenRead(inputPath);
    var r = new FileFormat.Zip.ZipReader(fs, leaveOpen: true, password: password);
    // Only works for single-entry ZIPs with Deflate
    var deflateEntries = r.Entries.Where(e =>
      !e.IsDirectory && e.CompressionMethod == FileFormat.Zip.ZipCompressionMethod.Deflate).ToArray();
    if (deflateEntries.Length != 1) return null;

    var entry = deflateEntries[0];
    var (method, crc32, uncompSize, rawDeflate) = r.ExtractEntryRaw(entry);
    if (method != FileFormat.Zip.ZipCompressionMethod.Deflate) return null;

    if (dst == F.Gzip) {
      using (var outFs = File.Create(outputPath)) {
        FileFormat.Gzip.GzipRawHelper.Wrap(outFs, rawDeflate, crc32, (uint)uncompSize);
        outFs.Flush(flushToDisk: true);
      }
      return "bitstream transfer (ZIP Deflate → Gzip)";
    }

    if (dst == F.Zlib) {
      // Need Adler-32 — must decompress for checksum
      var uncompressed = Compression.Core.Deflate.DeflateDecompressor.Decompress(rawDeflate);
      var adler = Compression.Core.Checksums.Adler32.Compute(uncompressed);
      using (var outFs = File.Create(outputPath)) {
        var wrapped = FileFormat.Zlib.ZlibRawHelper.Wrap(rawDeflate, adler);
        outFs.Write(wrapped, 0, wrapped.Length);
        outFs.Flush(flushToDisk: true);
      }
      return "bitstream transfer (ZIP Deflate → Zlib)";
    }

    return null;
  }

  /// <summary>Gzip/Zlib → ZIP: raw Deflate transfer.</summary>
  private static string? TryStreamToZipRestream(string inputPath, string outputPath, F src, string? password) {
    var inputData = File.ReadAllBytes(inputPath);
    byte[] rawDeflate;
    uint crc32;
    long uncompSize;

    if (src == F.Gzip) {
      var (d, c, s) = FileFormat.Gzip.GzipRawHelper.Unwrap(inputData);
      rawDeflate = d; crc32 = c; uncompSize = s;
    }
    else if (src == F.Zlib) {
      var (d, _) = FileFormat.Zlib.ZlibRawHelper.Unwrap(inputData);
      rawDeflate = d;
      // Need CRC-32 + size — must decompress for checksums
      var uncompressed = Compression.Core.Deflate.DeflateDecompressor.Decompress(rawDeflate);
      crc32 = Compression.Core.Checksums.Crc32.Compute(uncompressed);
      uncompSize = uncompressed.Length;
    }
    else return null;

    using (var outFs = File.Create(outputPath)) {
      var w = new FileFormat.Zip.ZipWriter(outFs, leaveOpen: true, password: password);
      var name = Path.GetFileNameWithoutExtension(inputPath);
      w.AddRawEntry(name, rawDeflate, FileFormat.Zip.ZipCompressionMethod.Deflate, crc32, uncompSize);
      w.Finish();
      outFs.Flush(flushToDisk: true);
    }
    return $"bitstream transfer ({src} Deflate → ZIP)";
  }

  // ── Optimize ──────────────────────────────────────────────────────

  /// <summary>
  /// Optimizes an archive by re-encoding with the best available encoder
  /// while keeping the same format. Uses Zopfli (level Maximum) for Deflate,
  /// Best for LZMA/LZX/etc. The output is fully compatible with standard decoders.
  /// </summary>
  /// <remarks>
  /// Fail-safe: the output is staged to a sibling <c>.tmp</c> file, flushed
  /// to disk, then atomically renamed over <paramref name="outputPath"/>. A
  /// crash during recompression never leaves a partial archive in place.
  /// </remarks>
  /// <returns>(originalSize, optimizedSize, entriesOptimized)</returns>
  public static (long OriginalSize, long OptimizedSize, int EntriesOptimized) Optimize(
      string inputPath, string outputPath, string? password) {
    var format = FormatDetector.Detect(inputPath);
    var originalSize = new FileInfo(inputPath).Length;
    var entries = 0;

    // ── ZIP: re-encode each Deflate entry with Zopfli ────────────────
    if (format == F.Zip) {
      AtomicFileWriter.WriteAtomic(outputPath, outFs => entries = OptimizeZip(inputPath, outFs, password));
      return (originalSize, new FileInfo(outputPath).Length, entries);
    }

    // ── Gzip: re-encode Deflate with Maximum level ───────────────────
    if (format == F.Gzip) {
      var data = DecompressFile(inputPath, F.Gzip);
      AtomicFileWriter.WriteAtomic(outputPath, outFs => {
        using var gs = new FileFormat.Gzip.GzipStream(outFs,
          Compression.Core.Streams.CompressionStreamMode.Compress,
          Compression.Core.Deflate.DeflateCompressionLevel.Maximum,
          leaveOpen: true);
        gs.Write(data);
      });
      return (originalSize, new FileInfo(outputPath).Length, 1);
    }

    // ── Zlib: re-encode Deflate with Maximum level ───────────────────
    if (format == F.Zlib) {
      var data = File.ReadAllBytes(inputPath);
      var decompressed = FileFormat.Zlib.ZlibStream.Decompress(data.AsSpan());
      var recompressed = FileFormat.Zlib.ZlibStream.Compress(decompressed.AsSpan(),
        Compression.Core.Deflate.DeflateCompressionLevel.Maximum);
      AtomicFileWriter.WriteAllBytesAtomic(outputPath, recompressed);
      return (originalSize, new FileInfo(outputPath).Length, 1);
    }

    // ── Compound tar: re-encode outer compression with best level ────
    var comp = FormatDetector.GetTarCompression(format);
    if (comp.HasValue) {
      // Decompress to raw tar, recompress with best settings
      AtomicFileWriter.WriteAtomic(outputPath, outFs => {
        using var inFs = File.OpenRead(inputPath);
        using var rawTar = new MemoryStream();
        DecompressStreamPair(inFs, rawTar, comp.Value);
        rawTar.Position = 0;
        CompressStreamPairOptimal(rawTar, outFs, comp.Value);
      });
      return (originalSize, new FileInfo(outputPath).Length, 1);
    }

    // ── Other stream formats: decompress + recompress with best ──────
    if (FormatDetector.IsStreamFormat(format)) {
      AtomicFileWriter.WriteAtomic(outputPath, outFs => {
        using var inFs = File.OpenRead(inputPath);
        using var raw = new MemoryStream();
        DecompressStreamPair(inFs, raw, format);
        raw.Position = 0;
        CompressStreamPairOptimal(raw, outFs, format);
      });
      return (originalSize, new FileInfo(outputPath).Length, 1);
    }

    // ── Unsupported: fall back to copy ───────────────────────────────
    // Use temp+rename so a crash mid-copy doesn't leave a truncated target.
    AtomicFileWriter.WriteAtomic(outputPath, outFs => {
      using var inFs = File.OpenRead(inputPath);
      inFs.CopyTo(outFs);
    });
    return (originalSize, originalSize, 0);
  }

  private static int OptimizeZip(string inputPath, Stream outFs, string? password) {
    using var inFs = File.OpenRead(inputPath);
    var r = new FileFormat.Zip.ZipReader(inFs, leaveOpen: true, password: password);
    var w = new FileFormat.Zip.ZipWriter(outFs, leaveOpen: true,
      compressionLevel: Compression.Core.Deflate.DeflateCompressionLevel.Maximum,
      password: password);

    var optimized = 0;
    foreach (var entry in r.Entries) {
      if (entry.IsDirectory) {
        w.AddDirectory(entry.FileName, entry.LastModified);
        continue;
      }

      // For Deflate entries: decompress and re-encode with Zopfli (Maximum)
      // For other methods: decompress and re-encode with Deflate Maximum
      var data = r.ExtractEntry(entry);
      w.AddEntry(entry.FileName, data, FileFormat.Zip.ZipCompressionMethod.Deflate, entry.LastModified);
      ++optimized;
    }

    w.Finish();
    return optimized;
  }

  // ── Stream compression dispatch (registry-only) ─────────────────

  private static void DecompressStreamPair(Stream input, Stream output, F format) {
    FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetStreamOps(format.ToString())
      ?? throw new NotSupportedException($"No decompressor for: {format}");
    ops.Decompress(input, output);
  }

  private static void CompressStreamPair(Stream input, Stream output, F format) {
    FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetStreamOps(format.ToString())
      ?? throw new NotSupportedException($"No compressor for: {format}");
    ops.Compress(input, output);
  }

  private static void CompressStreamPairOptimal(Stream input, Stream output, F format) {
    FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetStreamOps(format.ToString())
      ?? throw new NotSupportedException($"No compressor for: {format}");
    // When the format declares an option schema, search its parameter space for
    // the smallest output on this actual data; otherwise use the format's own
    // CompressOptimal heuristic.
    if (ops is Compression.Registry.IFormatOptionsSchema schema && schema.OptionsSchema.Count > 0) {
      using var raw = new MemoryStream();
      input.CopyTo(raw);
      var best = CompressionOptimizer.OptimizeStream(raw.ToArray(), ops, schema);
      output.Write(best.Bytes);
      return;
    }
    ops.CompressOptimal(input, output);
  }

  private static Stream WrapDecompressStream(Stream s, F format) {
    FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetStreamOps(format.ToString());
    if (ops != null) {
      var wrapped = ops.WrapDecompress(s);
      if (wrapped != null) return wrapped;
    }
    throw new NotSupportedException($"No stream decompressor for: {format}");
  }

  // ── Stream helpers ──────────────────────────────────────────────

  private static void ExtractStream(string inputPath, string outputDir, F format) {
    var data = DecompressFile(inputPath, format);
    var outputName = StripCompressionExtension(inputPath);
    File.WriteAllBytes(Path.Combine(outputDir, Path.GetFileName(outputName)), data);
  }

  private static void CompressStream(string inputPath, string outputPath, F format, MethodSpec method = default) {
    AtomicFileWriter.WriteAtomic(outputPath, outFs => {
      using var inFs = File.OpenRead(inputPath);
      if (method.Optimize)
        CompressStreamPairOptimal(inFs, outFs, format);
      else
        CompressStreamPair(inFs, outFs, format);
    });
  }

  public static byte[] DecompressFile(string path, F format) {
    using var fs = File.OpenRead(path);
    using var ms = new MemoryStream();
    DecompressStreamPair(fs, ms, format);
    return ms.ToArray();
  }

  private static void DecompressToNull(Stream input, F format) {
    using var ms = new MemoryStream();
    DecompressStreamPair(input, ms, format);
  }


  // ── FS Toolbox: Convert Cluster/Sector Size ────────────────────────

  /// <summary>
  /// Result of a cluster-size waste analysis for a single cluster size candidate.
  /// </summary>
  public sealed record ClusterWasteInfo(int ClusterSize, long TotalSlack, long TotalAllocated, double SlackPercent);

  /// <summary>
  /// Computes the waste preview (tip slack) for the current and target cluster
  /// sizes of a FAT filesystem image, without modifying the image.
  /// </summary>
  /// <returns>Tuple of (current stats, target stats, list of file sizes).</returns>
  public static (ClusterWasteInfo Current, ClusterWasteInfo Target) PreviewClusterConversion(
      string imagePath, int targetClusterSize) {
    using var stream = File.OpenRead(imagePath);
    var hint = FileSystem.Fat.FatShrinkHelper.AnalyzeClusterSizes(stream);
    var currentStats = hint.AllStats.FirstOrDefault(s => s.ClusterSize == hint.CurrentClusterSize);
    var targetStats = hint.AllStats.FirstOrDefault(s => s.ClusterSize == targetClusterSize);

    var current = currentStats != null
      ? new ClusterWasteInfo(currentStats.ClusterSize, currentStats.TotalSlack, currentStats.TotalAllocated, currentStats.SlackPercent)
      : new ClusterWasteInfo(hint.CurrentClusterSize, 0, 0, 0);
    var target = targetStats != null
      ? new ClusterWasteInfo(targetStats.ClusterSize, targetStats.TotalSlack, targetStats.TotalAllocated, targetStats.SlackPercent)
      : ComputeWasteForSize(imagePath, targetClusterSize);
    return (current, target);
  }

  private static ClusterWasteInfo ComputeWasteForSize(string imagePath, int clusterSize) {
    using var stream = File.OpenRead(imagePath);
    var reader = new FileSystem.Fat.FatReader(stream);
    var totalSlack = 0L;
    var totalAllocated = 0L;
    foreach (var e in reader.Entries) {
      if (e.IsDirectory || e.Size <= 0) continue;
      var clusters = (e.Size + clusterSize - 1) / clusterSize;
      var allocated = clusters * clusterSize;
      totalAllocated += allocated;
      totalSlack += allocated - e.Size;
    }
    var pct = totalAllocated > 0 ? 100.0 * totalSlack / totalAllocated : 0;
    return new ClusterWasteInfo(clusterSize, totalSlack, totalAllocated, pct);
  }

  /// <summary>
  /// Rebuilds a FAT image with a different cluster size. Extracts all files
  /// and recreates the image using <see cref="FileSystem.Fat.FatWriter"/> with
  /// the requested cluster size. The image is written to
  /// <paramref name="outputPath"/> (which may be the same as
  /// <paramref name="inputPath"/> for in-place conversion).
  /// </summary>
  /// <remarks>
  /// Fail-safe: the rebuilt image is staged to a sibling <c>.tmp</c> file,
  /// flushed to disk, then atomically renamed over <paramref name="outputPath"/>.
  /// In-place conversion (input == output) is safe — the source is fully
  /// read into memory before the destination is touched.
  /// </remarks>
  public static void ConvertClusters(string inputPath, string outputPath, int targetClusterSize) {
    // Extract all files from the existing image. Use a using block so the
    // input handle is released before we overwrite the same path (in-place
    // conversion when input == output).
    List<(string Name, byte[] Data)> files;
    int totalSectors;
    using (var inStream = File.OpenRead(inputPath)) {
      var reader = new FileSystem.Fat.FatReader(inStream);
      files = reader.Entries.Where(e => !e.IsDirectory)
                            .Select(e => (e.Name, Data: reader.Extract(e)))
                            .ToList();
      totalSectors = (int)(inStream.Length / 512);
    }

    // Rebuild with the new cluster size.
    var writer = new FileSystem.Fat.FatWriter();
    foreach (var (name, data) in files)
      writer.AddFile(name, data);
    var rebuilt = writer.Build(totalSectors: totalSectors, requestedClusterSize: targetClusterSize);
    AtomicFileWriter.WriteAllBytesAtomic(outputPath, rebuilt);
  }

  // ── FS Toolbox: Resize to Media Profile ───────────────────────────

  /// <summary>
  /// Result of a resize preview: whether the content fits, and the before/after sizes.
  /// </summary>
  public sealed record ResizePreview(long CurrentSize, long TargetSize, long ContentSize, bool Fits);

  /// <summary>
  /// Previews a resize operation without modifying the image. Computes how much
  /// space the live content occupies and whether it fits in the target size.
  /// </summary>
  public static ResizePreview PreviewResize(string imagePath, long targetSize) {
    var currentSize = new FileInfo(imagePath).Length;
    // Compute content size by summing file data from the FS.
    var format = FormatDetector.Detect(imagePath);
    var entries = List(imagePath, null);
    var contentSize = entries.Where(e => !e.IsDirectory).Sum(e => e.OriginalSize);
    // Add ~50% overhead estimate for metadata/FAT tables.
    var estimatedMinSize = contentSize * 3 / 2 + 32768;
    return new ResizePreview(currentSize, targetSize, contentSize, estimatedMinSize <= targetSize);
  }

  /// <summary>
  /// Resizes a filesystem image to the target size. Defragments first to
  /// pack content at the start, then truncates or extends the image.
  /// Updates FS metadata (BPB total sectors for FAT).
  /// </summary>
  /// <exception cref="InvalidOperationException">If the content does not fit
  /// in the target size.</exception>
  public static void Resize(string imagePath, long targetSize) {
    var format = FormatDetector.Detect(imagePath);
    FormatRegistration.EnsureInitialized();
    var descriptor = Compression.Registry.FormatRegistry.GetById(format.ToString());

    // Strategy: extract all files, rebuild at the target size.
    var entries = List(imagePath, null);
    var files = new List<(string Name, byte[] Data)>();
    {
      var tempDir = Path.Combine(Path.GetTempPath(), "cwb_resize_" + Guid.NewGuid().ToString("N")[..8]);
      try {
        Extract(imagePath, tempDir, null, null);
        foreach (var e in entries) {
          if (e.IsDirectory) continue;
          var filePath = Path.Combine(tempDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
          if (File.Exists(filePath))
            files.Add((e.Name, File.ReadAllBytes(filePath)));
        }
      } finally {
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
      }
    }

    var formatId = format.ToString();
    if (formatId == "Fat") {
      var totalSectors = (int)(targetSize / 512);
      var writer = new FileSystem.Fat.FatWriter();
      foreach (var (name, data) in files)
        writer.AddFile(name, data);
      var rebuilt = writer.Build(totalSectors: totalSectors);
      if (rebuilt.Length > targetSize)
        throw new InvalidOperationException(
          $"Content does not fit in target size ({targetSize} bytes). " +
          $"Minimum required: {rebuilt.Length} bytes.");
      // Extend to exact target size if the image is smaller.
      if (rebuilt.Length < targetSize) {
        var padded = new byte[targetSize];
        Array.Copy(rebuilt, padded, rebuilt.Length);
        // Update BPB total sectors to match padded size.
        var paddedTotalSectors = (int)(targetSize / 512);
        if (paddedTotalSectors < 65536) {
          System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(padded.AsSpan(19), (ushort)paddedTotalSectors);
          System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(padded.AsSpan(32), 0u);
        } else {
          System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(padded.AsSpan(19), 0);
          System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(padded.AsSpan(32), (uint)paddedTotalSectors);
        }
        rebuilt = padded;
      }
      AtomicFileWriter.WriteAllBytesAtomic(imagePath, rebuilt);
    } else if (formatId is "Ext" or "Ext1") {
      var blockSize = 1024;
      var totalBlocks = (int)(targetSize / blockSize);
      var writer = new FileSystem.Ext.ExtWriter();
      foreach (var (name, data) in files)
        writer.AddFile(name, data);
      var rebuilt = writer.Build(blockSize: blockSize, totalBlocks: totalBlocks);
      if (rebuilt.Length > targetSize)
        throw new InvalidOperationException(
          $"Content does not fit in target size ({targetSize} bytes). " +
          $"Minimum required: {rebuilt.Length} bytes.");
      AtomicFileWriter.WriteAllBytesAtomic(imagePath, rebuilt);
    } else {
      throw new NotSupportedException($"Resize is not supported for format: {formatId}. Supported: Fat, Ext.");
    }
  }

  // ── Cross-Format Conversion ────────────────────────────────────────

  /// <summary>
  /// Backwards-compatible alias for <see cref="ConvertArchive"/>.
  /// Older callers and tests use this name from when the feature was
  /// FS-image-only; the implementation now lives in
  /// <see cref="ConvertArchive"/> and accepts any archive/filesystem
  /// source and any creatable target.
  /// </summary>
  public static List<string> ConvertFs(string inputPath, string outputPath, string? targetFormatId = null)
    => ConvertArchive(inputPath, outputPath, targetFormatId, createOptions: null);

  /// <summary>
  /// Back-compat overload for the original 2-/3-arg signature. Older callers
  /// (tests, UI before the options dialog was added) keep working unchanged;
  /// no <c>FormatCreateOptions</c> is threaded, so target writers fall back
  /// to their schema defaults.
  /// </summary>
  public static List<string> ConvertArchive(string inputPath, string outputPath, string? explicitTargetFormat = null)
    => ConvertArchive(inputPath, outputPath, explicitTargetFormat, createOptions: null);

  /// <summary>
  /// Converts between ANY listable/creatable format pair. Works across format
  /// categories:
  /// <list type="bullet">
  ///   <item>FS → FS (e.g. D64 → FAT)</item>
  ///   <item>FS → Archive (e.g. D64 → ZIP, FAT → 7z)</item>
  ///   <item>Archive → FS (e.g. ZIP → FAT image)</item>
  ///   <item>Archive → Archive (e.g. ZIP → TAR, 7z → ZIP)</item>
  /// </list>
  /// Dispatch order:
  /// <list type="number">
  ///   <item>Same-format / FAT-variant / ext-variant pair: routed through
  ///   <see cref="FsConversion.InPlaceConverter"/> for the metadata-only
  ///   fast path. If the in-place converter declines (e.g. NoOp or
  ///   NotSupported), we fall through to the extract+rebuild path.</item>
  ///   <item>Anything else: extract every entry from the source (via
  ///   <see cref="Extract"/>) then create the target (via <see cref="Create"/>).
  ///   Any format that supports <c>List</c> + <c>Extract</c> works as a
  ///   source; any format that implements
  ///   <see cref="Compression.Registry.IArchiveCreatable"/> works as a target.</item>
  /// </list>
  /// </summary>
  /// <param name="inputPath">Path to the source image / archive.</param>
  /// <param name="outputPath">Path for the output (extension determines format
  /// unless <paramref name="explicitTargetFormat"/> is set).</param>
  /// <param name="explicitTargetFormat">Explicit target format ID (e.g. "Fat",
  /// "Ext", "Zip", "SevenZip"). When null, the format is inferred from
  /// <paramref name="outputPath"/>'s extension.</param>
  /// <returns>List of metadata-loss warnings (empty if no metadata was lost).</returns>
  /// <remarks>
  /// Fail-safe: dispatches to <see cref="Create(string, IReadOnlyList{ArchiveInput}, CompressionOptions, FormatDetector.Format)"/>
  /// which stages the output to a sibling <c>.tmp</c> file and atomically
  /// renames it into place. The temp extraction directory is always cleaned
  /// up in a finally block. The source file is never deleted, even on
  /// successful conversion.
  /// </remarks>
  public static List<string> ConvertArchive(string inputPath, string outputPath,
                                            string? explicitTargetFormat,
                                            Compression.Registry.FormatCreateOptions? createOptions) {
    var targetFormatId = explicitTargetFormat;
    var warnings = new List<string>();

    // Detect source format.
    var srcFormat = FormatDetector.Detect(inputPath);
    FormatRegistration.EnsureInitialized();

    // Determine target format.
    FormatDetector.Format dstFormat;
    if (!string.IsNullOrEmpty(targetFormatId)) {
      if (!Enum.TryParse<FormatDetector.Format>(targetFormatId, ignoreCase: true, out dstFormat))
        throw new NotSupportedException($"Unknown target format: {targetFormatId}");
    } else {
      dstFormat = FormatDetector.DetectByExtension(outputPath);
    }

    if (dstFormat == FormatDetector.Format.Unknown)
      throw new NotSupportedException($"Cannot determine target format from extension: {Path.GetExtension(outputPath)}");

    // Check that the target supports creation.
    var dstOps = Compression.Registry.FormatRegistry.GetArchiveOps(dstFormat.ToString());
    if (dstOps is not Compression.Registry.IArchiveCreatable)
      throw new NotSupportedException($"Target format {dstFormat} does not support creation.");

    // ── Fast path: in-place variant conversion ──────────────────────
    // FAT12↔16↔32 and ext2↔3↔4 only rewrite metadata; the FsConversion
    // InPlaceConverter handles them with no extract/rebuild. We honor the
    // existing fast path so callers that previously relied on ConvertFs
    // for variant-only conversions don't regress.
    //
    // If the user supplied format-specific tunables (cluster size, label,
    // etc.) the in-place converter can't honor them — it only flips
    // metadata. Fall through to extract+rebuild so the schema knobs
    // actually take effect.
    var hasFormatSpecific = createOptions?.FormatSpecific is { Count: > 0 };
    if (!hasFormatSpecific && TryInPlaceConvert(inputPath, outputPath, srcFormat, dstFormat))
      return warnings;

    // Extract from source.
    var srcEntries = List(inputPath, null);
    var dstCreatable = (Compression.Registry.IArchiveCreatable)dstOps;

    // ── Tiny-source optimization (≤ 32 MiB): in-memory pipeline ───────
    // Buffering the whole source as byte[]s is faster than seek-heavy
    // streaming when the source easily fits in RAM. Bounded streaming
    // (the universal architecture) takes over for everything larger.
    var srcSize = new FileInfo(inputPath).Length;
    const long InMemoryOptimizationCeiling = 32L * 1024 * 1024;
    if (srcSize <= InMemoryOptimizationCeiling &&
        Compression.Lib.InMemoryProcessing.FitsInMemory(srcSize)) {
      AddConversionWarnings(srcFormat, dstFormat, srcEntries, warnings);

      var inputs = ExtractAllToMemory(inputPath, null);

      var dstDescriptorMem = Compression.Registry.FormatRegistry.GetById(dstFormat.ToString());
      if (dstDescriptorMem != null &&
          (dstDescriptorMem.Capabilities & Compression.Registry.FormatCapabilities.SupportsDirectories) == 0) {
        inputs = inputs.Where(i => !i.IsDirectory).ToList();
      }

      var memOpts = new Compression.Registry.FormatCreateOptions {
        FormatSpecific = createOptions?.FormatSpecific,
      };
      Compression.Lib.InMemoryProcessing.RebuildToFileAtomic(outputPath, dstCreatable, inputs, memOpts);
      return warnings;
    }

    // ── Bounded streaming pipeline ────────────────────────────────────
    // Source → target without any per-entry buffering. The source's
    // OpenEntry produces a BoundedEntryStream sized to the entry's
    // logical bytes (so slack / adjacent entries / padding are
    // physically unreachable); the target's CreateFromStreams either
    // streams those bytes through (FAT two-pass) or, for descriptors
    // that haven't overridden it, falls back to buffer-per-entry +
    // classic Create. Either way no whole-source tempdir is involved.
    AddConversionWarnings(srcFormat, dstFormat, srcEntries, warnings);

    var dstDescriptor = Compression.Registry.FormatRegistry.GetById(dstFormat.ToString());
    var supportsDirs = dstDescriptor != null &&
      (dstDescriptor.Capabilities & Compression.Registry.FormatCapabilities.SupportsDirectories) != 0;

    var srcOps = Compression.Registry.FormatRegistry.GetArchiveOps(srcFormat.ToString())
      ?? throw new NotSupportedException($"Cannot list format: {srcFormat}");

    // Open the source stream ONCE; the OpenEntry factories share it.
    // Per-entry streams use Position/Read against the same underlying
    // FileStream, which is safe because the writer reads each entry
    // stream to completion before requesting the next one.
    using var sharedSrc = File.OpenRead(inputPath);
    var streamingInputs = srcEntries
      .Where(e => !e.IsDirectory || supportsDirs)
      .Select(e => new Compression.Registry.Streaming.StreamingArchiveInput(
        Name: e.Name,
        Size: e.OriginalSize,
        IsDirectory: e.IsDirectory,
        OpenStream: e.IsDirectory
          ? () => new MemoryStream(System.Array.Empty<byte>(), writable: false)
          : () => srcOps.OpenEntry(sharedSrc, e.Name, null)));

    var streamOpts = new Compression.Registry.FormatCreateOptions {
      FormatSpecific = createOptions?.FormatSpecific,
    };
    AtomicFileWriter.WriteAtomic(outputPath,
      fs => dstCreatable.CreateFromStreams(fs, streamingInputs, streamOpts));

    return warnings;
  }

  /// <summary>
  /// Reads an archive / FS image entirely into memory: every entry is extracted
  /// via <see cref="IArchiveFormatOperations.ExtractEntryToMemory"/> and wrapped
  /// as an <see cref="Compression.Registry.ArchiveInputInfo.InMemory(string, byte[])"/>
  /// input. Directory entries pass through as in-memory placeholders. The source
  /// FileStream stays open for the duration of the loop so per-entry overrides
  /// that don't rewind can still read sequentially.
  /// </summary>
  public static IReadOnlyList<Compression.Registry.ArchiveInputInfo> ExtractAllToMemory(
      string path, string? password) {
    var format = FormatDetector.Detect(path);
    FormatRegistration.EnsureInitialized();
    var srcOps = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString())
      ?? throw new NotSupportedException($"Cannot list format: {format}");

    using var stream = File.OpenRead(path);
    var entries = srcOps.List(stream, password);
    var result = new List<Compression.Registry.ArchiveInputInfo>(entries.Count);
    foreach (var e in entries) {
      if (e.IsDirectory) {
        result.Add(new Compression.Registry.ArchiveInputInfo(
          FullPath: e.Name, ArchiveName: e.Name, IsDirectory: true));
        continue;
      }
      var bytes = srcOps.ExtractEntryToMemory(stream, e.Name, password);
      result.Add(Compression.Registry.ArchiveInputInfo.InMemory(e.Name, bytes));
    }
    return result;
  }

  /// <summary>
  /// Appends cross-category, short-name and timestamp-loss warnings to the
  /// supplied list. Shared between the in-memory and disk-tempdir branches of
  /// <see cref="ConvertArchive(string, string, string?, Compression.Registry.FormatCreateOptions?)"/>
  /// so both code paths report identical metadata-loss diagnostics regardless
  /// of which branch ran.
  /// </summary>
  private static void AddConversionWarnings(
      F srcFormat, F dstFormat,
      IReadOnlyList<ArchiveEntry> srcEntries,
      List<string> warnings) {
    var dstId = dstFormat.ToString();

    // Timestamp-loss warning: source has dates, target is a retro FS that doesn't.
    var srcHasTimestamps = srcEntries.Any(e => e.LastModified.HasValue);
    var retroFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
      "D64", "D71", "D81", "T64", "Adf", "AppleDos", "Atari8", "Bbc",
      "Cpm", "ZxScl", "TrDos", "Msa", "Mfs", "CpcDsk",
    };
    if (srcHasTimestamps && retroFormats.Contains(dstId))
      warnings.Add($"Target format {dstFormat} does not support timestamps; file dates will be lost.");

    // Short-name truncation warning: target has a ≤16-char name limit.
    var shortNameFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
      "D64", "D71", "D81", "Cpm", "Atari8",
    };
    if (shortNameFormats.Contains(dstId)) {
      foreach (var e in srcEntries) {
        if (!e.IsDirectory && e.Name.Length > 16)
          warnings.Add($"File name '{e.Name}' may be truncated in {dstFormat}.");
      }
    }

    // Cross-category warning: filesystem-image ↔ archive-container conversions.
    var filesystemFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
      "Fat", "ExFat", "Ntfs", "Ext", "Ext1", "Btrfs", "Xfs", "Hfs", "HfsPlus",
      "Apfs", "Mfs", "Iso", "Udf", "Zfs", "Ufs", "Jfs", "ReiserFs", "Reiser4",
      "F2fs", "SquashFs", "CramFs", "RomFs", "MinixFs", "D64", "D71", "D81",
      "T64", "Adf", "AppleDos", "ProDos", "Atari8", "Bbc", "Cpm", "CpcDsk",
      "ZxScl", "TrDos", "Msa", "Hpfs", "DoubleSpace", "Vdfs", "Bfs", "Ocfs2",
      "Jffs2", "Yaffs2", "BcacheFs",
    };
    var srcIsFs = filesystemFormats.Contains(srcFormat.ToString());
    var dstIsFs = filesystemFormats.Contains(dstId);
    if (srcIsFs != dstIsFs)
      warnings.Add($"Cross-category conversion: {srcFormat} ({(srcIsFs ? "filesystem" : "archive")}) -> {dstFormat} ({(dstIsFs ? "filesystem" : "archive")}).");
  }

  /// <summary>
  /// Attempts the in-place fast path for variant conversions
  /// (FAT12↔16↔32, ext2↔3↔4). Returns true if the conversion was handled
  /// in-place; false to let the caller fall through to the
  /// extract+rebuild path. Same-format conversions (Fat→Fat etc.) also
  /// flow through here as a copy-only optimization when the variant is
  /// not specified or already matches.
  /// </summary>
  /// <remarks>
  /// The InPlaceConverter needs the image opened R/W. For safety we stage
  /// a copy under a temp path, run the conversion against it, then
  /// atomically replace the target. This guarantees a crash mid-conversion
  /// can't tear the destination if it already existed.
  /// </remarks>
  private static bool TryInPlaceConvert(string inputPath, string outputPath, F srcFormat, F dstFormat) {
    var srcId = srcFormat.ToString();
    var dstId = dstFormat.ToString();

    // Only attempt for the FAT / ext families that the InPlaceConverter
    // understands. Other family-internal conversions (e.g. ext1→ext) are
    // not yet supported by the in-place path and just fall through.
    var srcIsFat = srcId.StartsWith("Fat", StringComparison.OrdinalIgnoreCase);
    var dstIsFat = dstId.StartsWith("Fat", StringComparison.OrdinalIgnoreCase);
    var srcIsExt = srcId.StartsWith("Ext", StringComparison.OrdinalIgnoreCase);
    var dstIsExt = dstId.StartsWith("Ext", StringComparison.OrdinalIgnoreCase);
    if (!((srcIsFat && dstIsFat) || (srcIsExt && dstIsExt))) return false;

    // Stage the source into the destination temp path, then run the
    // converter against that stream. AtomicFileWriter.ReplaceTarget will
    // do the atomic rename once we're done.
    var tempPath = AtomicFileWriter.MakeTempPath(outputPath);
    var handled = false;
    try {
      File.Copy(inputPath, tempPath, overwrite: true);

      FormatRegistration.EnsureInitialized();
      using (var fs = File.Open(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
        var result = FsConversion.InPlaceConverter.TryConvert(fs, srcId, dstId);
        switch (result) {
          case FsConversion.InPlaceConversionResult.Succeeded:
          case FsConversion.InPlaceConversionResult.NoOp:
            fs.Flush();
            handled = true;
            break;
          case FsConversion.InPlaceConversionResult.GeometryRejected:
          case FsConversion.InPlaceConversionResult.NotSupported:
          default:
            // Fall back to the generic extract+rebuild path.
            handled = false;
            break;
        }
      }

      if (handled) {
        AtomicFileWriter.ReplaceTarget(tempPath, outputPath);
        return true;
      }
      return false;
    }
    catch {
      AtomicFileWriter.TryDelete(tempPath);
      throw;
    }
    finally {
      // If we declined the in-place conversion, drop the temp copy so
      // the caller's extract+rebuild path doesn't trip over orphan files.
      if (!handled) AtomicFileWriter.TryDelete(tempPath);
    }
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static void WriteFile(string baseDir, string entryName, byte[] data) {
    var safeName = entryName.Replace('\\', '/').TrimStart('/');
    if (safeName.Contains("..")) safeName = Path.GetFileName(safeName);
    var fullPath = Path.Combine(baseDir, safeName);
    var dir = Path.GetDirectoryName(fullPath);
    if (dir != null) Directory.CreateDirectory(dir);
    File.WriteAllBytes(fullPath, data);
  }

  private static bool MatchesFilter(string name, string[] filters)
    => filters.Any(f => name.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("/" + f, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(name).Equals(f, StringComparison.OrdinalIgnoreCase));

  private static string StripCompressionExtension(string path) {
    var name = Path.GetFileName(path);
    var ext = Path.GetExtension(name);
    return FormatDetector.IsStreamExtension(ext) ? Path.GetFileNameWithoutExtension(name) : name;
  }

  /// <summary>
  /// Extracts a single entry from an archive and returns its contents as a byte array.
  /// </summary>
  public static byte[] ExtractEntry(string archivePath, string entryPath, string? password) {
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_preview_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tempDir);
      Extract(archivePath, tempDir, password, [entryPath]);
      // The entry may contain path separators; find the file on disk
      var file = Path.Combine(tempDir, entryPath.Replace('/', Path.DirectorySeparatorChar));
      return File.Exists(file) ? File.ReadAllBytes(file) : [];
    }
    finally {
      try { Directory.Delete(tempDir, true); } catch { }
    }
  }
}
