using F = Compression.Lib.FormatDetector.Format;

namespace Compression.Lib;

/// <summary>
/// Unified operations across all archive and stream formats.
/// </summary>
internal static class ArchiveOperations {

  internal static List<ArchiveEntry> List(string path, string? password) {
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
    return format switch {
      F.Zip => ListZip(fs, password),
      F.Rar => ListRar(fs, password),
      F.SevenZip => List7z(fs, password),
      F.Tar => ListTar(fs),
      F.TarGz => ListTarCompressed(fs, F.Gzip),
      F.TarBz2 => ListTarCompressed(fs, F.Bzip2),
      F.TarXz => ListTarCompressed(fs, F.Xz),
      F.TarZst => ListTarCompressed(fs, F.Zstd),
      F.TarLz4 => ListTarLz4(fs),
      F.TarLzip => ListTarLzip(fs),
      F.TarBr => ListTarBr(fs),
      F.Vpk => ListVpk(fs),
      F.Bsa => ListBsa(fs),
      F.Mpq => ListMpq(fs),
      F.Cab => ListCab(fs),
      F.Lzh => ListLzh(fs),
      F.Arj => ListArj(fs, password),
      F.Arc => ListArc(fs),
      F.Zoo => ListZoo(fs),
      F.Ace => ListAce(fs, password),
      F.Sqx => ListSqx(fs, password),
      F.Cpio => ListCpio(fs),
      F.Ar => ListAr(fs),
      F.Wim => ListWim(fs),
      F.Deb => ListDeb(fs),
      F.Shar => ListShar(fs),
      F.Pak => ListPak(fs),
      F.Ha => ListHa(fs),
      F.SquashFs => ListSquashFs(fs),
      F.CramFs => ListCramFs(fs),
      F.StuffIt => ListStuffIt(fs),
      F.Zpaq => ListZpaq(fs),
      F.Nsis => ListNsis(fs),
      F.InnoSetup => ListInnoSetup(fs),
      F.Dms => ListDms(fs),
      F.LzxAmiga => ListLzxAmiga(fs),
      F.CompactPro => ListCompactPro(fs),
      F.Spark => ListSpark(fs),
      F.Lbr => ListLbr(fs),
      F.Uharc => ListUharc(fs),
      F.Wad => ListWad(fs),
      F.Xar => ListXar(fs),
      F.AlZip => ListAlZip(fs),
      F.Rpm => [new(0, "payload.cpio", 0, 0, "cpio", false, false, null)],
      _ when pathHint != null && FormatDetector.IsStreamFormat(format) =>
        [new(0, StripCompressionExtension(pathHint), new FileInfo(pathHint).Length, new FileInfo(pathHint).Length, format.ToString(), false, false, null)],
      _ => throw new NotSupportedException($"Cannot list format: {format}"),
    };
  }

  internal static void Extract(string path, string outputDir, string? password, string[]? files) {
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
    switch (format) {
      case F.Zip: ExtractZip(fs, outputDir, password, files); break;
      case F.Rar: ExtractRar(fs, outputDir, password, files); break;
      case F.SevenZip: Extract7z(fs, outputDir, password, files); break;
      case F.Tar: ExtractTar(fs, outputDir, files); break;
      case F.TarGz: ExtractTarCompressed(fs, outputDir, F.Gzip, files); break;
      case F.TarBz2: ExtractTarCompressed(fs, outputDir, F.Bzip2, files); break;
      case F.TarXz: ExtractTarCompressed(fs, outputDir, F.Xz, files); break;
      case F.TarZst: ExtractTarCompressed(fs, outputDir, F.Zstd, files); break;
      case F.TarLz4: ExtractTarLz4(fs, outputDir, files); break;
      case F.TarLzip: ExtractTarLzip(fs, outputDir, files); break;
      case F.TarBr: ExtractTarBr(fs, outputDir, files); break;
      case F.Vpk: ExtractVpk(fs, outputDir, files); break;
      case F.Bsa: ExtractBsa(fs, outputDir, files); break;
      case F.Mpq: ExtractMpq(fs, outputDir, files); break;
      case F.Cab: ExtractCab(fs, outputDir, files); break;
      case F.Lzh: ExtractLzh(fs, outputDir, files); break;
      case F.Arj: ExtractArj(fs, outputDir, password, files); break;
      case F.Arc: ExtractArc(fs, outputDir, files); break;
      case F.Zoo: ExtractZoo(fs, outputDir, files); break;
      case F.Ace: ExtractAce(fs, outputDir, password, files); break;
      case F.Sqx: ExtractSqx(fs, outputDir, password, files); break;
      case F.Cpio: ExtractCpio(fs, outputDir, files); break;
      case F.Ar: ExtractAr(fs, outputDir, files); break;
      case F.Wim: ExtractWim(fs, outputDir, files); break;
      case F.Deb: ExtractDeb(fs, outputDir, files); break;
      case F.Shar: ExtractShar(fs, outputDir, files); break;
      case F.Pak: ExtractPak(fs, outputDir, files); break;
      case F.Ha: ExtractHa(fs, outputDir, files); break;
      case F.SquashFs: ExtractSquashFs(fs, outputDir, files); break;
      case F.CramFs: ExtractCramFs(fs, outputDir, files); break;
      case F.StuffIt: ExtractStuffIt(fs, outputDir, files); break;
      case F.Zpaq: ExtractZpaq(fs, outputDir, files); break;
      case F.Nsis: ExtractNsis(fs, outputDir, files); break;
      case F.InnoSetup: ExtractInnoSetup(fs, outputDir, files); break;
      case F.Dms: ExtractDms(fs, outputDir, files); break;
      case F.LzxAmiga: ExtractLzxAmiga(fs, outputDir, files); break;
      case F.CompactPro: ExtractCompactPro(fs, outputDir, files); break;
      case F.Spark: ExtractSpark(fs, outputDir, files); break;
      case F.Lbr: ExtractLbr(fs, outputDir, files); break;
      case F.Uharc: ExtractUharc(fs, outputDir, files); break;
      case F.Wad: ExtractWad(fs, outputDir, files); break;
      case F.Xar: ExtractXar(fs, outputDir, files); break;
      case F.AlZip: ExtractAlZip(fs, outputDir, files); break;
      case F.Rpm: ExtractRpm(fs, outputDir); break;
      default: throw new NotSupportedException($"Cannot extract format: {format}");
    }
  }

  internal static void Create(string outputPath, IReadOnlyList<ArchiveInput> inputs, CompressionOptions opts) {
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
    switch (format) {
      case F.Zip: CreateZip(outFs, inputs, password, method, incompressible, opts); break;
      case F.Tar: CreateTar(outFs, inputs); break;
      case F.TarGz: CreateTarGz(outFs, inputs, method); break;
      case F.TarBz2: CreateTarBz2(outFs, inputs); break;
      case F.TarXz: CreateTarXz(outFs, inputs); break;
      case F.TarZst: CreateTarZst(outFs, inputs); break;
      case F.TarLz4: CreateTarLz4(outFs, inputs); break;
      case F.TarLzip: CreateTarLzip(outFs, inputs); break;
      case F.TarBr: CreateTarBr(outFs, inputs); break;
      case F.SevenZip: Create7z(outFs, inputs, password, opts); break;
      case F.Lzh: CreateLzh(outFs, inputs, opts); break;
      case F.Arj: CreateArj(outFs, inputs, opts); break;
      case F.Arc: CreateArc(outFs, inputs, opts); break;
      case F.Zoo: CreateZoo(outFs, inputs, opts); break;
      case F.Ace: CreateAce(outFs, inputs, opts); break;
      case F.Sqx: CreateSqx(outFs, inputs, opts); break;
      case F.Cpio: CreateCpio(outFs, inputs); break;
      case F.Cab: CreateCab(outFs, inputs, opts); break;
      case F.Shar: CreateShar(outFs, inputs); break;
      case F.Pak: CreatePak(outFs, inputs); break;
      case F.Rar: CreateRar(outFs, inputs, opts); break;
      case F.Ha: CreateHa(outFs, inputs); break;
      case F.SquashFs: CreateSquashFs(outFs, inputs); break;
      case F.CramFs: CreateCramFs(outFs, inputs); break;
      case F.StuffIt: CreateStuffIt(outFs, inputs); break;
      case F.Zpaq: CreateZpaq(outFs, inputs); break;
      case F.Dms: CreateDms(outFs, inputs); break;
      case F.LzxAmiga: CreateLzxAmiga(outFs, inputs); break;
      case F.CompactPro: CreateCompactPro(outFs, inputs); break;
      case F.Spark: CreateSpark(outFs, inputs); break;
      case F.Lbr: CreateLbr(outFs, inputs); break;
      case F.Uharc: CreateUharc(outFs, inputs); break;
      case F.Wad: CreateWad(outFs, inputs); break;
      case F.Xar: CreateXar(outFs, inputs); break;
      case F.AlZip: CreateAlZip(outFs, inputs); break;
      case F.Vpk: CreateVpk(outFs, inputs); break;
      case F.Bsa: CreateBsa(outFs, inputs); break;
      default: throw new NotSupportedException($"Cannot create format: {format}");
    }
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
  internal static (string Strategy, int Tier) Convert(string inputPath, string outputPath,
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

  internal static bool Test(string path, string? password) {
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
  internal static (long OriginalSize, long OptimizedSize, int EntriesOptimized) Optimize(
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

  private static void CompressStreamPairOptimal(Stream input, Stream output, F format) {
    var mode = Compression.Core.Streams.CompressionStreamMode.Compress;
    switch (format) {
      case F.Gzip: {
        using var cs = new FileFormat.Gzip.GzipStream(output, mode,
          Compression.Core.Deflate.DeflateCompressionLevel.Maximum, leaveOpen: true);
        input.CopyTo(cs);
        break;
      }
      case F.Bzip2: { using var cs = new FileFormat.Bzip2.Bzip2Stream(output, mode, leaveOpen: true); input.CopyTo(cs); break; }
      case F.Xz: { using var cs = new FileFormat.Xz.XzStream(output, mode, leaveOpen: true); input.CopyTo(cs); break; }
      case F.Zstd: { using var cs = new FileFormat.Zstd.ZstdStream(output, mode, leaveOpen: true); input.CopyTo(cs); break; }
      case F.Compress: { using var cs = new FileFormat.Compress.CompressStream(output, mode, leaveOpen: true); input.CopyTo(cs); break; }
      case F.Lzma: FileFormat.Lzma.LzmaStream.Compress(input, output); break;
      case F.Lzip: FileFormat.Lzip.LzipStream.Compress(input, output); break;
      case F.Zlib: FileFormat.Zlib.ZlibStream.Compress(input, output, Compression.Core.Deflate.DeflateCompressionLevel.Maximum); break;
      default: CompressStreamPair(input, output, format); break;
    }
  }

  // ── ZIP ────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListZip(Stream s, string? pw) {
    var r = new FileFormat.Zip.ZipReader(s, password: pw);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.UncompressedSize, e.CompressedSize,
      e.CompressionMethod.ToString(), e.IsDirectory, e.IsEncrypted, e.LastModified)).ToList();
  }

  private static void ExtractZip(Stream s, string dir, string? pw, string[]? files) {
    var r = new FileFormat.Zip.ZipReader(s, password: pw);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(dir, e.FileName)); continue; }
      WriteFile(dir, e.FileName, r.ExtractEntry(e));
    }
  }

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

  // ── RAR ────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListRar(Stream s, string? pw) {
    var r = new FileFormat.Rar.RarReader(s, password: pw);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.Name, e.Size, e.CompressedSize,
      $"Method {e.CompressionMethod}", e.IsDirectory, e.IsEncrypted, e.ModifiedTime?.DateTime)).ToList();
  }

  private static void ExtractRar(Stream s, string dir, string? pw, string[]? files) {
    var r = new FileFormat.Rar.RarReader(s, password: pw);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(dir, e.Name)); continue; }
      WriteFile(dir, e.Name, r.Extract(i));
    }
  }

  private static void CreateRar(Stream s, IReadOnlyList<ArchiveInput> inputs, CompressionOptions opts) {
    var pw = opts.Password;
    var useRar4 = opts.Method.Name == "rar4";
    // RAR method levels: 0=Store,1=Fastest,2=Fast,3=Normal,4=Good,5=Best
    var rarLevel = opts.Level switch {
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

  // ── 7z ─────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> List7z(Stream s, string? pw) {
    var r = new FileFormat.SevenZip.SevenZipReader(s, password: pw);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.Name, e.Size, e.CompressedSize,
      string.IsNullOrEmpty(e.Method) ? "7z" : e.Method, e.IsDirectory, false, e.LastWriteTime)).ToList();
  }

  private static void Extract7z(Stream s, string dir, string? pw, string[]? files) {
    var r = new FileFormat.SevenZip.SevenZipReader(s, password: pw);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(dir, e.Name)); continue; }
      WriteFile(dir, e.Name, r.Extract(i));
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

  // ── TAR ────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListTar(Stream s) {
    var r = new FileFormat.Tar.TarReader(s);
    var entries = new List<ArchiveEntry>();
    var i = 0;
    while (r.GetNextEntry() is { } e) {
      entries.Add(new ArchiveEntry(i++, e.Name, e.Size, e.Size, "tar", e.IsDirectory, false, e.ModifiedTime.DateTime));
      r.Skip();
    }
    return entries;
  }

  private static void ExtractTar(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Tar.TarReader(s);
    while (r.GetNextEntry() is { } e) {
      if (files != null && !MatchesFilter(e.Name, files)) { r.Skip(); continue; }
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(dir, e.Name)); r.Skip(); continue; }
      using var es = r.GetEntryStream();
      var data = new byte[e.Size];
      es.ReadExactly(data);
      WriteFile(dir, e.Name, data);
    }
  }

  private static void CreateTar(Stream s, IReadOnlyList<ArchiveInput> inputs) {
    var w = new FileFormat.Tar.TarWriter(s);
    foreach (var i in inputs) {
      if (i.IsDirectory) {
        w.AddEntry(new FileFormat.Tar.TarEntry { Name = i.EntryName, Size = 0, TypeFlag = (byte)'5' }, []);
      }
      else {
        var data = File.ReadAllBytes(i.FullPath);
        w.AddEntry(new FileFormat.Tar.TarEntry { Name = i.EntryName, Size = data.Length }, data);
      }
    }
    w.Finish();
  }

  private static List<ArchiveEntry> ListTarCompressed(Stream s, F compression) {
    using var ds = WrapDecompressStream(s, compression);
    return ListTar(ds);
  }

  private static void ExtractTarCompressed(Stream s, string dir, F compression, string[]? files) {
    using var ds = WrapDecompressStream(s, compression);
    ExtractTar(ds, dir, files);
  }

  private static void CreateTarGz(Stream outFs, IReadOnlyList<ArchiveInput> inputs, MethodSpec method = default) {
    var level = method.ResolveDeflateLevel();
    using var cs = new FileFormat.Gzip.GzipStream(outFs, Core.Streams.CompressionStreamMode.Compress, level, leaveOpen: true);
    CreateTar(cs, inputs);
  }

  private static void CreateTarBz2(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var cs = new FileFormat.Bzip2.Bzip2Stream(outFs, Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    CreateTar(cs, inputs);
  }

  private static void CreateTarXz(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var cs = new FileFormat.Xz.XzStream(outFs, Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    CreateTar(cs, inputs);
  }

  private static void CreateTarZst(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var cs = new FileFormat.Zstd.ZstdStream(outFs, Core.Streams.CompressionStreamMode.Compress, leaveOpen: true);
    CreateTar(cs, inputs);
  }

  // ── CAB ────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListCab(Stream s) {
    var r = new FileFormat.Cab.CabReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.UncompressedSize, -1,
      "CAB", false, false, e.LastModified)).ToList();
  }

  private static void ExtractCab(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Cab.CabReader(s);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.ExtractEntry(e));
    }
  }

  private static void CreateCab(Stream outFs, IReadOnlyList<ArchiveInput> inputs, CompressionOptions opts) {
    var method = opts.Method.Name;
    var compType = method switch {
      "lzx" => FileFormat.Cab.CabCompressionType.Lzx,
      "quantum" => FileFormat.Cab.CabCompressionType.Quantum,
      "none" or "store" => FileFormat.Cab.CabCompressionType.None,
      _ => FileFormat.Cab.CabCompressionType.MsZip,
    };
    var lzxWindow = opts.Level.HasValue ? Math.Clamp(opts.Level.Value, 15, 21) : 15;
    var quantumLevel = opts.Level.HasValue ? Math.Clamp(opts.Level.Value, 1, 7) : 4;
    var w = new FileFormat.Cab.CabWriter(compType, lzxWindowBits: lzxWindow, quantumWindowLevel: quantumLevel);
    foreach (var (name, data) in ArchiveInput.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(outFs);
  }

  // ── LZH ────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListLzh(Stream s) {
    var r = new FileFormat.Lzh.LhaReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.Method, false, false, e.LastModified)).ToList();
  }

  private static void ExtractLzh(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Lzh.LhaReader(s);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.ExtractEntry(e));
    }
  }

  private static void CreateLzh(Stream outFs, IReadOnlyList<ArchiveInput> inputs, CompressionOptions opts) {
    var lzhMethod = opts.Method.Name switch {
      "lh0" or "store" => FileFormat.Lzh.LhaConstants.MethodLh0,
      "lh1" => FileFormat.Lzh.LhaConstants.MethodLh1,
      "lh2" => FileFormat.Lzh.LhaConstants.MethodLh2,
      "lh3" => FileFormat.Lzh.LhaConstants.MethodLh3,
      "lh4" => FileFormat.Lzh.LhaConstants.MethodLh4,
      "lh6" => FileFormat.Lzh.LhaConstants.MethodLh6,
      "lh7" => FileFormat.Lzh.LhaConstants.MethodLh7,
      "lzs" => FileFormat.Lzh.LhaConstants.MethodLzs,
      "lz5" => FileFormat.Lzh.LhaConstants.MethodLz5,
      "pm0" => FileFormat.Lzh.LhaConstants.MethodPm0,
      "pm1" => FileFormat.Lzh.LhaConstants.MethodPm1,
      "pm2" => FileFormat.Lzh.LhaConstants.MethodPm2,
      _ => FileFormat.Lzh.LhaConstants.MethodLh5,
    };
    var w = new FileFormat.Lzh.LhaWriter(lzhMethod);
    foreach (var (name, data) in ArchiveInput.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(outFs);
  }

  // ── ARJ ────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListArj(Stream s, string? pw) {
    var r = new FileFormat.Arj.ArjReader(s, pw);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.OriginalSize, e.CompressedSize,
      $"Method {e.Method}", e.IsDirectory, false, e.LastModified)).ToList();
  }

  private static void ExtractArj(Stream s, string dir, string? pw, string[]? files) {
    var r = new FileFormat.Arj.ArjReader(s, pw);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(dir, e.FileName)); continue; }
      WriteFile(dir, e.FileName, r.ExtractEntry(e));
    }
  }

  private static void CreateArj(Stream outFs, IReadOnlyList<ArchiveInput> inputs, CompressionOptions opts) {
    // ARJ methods: 0=Store, 1=Compressed (best), 2=Compressed2, 3=Compressed3 (fastest), 4=StoreFast
    byte arjMethod = opts.Method.Name switch {
      "store" => 0,
      "1" or "compressed" => 1,
      "2" => 2,
      "3" or "fastest" => 3,
      _ => opts.Level switch {
        0 => (byte)0,
        >= 7 => (byte)1,
        >= 4 => (byte)2,
        _ => (byte)1,
      },
    };
    var w = new FileFormat.Arj.ArjWriter(arjMethod, password: opts.Password);
    foreach (var i in inputs) {
      if (i.IsDirectory) w.AddDirectory(i.EntryName);
      else w.AddFile(i.EntryName, File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(outFs);
  }

  // ── ARC ────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListArc(Stream s) {
    var r = new FileFormat.Arc.ArcReader(s);
    var entries = new List<ArchiveEntry>();
    var i = 0;
    while (r.GetNextEntry() is { } e)
      entries.Add(new ArchiveEntry(i++, e.FileName, e.OriginalSize, e.CompressedSize,
        $"Method {e.Method}", false, false, e.LastModified.DateTime));
    return entries;
  }

  private static void ExtractArc(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Arc.ArcReader(s);
    while (r.GetNextEntry() is { } e) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.ReadEntryData());
    }
  }

  private static void CreateArc(Stream outFs, IReadOnlyList<ArchiveInput> inputs, CompressionOptions opts) {
    var arcMethod = opts.Method.Name switch {
      "store" => FileFormat.Arc.ArcCompressionMethod.Stored,
      "pack" or "packed" => FileFormat.Arc.ArcCompressionMethod.Packed,
      "squeeze" or "squeezed" => FileFormat.Arc.ArcCompressionMethod.Squeezed,
      "crunch5" => FileFormat.Arc.ArcCompressionMethod.Crunched5,
      "crunch6" => FileFormat.Arc.ArcCompressionMethod.Crunched6,
      "crunch7" => FileFormat.Arc.ArcCompressionMethod.Crunched7,
      "crunch" or "crunch8" => FileFormat.Arc.ArcCompressionMethod.Crunched,
      "squash" or "squashed" => FileFormat.Arc.ArcCompressionMethod.Squashed,
      _ => FileFormat.Arc.ArcCompressionMethod.Crunched,
    };
    var w = new FileFormat.Arc.ArcWriter(outFs, arcMethod);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddEntry(name, data);
    w.Finish();
  }

  // ── ZOO ────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListZoo(Stream s) {
    var r = new FileFormat.Zoo.ZooReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.EffectiveName, e.OriginalSize, e.CompressedSize,
      e.CompressionMethod.ToString(), false, false, e.LastModified)).ToList();
  }

  private static void ExtractZoo(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Zoo.ZooReader(s);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.EffectiveName, files)) continue;
      WriteFile(dir, e.EffectiveName, r.ExtractEntry(e));
    }
  }

  private static void CreateZoo(Stream outFs, IReadOnlyList<ArchiveInput> inputs, CompressionOptions opts) {
    var zooMethod = opts.Method.Name switch {
      "store" => FileFormat.Zoo.ZooCompressionMethod.Store,
      _ => FileFormat.Zoo.ZooCompressionMethod.Lzw,
    };
    var w = new FileFormat.Zoo.ZooWriter(outFs, defaultMethod: zooMethod);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddEntry(name, data);
    w.Finish();
  }

  // ── ACE ────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListAce(Stream s, string? pw) {
    var r = new FileFormat.Ace.AceReader(s, password: pw);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.OriginalSize, e.CompressedSize,
      $"ACE {e.CompressionType}", false, e.IsEncrypted, e.LastModified)).ToList();
  }

  private static void ExtractAce(Stream s, string dir, string? pw, string[]? files) {
    var r = new FileFormat.Ace.AceReader(s, password: pw);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.ExtractEntry(e));
    }
  }

  private static void CreateAce(Stream outFs, IReadOnlyList<ArchiveInput> inputs, CompressionOptions opts) {
    var dictBits = opts.DictSize > 0
      ? Math.Clamp((int)Math.Log2(opts.DictSize), 10, 22) : 15;
    var solid = opts.SolidSize == 0;
    var compType = opts.Method.Name switch {
      "store" => 0,
      "ace20" or "ace2" => 2,
      _ => 1,
    };
    var w = new FileFormat.Ace.AceWriter(dictionaryBits: dictBits, password: opts.Password,
      solid: solid, compressionType: compType);
    foreach (var (name, data) in ArchiveInput.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(outFs);
  }

  // ── SQX ────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListSqx(Stream s, string? pw) {
    var r = new FileFormat.Sqx.SqxReader(s, password: pw);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.OriginalSize, e.CompressedSize,
      $"SQX {e.Method}", false, e.IsEncrypted, e.LastModified)).ToList();
  }

  private static void ExtractSqx(Stream s, string dir, string? pw, string[]? files) {
    var r = new FileFormat.Sqx.SqxReader(s, password: pw);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.ExtractEntry(e));
    }
  }

  private static void CreateSqx(Stream outFs, IReadOnlyList<ArchiveInput> inputs, CompressionOptions opts) {
    var solid = opts.SolidSize == 0;
    var dictSize = opts.DictSize > 0 ? (int)Math.Min(opts.DictSize, 4 * 1024 * 1024) : 256 * 1024;
    var w = new FileFormat.Sqx.SqxWriter(password: opts.Password, solid: solid,
      dictSize: dictSize);
    foreach (var (name, data) in ArchiveInput.FilesOnly(inputs))
      w.AddFile(name, data);
    w.WriteTo(outFs);
  }

  // ── CPIO ───────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListCpio(Stream s) {
    var r = new FileFormat.Cpio.CpioReader(s);
    var all = r.ReadAll();
    return all.Select((x, i) => new ArchiveEntry(i, x.Entry.Name, x.Entry.FileSize, x.Entry.FileSize,
      "cpio", x.Entry.IsDirectory, false, DateTimeOffset.FromUnixTimeSeconds(x.Entry.ModificationTime).DateTime)).ToList();
  }

  private static void ExtractCpio(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Cpio.CpioReader(s);
    foreach (var (entry, data) in r.ReadAll()) {
      if (files != null && !MatchesFilter(entry.Name, files)) continue;
      if (entry.IsDirectory) { Directory.CreateDirectory(Path.Combine(dir, entry.Name)); continue; }
      WriteFile(dir, entry.Name, data);
    }
  }

  private static void CreateCpio(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    var w = new FileFormat.Cpio.CpioWriter(outFs);
    foreach (var i in inputs) {
      if (i.IsDirectory) w.AddDirectory(i.EntryName);
      else w.AddFile(i.EntryName, File.ReadAllBytes(i.FullPath));
    }
    w.Finish();
  }

  // ── AR ─────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListAr(Stream s) {
    var r = new FileFormat.Ar.ArReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.Name, e.Data.Length, e.Data.Length,
      "ar", false, false, e.ModifiedTime.DateTime)).ToList();
  }

  private static void ExtractAr(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Ar.ArReader(s);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(dir, e.Name, e.Data);
    }
  }

  // ── WIM ────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListWim(Stream s) {
    var r = new FileFormat.Wim.WimReader(s);
    return r.Resources.Select((e, i) => new ArchiveEntry(i, $"resource_{i}", e.OriginalSize, e.CompressedSize,
      e.IsCompressed ? "LZX" : "Store", false, false, null)).ToList();
  }

  private static void ExtractWim(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Wim.WimReader(s);
    for (var i = 0; i < r.Resources.Count; ++i) {
      var name = $"resource_{i}";
      if (files != null && !MatchesFilter(name, files)) continue;
      WriteFile(dir, name, r.ReadResource(i));
    }
  }

  // ── DEB ────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListDeb(Stream s) {
    var r = new FileFormat.Deb.DebReader(s);
    var data = r.ReadDataEntries();
    return data.Select((e, i) => new ArchiveEntry(i, e.Path, e.Data.Length, e.Data.Length,
      "deb", e.IsDirectory, false, null)).ToList();
  }

  private static void ExtractDeb(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Deb.DebReader(s);
    foreach (var e in r.ReadDataEntries()) {
      if (files != null && !MatchesFilter(e.Path, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(dir, e.Path)); continue; }
      WriteFile(dir, e.Path, e.Data);
    }
  }

  // ── SHAR ───────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListShar(Stream s) {
    var r = new FileFormat.Shar.SharReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.Data.Length, e.Data.Length,
      "shar", false, false, null)).ToList();
  }

  private static void ExtractShar(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Shar.SharReader(s);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, e.Data);
    }
  }

  private static void CreateShar(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    var w = new FileFormat.Shar.SharWriter();
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddFile(name, data);
    w.WriteTo(outFs);
  }

  // ── PAK ─────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListPak(Stream s) {
    var r = new FileFormat.Pak.PakReader(s);
    var entries = new List<ArchiveEntry>();
    var i = 0;
    while (r.GetNextEntry() is { } e)
      entries.Add(new ArchiveEntry(i++, e.FileName, e.OriginalSize, e.CompressedSize,
        $"Method {e.Method}", false, false, e.LastModified.DateTime));
    return entries;
  }

  private static void ExtractPak(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Pak.PakReader(s);
    while (r.GetNextEntry() is { } e) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.ReadEntryData());
    }
  }

  private static void CreatePak(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    var w = new FileFormat.Pak.PakWriter(outFs);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddEntry(name, data);
    w.Finish();
  }

  // ── RPM ────────────────────────────────────────────────────────────

  private static void ExtractRpm(Stream s, string dir) {
    var r = new FileFormat.Rpm.RpmReader(s);
    using var payload = r.GetPayloadStream();
    ExtractCpio(payload, dir, null);
  }

  // ── Ha ──────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListHa(Stream s) {
    var r = new FileFormat.Ha.HaReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.OriginalSize, e.CompressedSize,
      $"Method {e.Method}", e.IsDirectory, false, e.LastModified)).ToList();
  }

  private static void ExtractHa(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Ha.HaReader(s);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.Extract(e));
    }
  }

  private static void CreateHa(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.Ha.HaWriter(outFs, leaveOpen: true);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddFile(name, data, DateTime.Now);
  }

  // ── SquashFS ───────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListSquashFs(Stream s) {
    var r = new FileFormat.SquashFs.SquashFsReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FullPath, e.Size, -1,
      "squashfs", e.IsDirectory, false, e.ModifiedTime)).ToList();
  }

  private static void ExtractSquashFs(Stream s, string dir, string[]? files) {
    var r = new FileFormat.SquashFs.SquashFsReader(s);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(dir, e.FullPath, r.Extract(e));
    }
  }

  private static void CreateSquashFs(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.SquashFs.SquashFsWriter(outFs, leaveOpen: true);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        w.AddDirectory(input.EntryName.TrimEnd('/'));
      } else {
        var data = File.ReadAllBytes(input.FullPath);
        w.AddFile(input.EntryName, data);
      }
    }
  }

  // ── CramFS ─────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListCramFs(Stream s) {
    var r = new FileFormat.CramFs.CramFsReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FullPath, e.Size, -1,
      "cramfs", e.IsDirectory, false, null)).ToList();
  }

  private static void ExtractCramFs(Stream s, string dir, string[]? files) {
    var r = new FileFormat.CramFs.CramFsReader(s);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(dir, e.FullPath, r.Extract(e));
    }
  }

  private static void CreateCramFs(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.CramFs.CramFsWriter(outFs, leaveOpen: true);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        w.AddDirectory(input.EntryName.TrimEnd('/'));
      } else {
        var data = File.ReadAllBytes(input.FullPath);
        w.AddFile(input.EntryName, data);
      }
    }
  }

  // ── StuffIt ────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListStuffIt(Stream s) {
    var r = new FileFormat.StuffIt.StuffItReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.DataForkSize,
      e.CompressedDataSize, $"Method {e.DataMethod}", false, false, e.LastModified)).ToList();
  }

  private static void ExtractStuffIt(Stream s, string dir, string[]? files) {
    var r = new FileFormat.StuffIt.StuffItReader(s);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.Extract(e));
    }
  }

  private static void CreateStuffIt(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.StuffIt.StuffItWriter(outFs, leaveOpen: true);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddFile(name, data);
  }

  // ── ZPAQ ───────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListZpaq(Stream s) {
    var r = new FileFormat.Zpaq.ZpaqReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.Size, e.CompressedSize,
      "zpaq", e.IsDirectory, false, e.LastModified)).ToList();
  }

  private static void ExtractZpaq(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Zpaq.ZpaqReader(s);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      using var es = r.Extract(e);
      using var ms = new MemoryStream();
      es.CopyTo(ms);
      WriteFile(dir, e.FileName, ms.ToArray());
    }
  }

  private static void CreateZpaq(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.Zpaq.ZpaqWriter(outFs, leaveOpen: true);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        w.AddDirectory(input.EntryName);
      } else {
        var data = File.ReadAllBytes(input.FullPath);
        w.AddFile(input.EntryName, data);
      }
    }
  }

  // ── NSIS ───────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListNsis(Stream s) {
    var r = new FileFormat.Nsis.NsisReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.Size, e.CompressedSize,
      "nsis", e.IsDirectory, false, null)).ToList();
  }

  private static void ExtractNsis(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Nsis.NsisReader(s);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.Extract(e));
    }
  }

  // ── InnoSetup ──────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListInnoSetup(Stream s) {
    var r = new FileFormat.InnoSetup.InnoSetupReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.Size, e.CompressedSize,
      "innosetup", e.IsDirectory, false, null)).ToList();
  }

  private static void ExtractInnoSetup(Stream s, string dir, string[]? files) {
    var r = new FileFormat.InnoSetup.InnoSetupReader(s);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.Extract(e));
    }
  }

  // ── Stream compression helpers ─────────────────────────────────────

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

  internal static byte[] DecompressFile(string path, F format) {
    using var fs = File.OpenRead(path);
    using var ms = new MemoryStream();
    DecompressStreamPair(fs, ms, format);
    return ms.ToArray();
  }

  private static void DecompressToNull(Stream input, F format) {
    using var ms = new MemoryStream();
    DecompressStreamPair(input, ms, format);
  }

  private static void DecompressStreamPair(Stream input, Stream output, F format) {
    switch (format) {
      case F.Gzip: { using var ds = new FileFormat.Gzip.GzipStream(input, Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true); ds.CopyTo(output); break; }
      case F.Bzip2: { using var ds = new FileFormat.Bzip2.Bzip2Stream(input, Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true); ds.CopyTo(output); break; }
      case F.Xz: { using var ds = new FileFormat.Xz.XzStream(input, Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true); ds.CopyTo(output); break; }
      case F.Zstd: { using var ds = new FileFormat.Zstd.ZstdStream(input, Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true); ds.CopyTo(output); break; }
      case F.Compress: { using var ds = new FileFormat.Compress.CompressStream(input, Core.Streams.CompressionStreamMode.Decompress, leaveOpen: true); ds.CopyTo(output); break; }
      case F.Lzma: FileFormat.Lzma.LzmaStream.Decompress(input, output); break;
      case F.Lzip: FileFormat.Lzip.LzipStream.Decompress(input, output); break;
      case F.Zlib: FileFormat.Zlib.ZlibStream.Decompress(input, output); break;
      case F.Szdd: FileFormat.Szdd.SzddStream.Decompress(input, output); break;
      case F.Kwaj: FileFormat.Kwaj.KwajStream.Decompress(input, output); break;
      case F.Lz4: { var r = new FileFormat.Lz4.Lz4FrameReader(input); output.Write(r.Read()); break; }
      case F.Brotli: { var d = FileFormat.Brotli.BrotliStream.Decompress(input); output.Write(d); break; }
      case F.Snappy: { var r = new FileFormat.Snappy.SnappyFrameReader(input); output.Write(r.Read()); break; }
      case F.Lzop: { var r = new FileFormat.Lzop.LzopReader(input); output.Write(r.Decompress()); break; }
      case F.PowerPacker: FileFormat.PowerPacker.PowerPackerStream.Decompress(input, output); break;
      case F.Squeeze: FileFormat.Squeeze.SqueezeStream.Decompress(input, output); break;
      case F.IcePacker: FileFormat.IcePacker.IcePackerStream.Decompress(input, output); break;
      case F.Rzip: FileFormat.Rzip.RzipStream.Decompress(input, output); break;
      case F.MacBinary: { var data = FileFormat.MacBinary.MacBinaryReader.ReadDataFork(input); output.Write(data); break; }
      case F.BinHex: { var result = FileFormat.BinHex.BinHexReader.Decode(input); output.Write(result.DataFork); break; }
      case F.PackBits: FileFormat.PackBits.PackBitsStream.Decompress(input, output); break;
      case F.Yaz0: FileFormat.Yaz0.Yaz0Stream.Decompress(input, output); break;
      case F.BriefLz: FileFormat.BriefLz.BriefLzStream.Decompress(input, output); break;
      case F.Rnc: FileFormat.Rnc.RncStream.Decompress(input, output); break;
      case F.RefPack: FileFormat.RefPack.RefPackStream.Decompress(input, output); break;
      case F.ApLib: FileFormat.ApLib.ApLibStream.Decompress(input, output); break;
      case F.Lzfse: FileFormat.Lzfse.LzfseStream.Decompress(input, output); break;
      case F.Freeze: FileFormat.Freeze.FreezeStream.Decompress(input, output); break;
      case F.UuEncoding: { var (_, _, data) = FileFormat.UuEncoding.UuEncoder.Decode(input); output.Write(data); break; }
      case F.YEnc: { var (_, _, _, data) = FileFormat.YEnc.YEncDecoder.Decode(input); output.Write(data); break; }
      case F.Density: FileFormat.Density.DensityStream.Decompress(input, output); break;
      default: throw new NotSupportedException($"No decompressor for: {format}");
    }
  }

  private static void CompressStreamPair(Stream input, Stream output, F format) {
    switch (format) {
      case F.Gzip: { using var cs = new FileFormat.Gzip.GzipStream(output, Core.Streams.CompressionStreamMode.Compress, leaveOpen: true); input.CopyTo(cs); break; }
      case F.Bzip2: { using var cs = new FileFormat.Bzip2.Bzip2Stream(output, Core.Streams.CompressionStreamMode.Compress, leaveOpen: true); input.CopyTo(cs); break; }
      case F.Xz: { using var cs = new FileFormat.Xz.XzStream(output, Core.Streams.CompressionStreamMode.Compress, leaveOpen: true); input.CopyTo(cs); break; }
      case F.Zstd: { using var cs = new FileFormat.Zstd.ZstdStream(output, Core.Streams.CompressionStreamMode.Compress, leaveOpen: true); input.CopyTo(cs); break; }
      case F.Compress: { using var cs = new FileFormat.Compress.CompressStream(output, Core.Streams.CompressionStreamMode.Compress, leaveOpen: true); input.CopyTo(cs); break; }
      case F.Lzma: FileFormat.Lzma.LzmaStream.Compress(input, output); break;
      case F.Lzip: FileFormat.Lzip.LzipStream.Compress(input, output); break;
      case F.Zlib: FileFormat.Zlib.ZlibStream.Compress(input, output); break;
      case F.Szdd: FileFormat.Szdd.SzddStream.Compress(input, output); break;
      case F.Kwaj: FileFormat.Kwaj.KwajStream.Compress(input, output); break;
      case F.Lz4: { using var ms = new MemoryStream(); input.CopyTo(ms); var w = new FileFormat.Lz4.Lz4FrameWriter(output); w.Write(ms.ToArray()); break; }
      case F.Brotli: { var c = FileFormat.Brotli.BrotliStream.Compress(input); output.Write(c); break; }
      case F.Snappy: { using var ms = new MemoryStream(); input.CopyTo(ms); var w = new FileFormat.Snappy.SnappyFrameWriter(output); w.Write(ms.ToArray()); break; }
      case F.Lzop: { using var ms = new MemoryStream(); input.CopyTo(ms); output.Write(FileFormat.Lzop.LzopWriter.Compress(ms.ToArray())); break; }
      case F.PowerPacker: FileFormat.PowerPacker.PowerPackerStream.Compress(input, output); break;
      case F.Squeeze: FileFormat.Squeeze.SqueezeStream.Compress(input, output); break;
      case F.IcePacker: FileFormat.IcePacker.IcePackerStream.Compress(input, output); break;
      case F.Rzip: FileFormat.Rzip.RzipStream.Compress(input, output); break;
      case F.MacBinary: { using var ms = new MemoryStream(); input.CopyTo(ms); FileFormat.MacBinary.MacBinaryWriter.Write(output, "data", ms.ToArray()); break; }
      case F.BinHex: { using var ms = new MemoryStream(); input.CopyTo(ms); FileFormat.BinHex.BinHexWriter.Write(output, "data", ms.ToArray()); break; }
      case F.PackBits: FileFormat.PackBits.PackBitsStream.Compress(input, output); break;
      case F.Yaz0: FileFormat.Yaz0.Yaz0Stream.Compress(input, output); break;
      case F.BriefLz: FileFormat.BriefLz.BriefLzStream.Compress(input, output); break;
      case F.Rnc: FileFormat.Rnc.RncStream.Compress(input, output); break;
      case F.RefPack: FileFormat.RefPack.RefPackStream.Compress(input, output); break;
      case F.ApLib: FileFormat.ApLib.ApLibStream.Compress(input, output); break;
      case F.Lzfse: FileFormat.Lzfse.LzfseStream.Compress(input, output); break;
      case F.Freeze: FileFormat.Freeze.FreezeStream.Compress(input, output); break;
      case F.UuEncoding: { using var ms = new MemoryStream(); input.CopyTo(ms); ms.Position = 0; FileFormat.UuEncoding.UuEncoder.Encode(ms, output, "data"); break; }
      case F.YEnc: { using var ms = new MemoryStream(); input.CopyTo(ms); FileFormat.YEnc.YEncEncoder.Encode(output, "data", ms.ToArray()); break; }
      case F.Density: FileFormat.Density.DensityStream.Compress(input, output); break;
      default: throw new NotSupportedException($"No compressor for: {format}");
    }
  }

  private static Stream WrapDecompressStream(Stream s, F format) {
    var mode = Core.Streams.CompressionStreamMode.Decompress;
    return format switch {
      F.Gzip => new FileFormat.Gzip.GzipStream(s, mode, leaveOpen: true),
      F.Bzip2 => new FileFormat.Bzip2.Bzip2Stream(s, mode, leaveOpen: true),
      F.Xz => new FileFormat.Xz.XzStream(s, mode, leaveOpen: true),
      F.Zstd => new FileFormat.Zstd.ZstdStream(s, mode, leaveOpen: true),
      F.Compress => new FileFormat.Compress.CompressStream(s, mode, leaveOpen: true),
      _ => throw new NotSupportedException($"No stream decompressor for: {format}"),
    };
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
    var ext = Path.GetExtension(name).ToLowerInvariant();
    return ext is ".gz" or ".bz2" or ".xz" or ".zst" or ".lz4" or ".br" or ".sz" or ".lzo" or ".z" or ".lzma" or ".lz" or ".zlib"
        or ".pp" or ".sqz" or ".ice" or ".rz" or ".hqx" or ".bin"
        or ".packbits" or ".yaz0" or ".szs" or ".blz" or ".rnc" or ".qfs" or ".refpack" or ".aplib" or ".lzfse" or ".freeze"
        or ".uue" or ".uu" or ".yenc" or ".density"
      ? Path.GetFileNameWithoutExtension(name)
      : name;
  }

  // ── DMS (Disk Masher System) ──────────────────────────────────────

  private static List<ArchiveEntry> ListDms(Stream s) {
    var r = new FileFormat.Dms.DmsReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, $"track_{e.TrackNumber:D3}.bin",
      e.UncompressedSize, e.CompressedSize, $"Mode {e.CompressionMode}", false, false, null)).ToList();
  }

  private static void ExtractDms(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Dms.DmsReader(s);
    // Extract as a single disk image
    var disk = r.ExtractDisk();
    WriteFile(dir, "disk.adf", disk);
  }

  private static void CreateDms(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    // DMS wraps a single disk image
    var fileInputs = inputs.Where(i => !i.IsDirectory).ToArray();
    if (fileInputs.Length != 1)
      throw new ArgumentException("DMS format requires exactly one input file (disk image).");
    var data = File.ReadAllBytes(fileInputs[0].FullPath);
    using var w = new FileFormat.Dms.DmsWriter(outFs, leaveOpen: true);
    w.WriteDisk(data, compressionMode: 0);
  }

  // ── LZX (Amiga) ──────────────────────────────────────────────────

  private static List<ArchiveEntry> ListLzxAmiga(Stream s) {
    var r = new FileFormat.Lzx.LzxAmigaReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.Method == 0 ? "Stored" : "LZX", false, false, e.LastModified)).ToList();
  }

  private static void ExtractLzxAmiga(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Lzx.LzxAmigaReader(s);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.Extract(e));
    }
  }

  private static void CreateLzxAmiga(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.Lzx.LzxAmigaWriter(outFs, leaveOpen: true);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddFile(name, data, DateTime.Now);
  }

  // ── Compact Pro ───────────────────────────────────────────────────

  private static List<ArchiveEntry> ListCompactPro(Stream s) {
    var r = new FileFormat.CompactPro.CompactProReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.DataForkSize,
      e.DataForkCompressedSize, $"Method {e.DataForkMethod}", e.IsDirectory, false, e.ModifiedDate)).ToList();
  }

  private static void ExtractCompactPro(Stream s, string dir, string[]? files) {
    var r = new FileFormat.CompactPro.CompactProReader(s);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.Extract(e));
    }
  }

  private static void CreateCompactPro(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.CompactPro.CompactProWriter(outFs, leaveOpen: true);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddFile(name, data);
  }

  // ── Spark ─────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListSpark(Stream s) {
    var r = new FileFormat.Spark.SparkReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.OriginalSize,
      e.CompressedSize, $"Method {e.Method:X2}", e.IsDirectory, false, e.LastModified)).ToList();
  }

  private static void ExtractSpark(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Spark.SparkReader(s);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.Extract(e));
    }
  }

  private static void CreateSpark(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.Spark.SparkWriter(outFs, leaveOpen: true);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddFile(name, data, DateTime.Now);
  }

  // ── LBR ───────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListLbr(Stream s) {
    var r = new FileFormat.Lbr.LbrReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName,
      (long)e.SectorCount * 128, (long)e.SectorCount * 128, "Stored", false, false, null)).ToList();
  }

  private static void ExtractLbr(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Lbr.LbrReader(s);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.Extract(e));
    }
  }

  private static void CreateLbr(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.Lbr.LbrWriter(outFs, leaveOpen: true);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddFile(name, data);
  }

  // ── UHARC ─────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListUharc(Stream s) {
    var r = new FileFormat.Uharc.UharcReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.OriginalSize,
      e.CompressedSize, e.Method == 255 ? "Store" : "LZP", e.IsDirectory, false, e.LastModified)).ToList();
  }

  private static void ExtractUharc(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Uharc.UharcReader(s);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.Extract(e));
    }
  }

  private static void CreateUharc(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.Uharc.UharcWriter(outFs, leaveOpen: true);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddFile(name, data, DateTime.Now);
  }

  // ── WAD ───────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListWad(Stream s) {
    var r = new FileFormat.Wad.WadReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.Name, e.Size, e.Size,
      "Stored", e.IsMarker, false, null)).ToList();
  }

  private static void ExtractWad(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Wad.WadReader(s);
    foreach (var e in r.Entries) {
      if (e.IsMarker) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(dir, e.Name, r.Extract(e));
    }
  }

  private static void CreateWad(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.Wad.WadWriter(outFs, leaveOpen: true);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddLump(name, data);
  }

  // ── XAR ────────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListXar(Stream s) {
    var r = new FileFormat.Xar.XarReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.Method, e.IsDirectory, false, e.LastModified)).ToList();
  }

  private static void ExtractXar(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Xar.XarReader(s);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.Extract(e));
    }
  }

  private static void CreateXar(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.Xar.XarWriter(outFs);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddFile(name, data);
  }

  // ── Compound Tar: LZ4 ──────────────────────────────────────────────

  private static List<ArchiveEntry> ListTarLz4(Stream s) {
    var r = new FileFormat.Lz4.Lz4FrameReader(s);
    using var decompressed = new MemoryStream(r.Read());
    return ListTar(decompressed);
  }

  private static void ExtractTarLz4(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Lz4.Lz4FrameReader(s);
    using var decompressed = new MemoryStream(r.Read());
    ExtractTar(decompressed, dir, files);
  }

  private static void CreateTarLz4(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var tarMs = new MemoryStream();
    CreateTar(tarMs, inputs);
    tarMs.Position = 0;
    var w = new FileFormat.Lz4.Lz4FrameWriter(outFs);
    w.Write(tarMs.ToArray());
  }

  // ── Compound Tar: Lzip ────────────────────────────────────────────

  private static List<ArchiveEntry> ListTarLzip(Stream s) {
    using var decompressed = new MemoryStream();
    FileFormat.Lzip.LzipStream.Decompress(s, decompressed);
    decompressed.Position = 0;
    return ListTar(decompressed);
  }

  private static void ExtractTarLzip(Stream s, string dir, string[]? files) {
    using var decompressed = new MemoryStream();
    FileFormat.Lzip.LzipStream.Decompress(s, decompressed);
    decompressed.Position = 0;
    ExtractTar(decompressed, dir, files);
  }

  private static void CreateTarLzip(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var tarMs = new MemoryStream();
    CreateTar(tarMs, inputs);
    tarMs.Position = 0;
    FileFormat.Lzip.LzipStream.Compress(tarMs, outFs);
  }

  // ── Compound Tar: Brotli ──────────────────────────────────────────

  private static List<ArchiveEntry> ListTarBr(Stream s) {
    var decompressed = FileFormat.Brotli.BrotliStream.Decompress(s);
    using var ms = new MemoryStream(decompressed);
    return ListTar(ms);
  }

  private static void ExtractTarBr(Stream s, string dir, string[]? files) {
    var decompressed = FileFormat.Brotli.BrotliStream.Decompress(s);
    using var ms = new MemoryStream(decompressed);
    ExtractTar(ms, dir, files);
  }

  private static void CreateTarBr(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var tarMs = new MemoryStream();
    CreateTar(tarMs, inputs);
    var compressed = FileFormat.Brotli.BrotliStream.Compress(tarMs.ToArray());
    outFs.Write(compressed);
  }

  // ── VPK (Valve Pak) ───────────────────────────────────────────────

  private static List<ArchiveEntry> ListVpk(Stream s) {
    var r = new FileFormat.Vpk.VpkReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FullPath,
      e.PreloadBytes.Length + e.Length, e.PreloadBytes.Length + e.Length,
      "Stored", false, false, null)).ToList();
  }

  private static void ExtractVpk(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Vpk.VpkReader(s);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(dir, e.FullPath, r.Extract(e));
    }
  }

  private static void CreateVpk(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.Vpk.VpkWriter(outFs, leaveOpen: true);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  // ── BSA (Bethesda) ────────────────────────────────────────────────

  private static List<ArchiveEntry> ListBsa(Stream s) {
    var r = new FileFormat.Bsa.BsaReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FullPath, e.OriginalSize,
      e.CompressedSize < 0 ? -1 : e.CompressedSize,
      e.IsCompressed ? "zlib" : "Stored", false, false, null)).ToList();
  }

  private static void ExtractBsa(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Bsa.BsaReader(s);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(dir, e.FullPath, r.Extract(e));
    }
  }

  private static void CreateBsa(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.Bsa.BsaWriter(outFs, leaveOpen: true);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddFile(name, data);
    w.Finish();
  }

  // ── MPQ (Blizzard) ────────────────────────────────────────────────

  private static List<ArchiveEntry> ListMpq(Stream s) {
    var r = new FileFormat.Mpq.MpqReader(s);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.IsCompressed ? "Compressed" : "Stored", false, e.IsEncrypted, null)).ToList();
  }

  private static void ExtractMpq(Stream s, string dir, string[]? files) {
    var r = new FileFormat.Mpq.MpqReader(s);
    foreach (var e in r.Entries) {
      if (!e.Exists) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      try { WriteFile(dir, e.FileName, r.Extract(e)); } catch { /* skip entries that can't be decompressed */ }
    }
  }

  // ── ALZip ──────────────────────────────────────────────────────────

  private static List<ArchiveEntry> ListAlZip(Stream s) {
    using var r = new FileFormat.AlZip.AlZipReader(s, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntry(i, e.FileName, e.OriginalSize, e.CompressedSize,
      e.MethodName, e.IsDirectory, false, e.LastModified)).ToList();
  }

  private static void ExtractAlZip(Stream s, string dir, string[]? files) {
    using var r = new FileFormat.AlZip.AlZipReader(s, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(dir, e.FileName, r.Extract(e));
    }
  }

  private static void CreateAlZip(Stream outFs, IReadOnlyList<ArchiveInput> inputs) {
    using var w = new FileFormat.AlZip.AlZipWriter(outFs);
    foreach (var (name, data) in ArchiveInput.FlatFiles(inputs))
      w.AddFile(name, data);
  }

  /// <summary>
  /// Extracts a single entry from an archive and returns its contents as a byte array.
  /// </summary>
  internal static byte[] ExtractEntry(string archivePath, string entryPath, string? password) {
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
