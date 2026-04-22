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

    if (FormatDetector.IsStreamFormat(format)) {
      ExtractStream(path, outputDir, format);
      return;
    }

    using var fs = File.OpenRead(path);
    ExtractWithStream(format, fs, outputDir, password, files);
  }

  private static void ExtractWithStream(F format, Stream fs, string outputDir, string? password, string[]? files) {
    FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());
    if (ops != null) { ops.Extract(fs, outputDir, password, files); return; }

    throw new NotSupportedException($"Cannot extract format: {format}");
  }

  public static void Create(string outputPath, IReadOnlyList<ArchiveInput> inputs, CompressionOptions opts) {
    var method = opts.Method.Name == null ? MethodSpec.Default : opts.Method;
    var password = opts.Password;
    var format = FormatDetector.DetectByExtension(outputPath);
    if (format == F.Unknown)
      throw new NotSupportedException($"Cannot determine format from extension: {Path.GetExtension(outputPath)}");

    // Build the set of incompressible file paths for entropy-aware formats
    HashSet<string>? incompressible = null;
    if (!opts.ForceCompress) {
      incompressible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var i in inputs) {
        if (!i.IsDirectory && !string.IsNullOrEmpty(i.FullPath) && EntropyDetector.IsIncompressible(i.FullPath))
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

    using var outFs = File.Create(outputPath);

    // Complex formats that depend on Compression.Lib internals (MethodSpec, SolidBlockPlanner, etc.)
    switch (format) {
      case F.Zip: CreateZip(outFs, inputs, password, method, incompressible, opts); return;
      case F.SevenZip: Create7z(outFs, inputs, password, opts); return;
      case F.Rar: CreateRar(outFs, inputs, opts); return;
    }

    // All other formats dispatch through the registry
    FormatRegistration.EnsureInitialized();
    var ops = Compression.Registry.FormatRegistry.GetArchiveOps(format.ToString());
    if (ops is Compression.Registry.IArchiveCreatable creator) {
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
      };
      creator.Create(outFs, registryInputs, registryOpts);
      return;
    }

    throw new NotSupportedException($"Format {format} has no creatable descriptor");
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
  public static (string Strategy, int Tier) Convert(string inputPath, string outputPath,
      string? password, MethodSpec method = default) {
    if (method.Name == null) method = MethodSpec.Default;
    var srcFormat = FormatDetector.Detect(inputPath);
    var dstFormat = FormatDetector.DetectByExtension(outputPath);

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
        using var inFs = File.OpenRead(inputPath);
        using var outFs = File.Create(outputPath);
        using var decompressed = new MemoryStream();
        DecompressStreamPair(inFs, decompressed, srcComp.Value);
        decompressed.Position = 0;
        CompressStreamPair(decompressed, outFs, dstComp.Value);
        return ("tar passthrough, swap outer compression", 2);
      }

      // Tier 2b: compound tar → plain tar (just strip outer compression)
      if (srcComp.HasValue && dstFormat == F.Tar) {
        using var inFs = File.OpenRead(inputPath);
        using var outFs = File.Create(outputPath);
        DecompressStreamPair(inFs, outFs, srcComp.Value);
        return ("unwrap outer compression", 2);
      }

      // Tier 2c: plain tar → compound tar (just add outer compression)
      if (srcFormat == F.Tar && dstComp.HasValue) {
        using var inFs = File.OpenRead(inputPath);
        using var outFs = File.Create(outputPath);
        CompressStreamPair(inFs, outFs, dstComp.Value);
        return ("wrap with outer compression", 2);
      }

      // Tier 2d: stream → stream with different codec (decompress + recompress content)
      if (FormatDetector.IsStreamFormat(srcFormat) && FormatDetector.IsStreamFormat(dstFormat)) {
        using var inFs = File.OpenRead(inputPath);
        using var raw = new MemoryStream();
        DecompressStreamPair(inFs, raw, srcFormat);
        raw.Position = 0;
        using var outFs = File.Create(outputPath);
        CompressStreamPair(raw, outFs, dstFormat);
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
      Create(outputPath, inputs, new CompressionOptions { Method = method, Password = password });
    }
    finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
    var label = method.Optimize ? $"full recompress ({method})" : "full recompress";
    return (label, 3);
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
      File.WriteAllBytes(outputPath, FileFormat.Zlib.ZlibRawHelper.Wrap(deflate, adler));
      return "bitstream transfer (Deflate reframe, gz→zlib)";
    }

    if (src == F.Zlib && dst == F.Gzip) {
      var (deflate, _) = FileFormat.Zlib.ZlibRawHelper.Unwrap(inputData);
      // Need CRC-32 + size of uncompressed data
      var uncompressed = Compression.Core.Deflate.DeflateDecompressor.Decompress(deflate);
      var crc32 = Compression.Core.Checksums.Crc32.Compute(uncompressed);
      File.WriteAllBytes(outputPath, FileFormat.Gzip.GzipRawHelper.Wrap(deflate, crc32, (uint)uncompressed.Length));
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
      using var outFs = File.Create(outputPath);
      FileFormat.Gzip.GzipRawHelper.Wrap(outFs, rawDeflate, crc32, (uint)uncompSize);
      return "bitstream transfer (ZIP Deflate → Gzip)";
    }

    if (dst == F.Zlib) {
      // Need Adler-32 — must decompress for checksum
      var uncompressed = Compression.Core.Deflate.DeflateDecompressor.Decompress(rawDeflate);
      var adler = Compression.Core.Checksums.Adler32.Compute(uncompressed);
      File.WriteAllBytes(outputPath, FileFormat.Zlib.ZlibRawHelper.Wrap(rawDeflate, adler));
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

    using var outFs = File.Create(outputPath);
    var w = new FileFormat.Zip.ZipWriter(outFs, leaveOpen: true, password: password);
    var name = Path.GetFileNameWithoutExtension(inputPath);
    w.AddRawEntry(name, rawDeflate, FileFormat.Zip.ZipCompressionMethod.Deflate, crc32, uncompSize);
    w.Finish();
    return $"bitstream transfer ({src} Deflate → ZIP)";
  }

  // ── Optimize ──────────────────────────────────────────────────────

  /// <summary>
  /// Optimizes an archive by re-encoding with the best available encoder
  /// while keeping the same format. Uses Zopfli (level Maximum) for Deflate,
  /// Best for LZMA/LZX/etc. The output is fully compatible with standard decoders.
  /// </summary>
  /// <returns>(originalSize, optimizedSize, entriesOptimized)</returns>
  public static (long OriginalSize, long OptimizedSize, int EntriesOptimized) Optimize(
      string inputPath, string outputPath, string? password) {
    var format = FormatDetector.Detect(inputPath);
    var originalSize = new FileInfo(inputPath).Length;

    // ── ZIP: re-encode each Deflate entry with Zopfli ────────────────
    if (format == F.Zip) {
      var count = OptimizeZip(inputPath, outputPath, password);
      return (originalSize, new FileInfo(outputPath).Length, count);
    }

    // ── Gzip: re-encode Deflate with Maximum level ───────────────────
    if (format == F.Gzip) {
      var data = DecompressFile(inputPath, F.Gzip);
      using (var outFs = File.Create(outputPath)) {
        using var gs = new FileFormat.Gzip.GzipStream(outFs,
          Compression.Core.Streams.CompressionStreamMode.Compress,
          Compression.Core.Deflate.DeflateCompressionLevel.Maximum,
          leaveOpen: true);
        gs.Write(data);
      }
      return (originalSize, new FileInfo(outputPath).Length, 1);
    }

    // ── Zlib: re-encode Deflate with Maximum level ───────────────────
    if (format == F.Zlib) {
      var data = File.ReadAllBytes(inputPath);
      var decompressed = FileFormat.Zlib.ZlibStream.Decompress(data.AsSpan());
      var recompressed = FileFormat.Zlib.ZlibStream.Compress(decompressed.AsSpan(),
        Compression.Core.Deflate.DeflateCompressionLevel.Maximum);
      File.WriteAllBytes(outputPath, recompressed);
      return (originalSize, new FileInfo(outputPath).Length, 1);
    }

    // ── Compound tar: re-encode outer compression with best level ────
    var comp = FormatDetector.GetTarCompression(format);
    if (comp.HasValue) {
      // Decompress to raw tar, recompress with best settings
      using var inFs = File.OpenRead(inputPath);
      using var rawTar = new MemoryStream();
      DecompressStreamPair(inFs, rawTar, comp.Value);
      rawTar.Position = 0;
      using var outFs = File.Create(outputPath);
      CompressStreamPairOptimal(rawTar, outFs, comp.Value);
      return (originalSize, new FileInfo(outputPath).Length, 1);
    }

    // ── Other stream formats: decompress + recompress with best ──────
    if (FormatDetector.IsStreamFormat(format)) {
      using var inFs = File.OpenRead(inputPath);
      using var raw = new MemoryStream();
      DecompressStreamPair(inFs, raw, format);
      raw.Position = 0;
      using var outFs = File.Create(outputPath);
      CompressStreamPairOptimal(raw, outFs, format);
      return (originalSize, new FileInfo(outputPath).Length, 1);
    }

    // ── Unsupported: fall back to copy ───────────────────────────────
    File.Copy(inputPath, outputPath, overwrite: true);
    return (originalSize, originalSize, 0);
  }

  private static int OptimizeZip(string inputPath, string outputPath, string? password) {
    using var inFs = File.OpenRead(inputPath);
    var r = new FileFormat.Zip.ZipReader(inFs, leaveOpen: true, password: password);
    using var outFs = File.Create(outputPath);
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
    using var inFs = File.OpenRead(inputPath);
    using var outFs = File.Create(outputPath);
    if (method.Optimize)
      CompressStreamPairOptimal(inFs, outFs, format);
    else
      CompressStreamPair(inFs, outFs, format);
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

  // ── Complex Create methods (depend on Compression.Lib internals) ─

  private static void CreateZip(Stream s, IReadOnlyList<ArchiveInput> inputs, string? pw,
      MethodSpec method, HashSet<string>? incompressible, CompressionOptions opts) {
    var (zipMethod, level) = method.ResolveZip();
    // Override Deflate level from --level if provided
    if (opts.Level.HasValue) level = opts.ResolveDeflateLevel();

    var zipEnc = opts.ResolveZipEncryption();

    if (opts.Threads > 1 && inputs.Count(i => !i.IsDirectory) > 1) {
      ParallelCompression.CreateZipParallel(s, inputs, pw, zipMethod, level, incompressible, opts.Threads, zipEnc);
      return;
    }

    var w = new FileFormat.Zip.ZipWriter(s, leaveOpen: true, compressionLevel: level, password: pw,
      encryptionMethod: zipEnc);
    // Wire per-method settings from CLI options
    if (opts.DictSize > 0 && zipMethod == FileFormat.Zip.ZipCompressionMethod.Lzma)
      w.LzmaDictionarySize = CompressionOptions.NormalizeDictSize(opts.ResolveLzmaDictSize());
    w.LzmaLevel = opts.ResolveLzmaLevel();
    if (opts.WordSize.HasValue && zipMethod == FileFormat.Zip.ZipCompressionMethod.Ppmd)
      w.PpmdOrder = Math.Clamp(opts.WordSize.Value, 2, 16);
    if (opts.DictSize > 0 && zipMethod == FileFormat.Zip.ZipCompressionMethod.Ppmd)
      w.PpmdMemorySizeMB = Math.Clamp((int)(opts.DictSize / (1024 * 1024)), 1, 256);
    if (opts.DictSize > 0 && zipMethod == FileFormat.Zip.ZipCompressionMethod.BZip2)
      w.Bzip2BlockSize = opts.ResolveBzip2BlockSize();
    foreach (var i in inputs) {
      if (i.IsDirectory) { w.AddDirectory(i.EntryName); continue; }
      var data = File.ReadAllBytes(i.FullPath);
      var entryMethod = incompressible != null && incompressible.Contains(i.FullPath)
        ? FileFormat.Zip.ZipCompressionMethod.Store
        : zipMethod;
      w.AddEntry(i.EntryName, data, entryMethod);
    }
    w.Finish();
  }

  private static void CreateRar(Stream s, IReadOnlyList<ArchiveInput> inputs, CompressionOptions opts) {
    var pw = opts.Password;
    var useRar4 = opts.Method.Name == "rar4";
    var useStore = opts.Method.Name is "store" or "copy";
    // RAR method levels: 0=Store,1=Fastest,2=Fast,3=Normal,4=Good,5=Best
    var rarLevel = useStore ? 0 : opts.Level switch {
      0 => 0, 1 => 1, 2 => 2, 3 or 4 => 3, 5 or 6 => 4, >= 7 => 5, _ => 3,
    };

    if (useRar4) {
      var windowBits = opts.DictSize > 0
        ? Math.Clamp((int)Math.Log2(opts.DictSize), 15, 22) : 20;
      // RAR4 methods: 0x30=Store..0x35=Best
      var rar4Method = (byte)(0x30 + rarLevel);
      var w4 = new FileFormat.Rar.Rar4Writer(s, method: rar4Method, windowBits: windowBits,
        solid: opts.SolidSize == 0, password: pw);
      foreach (var (name, data) in ArchiveInput.FilesOnly(inputs))
        w4.AddFile(name, data);
      w4.Finish();
    }
    else {
      var dictLog = opts.DictSize > 0
        ? Math.Clamp((int)Math.Log2(opts.DictSize), 17, 28) : 17;
      var w = new FileFormat.Rar.RarWriter(s, method: rarLevel, dictionarySizeLog: dictLog,
        solid: opts.SolidSize == 0, password: pw, encryptHeaders: opts.EncryptFilenames);
      foreach (var (name, data) in ArchiveInput.FilesOnly(inputs))
        w.AddFile(name, data);
      w.Finish();
    }
  }

  private static void Create7z(Stream s, IReadOnlyList<ArchiveInput> inputs, string? pw,
      CompressionOptions opts) {
    var method = opts.Method;
    var defaultCodec = method.IsDefault ? FileFormat.SevenZip.SevenZipCodec.Lzma2 : method.Resolve7z();
    var dictSize = defaultCodec == FileFormat.SevenZip.SevenZipCodec.PPMd
      ? opts.ResolvePpmdMemorySize()
      : defaultCodec == FileFormat.SevenZip.SevenZipCodec.BZip2
        ? opts.ResolveBzip2BlockSize() * 100 * 1024
        : opts.ResolveLzmaDictSize();
    var ppmdOrder = opts.ResolvePpmdOrder();
    var ppmdMem = opts.ResolvePpmdMemorySize();
    var blockSize = opts.SolidSize > 0 ? opts.SolidSize : SolidBlockPlanner.DefaultMaxBlockSize;

    // Detect incompressible files via entropy analysis
    var incompressible = opts.ForceCompress ? null : SolidBlockPlanner.DetectIncompressible(inputs);

    // Plan solid blocks: group by extension similarity, separate incompressible
    var solidBlocks = SolidBlockPlanner.Plan(inputs, blockSize, incompressible);

    // Check if any block needs a different codec/filter than default
    var needsMultiCodec = solidBlocks.Any(b =>
      SolidBlockPlanner.RecommendCodec(b, defaultCodec) != defaultCodec ||
      SolidBlockPlanner.RecommendFilter(b) != FileFormat.SevenZip.SevenZipFilter.None);

    var w = new FileFormat.SevenZip.SevenZipWriter(s, defaultCodec, dictionarySize: dictSize,
      ppmdOrder: ppmdOrder, ppmdMemorySize: ppmdMem, password: pw,
      encryptHeaders: opts.EncryptFilenames);

    // Add directory entries
    foreach (var i in inputs)
      if (i.IsDirectory) w.AddDirectory(i.EntryName);

    // Add file entries in solid-block order (grouped by extension similarity)
    var fileEntryIndex = 0;
    var blockDescs = new List<FileFormat.SevenZip.SevenZipWriter.BlockDescriptor>();
    foreach (var block in solidBlocks) {
      var indices = new int[block.Files.Count];
      for (var j = 0; j < block.Files.Count; j++) {
        var (input, data) = block.Files[j];
        w.AddEntry(new FileFormat.SevenZip.SevenZipEntry { Name = input.EntryName, Size = data.Length }, data);
        indices[j] = fileEntryIndex++;
      }

      if (needsMultiCodec) {
        var blockCodec = SolidBlockPlanner.RecommendCodec(block, defaultCodec);
        var blockFilter = SolidBlockPlanner.RecommendFilter(block);
        blockDescs.Add(new FileFormat.SevenZip.SevenZipWriter.BlockDescriptor {
          EntryIndices = indices,
          Codec = blockCodec != defaultCodec ? blockCodec : null,
          Filter = blockFilter != FileFormat.SevenZip.SevenZipFilter.None ? blockFilter : null,
        });
      }
    }

    if (needsMultiCodec) {
      w.FinishWithBlocks(blockDescs, maxThreads: opts.Threads);
    }
    else {
      w.Finish(maxThreads: opts.Threads, maxBlockSize: opts.Threads > 1 ? blockSize : 0);
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
