using System.CommandLine;
using System.Diagnostics;
using Compression.Lib;
using Compression.Registry;
using F = Compression.Lib.FormatDetector.Format;

var archiveArg = new Argument<FileInfo>("archive") { Description = "Path to the archive file" };
var outputOpt = new Option<DirectoryInfo?>("--output", "-o") { Description = "Output directory" };
var passwordOpt = new Option<string?>("--password", "-p") { Description = "Password for encrypted archives" };
var filesArg = new Argument<string[]>("files") { Description = "Specific files to extract", Arity = ArgumentArity.ZeroOrMore };

// ── list ─────────────────────────────────────────────────────────────

var listCmd = new Command("list", """
  List contents of an archive. Shows name, sizes, ratio, method, and date.
  Example: cwb list archive.7z
  """) { archiveArg, passwordOpt };
listCmd.Aliases.Add("l");
listCmd.SetAction((ParseResult ctx) => {
  var archive = ctx.GetValue(archiveArg)!;
  var password = ctx.GetValue(passwordOpt);

  if (!archive.Exists) { Console.Error.WriteLine($"File not found: {archive.FullName}"); return 1; }

  var format = FormatDetector.Detect(archive.FullName);
  Console.WriteLine($"Archive: {archive.Name}  Format: {format}");
  Console.WriteLine();

  var entries = ArchiveOperations.List(archive.FullName, password);
  if (entries.Count == 0) { Console.WriteLine("(empty archive)"); return 0; }

  // Column headers
  Console.WriteLine($"{"Name",-40} {"Original",12} {"Compressed",12} {"Ratio",7} {"Method",-10} {"Modified",-20}");
  Console.WriteLine(new string('-', 105));

  long totalOrig = 0, totalComp = 0;
  foreach (var e in entries) {
    var ratio = e.CompressedSize >= 0 ? $"{e.Ratio:F1}%" : "—";
    var comp = e.CompressedSize >= 0 ? FormatSize(e.CompressedSize) : "—";
    var mod = e.LastModified?.ToString("yyyy-MM-dd HH:mm") ?? "";
    var flags = (e.IsDirectory ? "D" : " ") + (e.IsEncrypted ? "*" : " ");
    Console.WriteLine($"{flags}{e.Name,-38} {FormatSize(e.OriginalSize),12} {comp,12} {ratio,7} {e.Method,-10} {mod,-20}");
    totalOrig += e.OriginalSize;
    if (e.CompressedSize >= 0) totalComp += e.CompressedSize;
  }

  Console.WriteLine(new string('-', 105));
  var totalRatio = totalOrig > 0 ? $"{100.0 * totalComp / totalOrig:F1}%" : "—";
  Console.WriteLine($"{"Total: " + entries.Count + " entries",-40} {FormatSize(totalOrig),12} {FormatSize(totalComp),12} {totalRatio,7}");
  return 0;
});

// ── extract ──────────────────────────────────────────────────────────

var extractCmd = new Command("extract", """
  Extract files from an archive, including SFX executables.
  Examples:
    cwb extract archive.7z -o output/        Extract all to output/
    cwb extract archive.zip file.txt         Extract specific file
    cwb extract setup.exe -o output/         Extract third-party SFX (7-Zip, WinRAR, etc.)
  """) { archiveArg, outputOpt, passwordOpt, filesArg };
extractCmd.Aliases.Add("x");
extractCmd.SetAction((ParseResult ctx) => {
  var archive = ctx.GetValue(archiveArg)!;
  var output = ctx.GetValue(outputOpt);
  var password = ctx.GetValue(passwordOpt);
  var files = ctx.GetValue(filesArg);

  if (!archive.Exists) { Console.Error.WriteLine($"File not found: {archive.FullName}"); return 1; }

  var outputDir = output?.FullName ?? Directory.GetCurrentDirectory();
  var fileFilter = files is { Length: > 0 } ? files : null;

  Console.Write($"Extracting {archive.Name}...");
  var sw = Stopwatch.StartNew();
  ArchiveOperations.Extract(archive.FullName, outputDir, password, fileFilter);
  sw.Stop();
  Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
  Console.WriteLine($"Output: {outputDir}");
  return 0;
});

// ── create ───────────────────────────────────────────────────────────

var createArchiveArg = new Argument<FileInfo>("archive") { Description = "Path of the archive to create" };
var createFilesArg = new Argument<string[]>("files") { Description = "Files to add", Arity = ArgumentArity.OneOrMore };
var methodOpt = new Option<string?>("--method", "-m") { Description = "Compression method. ZIP: store,deflate,deflate64,bzip2,lzma,ppmd,zstd. 7z: lzma,ppmd,deflate,bzip2,copy. Append '+' for optimal (e.g. deflate+, lzma+)" };
var forceCompressOpt = new Option<bool>("--force-compress") { Description = "Compress all files even if they appear incompressible (compressed/encrypted/random)" };
var threadsOpt = new Option<int>("--threads", "-t") { Description = "Compression threads. ZIP: per-entry parallel. 7z: per-solid-block parallel", DefaultValueFactory = _ => Environment.ProcessorCount };
var solidSizeOpt = new Option<string?>("--solid-size") { Description = "7z solid block size. 0=single block (best ratio), 64m=64MB (default), 1g=1GB" };
var dictSizeOpt = new Option<string?>("--dict-size", "-d") { Description = "LZMA dict: 64k-1g (e.g. 8m, 64m). PPMd memory: 1m-2g. BZip2 block: 100k-900k" };
var wordSizeOpt = new Option<int?>("--word-size", "-w") { Description = "LZMA fast bytes: 5-273 (default 32). PPMd model order: 2-32 (default 6)" };
var levelOpt = new Option<int?>("--level", "-l") { Description = "Level 0-9. Deflate: 0=none,6=default,9=best. LZMA: 1=fast,5=normal,9=best" };
var encryptHeadersOpt = new Option<bool>("--encrypt-headers", "-eh") { Description = "Encrypt file names and headers (7z, RAR5). Requires --password" };
var zipEncryptionOpt = new Option<string?>("--zip-encryption") { Description = "ZIP encryption method: aes256 (default, strong) or zipcrypto (weak, legacy compatible)" };
var sfxOpt = new Option<bool>("--sfx") { Description = "Create a self-extracting archive (console stub, no runtime needed)" };
var sfxUiOpt = new Option<bool>("--sfx-ui") { Description = "Create a self-extracting archive (GUI stub with folder picker, Windows only)" };
var sfxTargetOpt = new Option<string?>("--sfx-target") { Description = "SFX target platform: win-x64, win-x86, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64" };

var createCmd = new Command("create", """
  Create a new archive. Format detected from extension (.zip, .7z, .tar.gz, etc.).

  Examples:
    cwb create backup.zip *.txt                         ZIP with Deflate (default)
    cwb create backup.7z src/ -m lzma -d 64m -l 9      7z, LZMA, 64MB dict, max level
    cwb create backup.7z src/ -m ppmd -w 8 -d 256m     7z, PPMd, order 8, 256MB memory
    cwb create backup.7z src/ -m lzma+ --solid-size 0   7z, best LZMA, single solid block
    cwb create backup.zip docs/ -m bzip2 -d 900k       ZIP, BZip2, 900KB block
    cwb create backup.zip docs/ -m lzma -d 32m -l 9    ZIP, LZMA, 32MB dict
    cwb create backup.zip docs/ -m ppmd -w 6 -d 8m     ZIP, PPMd, order 6, 8MB memory
    cwb create data.tar.xz files/ -d 64m -l 9          tar.xz, LZMA2 64MB dict
    cwb create app.7z src/ --sfx                        Self-extracting 7z (console)
    cwb create app.7z src/ --sfx --sfx-target linux-x64 SFX for Linux x64
    cwb create backup.7z src/ -p secret                 7z with AES-256 encryption
    cwb create backup.zip *.txt -m deflate+ -t 4        Zopfli optimal, 4 threads

  Methods by format:
    ZIP:  store, shrink, reduce, implode, deflate(+), deflate64, bzip2, lzma, ppmd, zstd
    7z:   lzma(+), lzma2, deflate, bzip2, ppmd, copy
    TAR compound: tar.gz, tar.bz2, tar.xz, tar.zst, tar.lz4, tar.lz
    CAB:  mszip, lzx, quantum
    LZH:  lh0-lh7, lzs, lz5, pm0-pm2  |  ARJ: method 1-4  |  ACE: lz77, ace20  |  ARC: store, pack, squeeze

  Key options for 7z:
    --method/-m   lzma, ppmd, deflate, bzip2, copy. Append '+' for best.
    --dict-size   LZMA: 64k-1g (e.g. -d 64m). PPMd: memory size (e.g. -d 256m).
                  BZip2: block×100KB (e.g. -d 900k = 9×100KB).
    --word-size   LZMA: fast bytes 5-273 (e.g. -w 64). PPMd: model order 2-32 (e.g. -w 8).
    --solid-size  Max solid block: 0=single block, 64m=64MB blocks, 1g=1GB blocks.
    --level       0-9: 1=fast, 5=normal, 9=best.
    --threads     Parallel compression (7z splits into solid blocks, ZIP per-entry).
  """) { createArchiveArg, createFilesArg, passwordOpt, methodOpt, forceCompressOpt, threadsOpt, solidSizeOpt, dictSizeOpt, wordSizeOpt, levelOpt, encryptHeadersOpt, zipEncryptionOpt, sfxOpt, sfxUiOpt, sfxTargetOpt };
createCmd.Aliases.Add("c");
createCmd.SetAction((ParseResult ctx) => {
  var archive = ctx.GetValue(createArchiveArg)!;
  var files = ctx.GetValue(createFilesArg)!;
  var password = ctx.GetValue(passwordOpt);
  var method = MethodSpec.Parse(ctx.GetValue(methodOpt));
  var forceCompress = ctx.GetValue(forceCompressOpt);
  var threads = ctx.GetValue(threadsOpt);
  var solidSize = ParseSize(ctx.GetValue(solidSizeOpt));
  var dictSize = ParseSize(ctx.GetValue(dictSizeOpt));
  var wordSize = ctx.GetValue(wordSizeOpt);
  var level = ctx.GetValue(levelOpt);
  var encryptHeaders = ctx.GetValue(encryptHeadersOpt);
  var zipEncryption = ctx.GetValue(zipEncryptionOpt);
  var makeSfx = ctx.GetValue(sfxOpt);
  var makeSfxUi = ctx.GetValue(sfxUiOpt);
  var sfxTarget = ctx.GetValue(sfxTargetOpt);

  List<ArchiveInput> resolved;
  try { resolved = ArchiveInput.Resolve(files); }
  catch (FileNotFoundException ex) { Console.Error.WriteLine(ex.Message); return 1; }

  // Detect incompressible files unless --force-compress is set
  var autoStored = 0;
  if (!forceCompress) {
    foreach (var input in resolved) {
      if (!input.IsDirectory && !string.IsNullOrEmpty(input.FullPath) && EntropyDetector.IsIncompressible(input.FullPath))
        autoStored++;
    }
  }

  var fileCount = resolved.Count(i => !i.IsDirectory);
  var dirCount = resolved.Count(i => i.IsDirectory);
  var countLabel = dirCount > 0 ? $"{fileCount} file(s), {dirCount} dir(s)" : $"{fileCount} file(s)";
  var opts = new CompressionOptions {
    Method = method, Threads = threads, SolidSize = solidSize,
    DictSize = dictSize, WordSize = wordSize, Level = level,
    ForceCompress = forceCompress, Password = password, EncryptFilenames = encryptHeaders,
    ZipEncryption = zipEncryption,
  };
  var methodLabel = method.IsDefault ? "" : $" [{method}]";
  var threadLabel = threads > 1 ? $" ({threads} threads)" : "";
  Console.Write($"Creating {archive.Name} with {countLabel}{methodLabel}{threadLabel}...");
  var sw = Stopwatch.StartNew();
  ArchiveOperations.Create(archive.FullName, resolved, opts);
  sw.Stop();
  Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
  if (autoStored > 0)
    Console.WriteLine($"  {autoStored} file(s) auto-stored as incompressible (use --force-compress to override)");
  Console.WriteLine($"Archive size: {FormatSize(new FileInfo(archive.FullName).Length)}");

  // Wrap into SFX if requested
  if (makeSfx || makeSfxUi) {
    var stubType = makeSfxUi ? SfxBuilder.StubType.Ui : SfxBuilder.StubType.Cli;
    var sfxPath = Path.ChangeExtension(archive.FullName, ".exe");
    try {
      Console.Write($"Creating SFX ({stubType})...");
      SfxBuilder.WrapExisting(archive.FullName, sfxPath, stubType, sfxTarget);
      Console.WriteLine($" done");
      Console.WriteLine($"SFX size: {FormatSize(new FileInfo(sfxPath).Length)}");
      // Delete the intermediate archive, keep only the .exe
      File.Delete(archive.FullName);
    }
    catch (Exception ex) {
      Console.Error.WriteLine($"\nSFX creation failed: {ex.Message}");
      Console.Error.WriteLine("The archive was still created successfully.");
    }
  }

  return 0;
});

// ── test ─────────────────────────────────────────────────────────────

var testCmd = new Command("test", "Test archive integrity") { archiveArg, passwordOpt };
testCmd.Aliases.Add("t");
testCmd.SetAction((ParseResult ctx) => {
  var archive = ctx.GetValue(archiveArg)!;
  var password = ctx.GetValue(passwordOpt);

  if (!archive.Exists) { Console.Error.WriteLine($"File not found: {archive.FullName}"); return 1; }

  Console.Write($"Testing {archive.Name}...");
  var sw = Stopwatch.StartNew();
  var ok = ArchiveOperations.Test(archive.FullName, password);
  sw.Stop();
  Console.WriteLine(ok ? $" OK ({sw.ElapsedMilliseconds}ms)" : " FAILED");
  return ok ? 0 : 1;
});

// ── info ─────────────────────────────────────────────────────────────

var infoCmd = new Command("info", "Show detailed archive information") { archiveArg, passwordOpt };
infoCmd.SetAction((ParseResult ctx) => {
  var archive = ctx.GetValue(archiveArg)!;
  var password = ctx.GetValue(passwordOpt);

  if (!archive.Exists) { Console.Error.WriteLine($"File not found: {archive.FullName}"); return 1; }

  var format = FormatDetector.Detect(archive.FullName);
  var entries = ArchiveOperations.List(archive.FullName, password);
  var fi = new FileInfo(archive.FullName);

  Console.WriteLine($"File:          {archive.Name}");
  Console.WriteLine($"Path:          {archive.FullName}");
  Console.WriteLine($"Format:        {format}");
  Console.WriteLine($"Archive size:  {FormatSize(fi.Length)} ({fi.Length:N0} bytes)");
  Console.WriteLine($"Entries:       {entries.Count}");

  var totalOrig = entries.Sum(e => e.OriginalSize);
  var totalComp = entries.Where(e => e.CompressedSize >= 0).Sum(e => e.CompressedSize);
  Console.WriteLine($"Original size: {FormatSize(totalOrig)} ({totalOrig:N0} bytes)");
  if (totalComp > 0) Console.WriteLine($"Ratio:         {(totalOrig > 0 ? 100.0 * totalComp / totalOrig : 0):F1}%");

  var encrypted = entries.Count(e => e.IsEncrypted);
  if (encrypted > 0) Console.WriteLine($"Encrypted:     {encrypted} of {entries.Count} entries");

  var methods = entries.Select(e => e.Method).Distinct().ToArray();
  if (methods.Length > 0) Console.WriteLine($"Methods:       {string.Join(", ", methods)}");
  return 0;
});

// ── convert ──────────────────────────────────────────────────────────

var convertInputArg = new Argument<FileInfo>("input") { Description = "Source archive" };
var convertOutputArg = new Argument<FileInfo>("output") { Description = "Destination archive (format from extension)" };

var convertMethodOpt = new Option<string?>("--method", "-m") { Description = "Target compression method. Append '+' for optimal encoding (e.g. deflate+)" };
var convertCmd = new Command("convert", """
  Convert between archive formats. Uses fastest tier possible (bitstream > restream > recompress).
  Examples:
    cwb convert in.zip out.7z                   ZIP → 7z (Tier 3: full recompress)
    cwb convert in.tar.gz out.tar.xz            tar.gz → tar.xz (Tier 2: restream)
    cwb convert in.gz out.zlib                  gz → zlib (Tier 1: bitstream transfer)
    cwb convert in.zip out.7z -m lzma+          Convert with best LZMA
  """) { convertInputArg, convertOutputArg, passwordOpt, convertMethodOpt };
convertCmd.SetAction((ParseResult ctx) => {
  var input = ctx.GetValue(convertInputArg)!;
  var output = ctx.GetValue(convertOutputArg)!;
  var password = ctx.GetValue(passwordOpt);
  var method = MethodSpec.Parse(ctx.GetValue(convertMethodOpt));

  if (!input.Exists) { Console.Error.WriteLine($"File not found: {input.FullName}"); return 1; }

  var srcFormat = FormatDetector.Detect(input.FullName);
  var dstFormat = FormatDetector.DetectByExtension(output.FullName);
  var methodLabel = method.IsDefault ? "" : $" [{method}]";
  Console.WriteLine($"Converting {input.Name} ({srcFormat}) -> {output.Name} ({dstFormat}){methodLabel}");

  var sw = Stopwatch.StartNew();
  var (strategy, tier) = ArchiveOperations.Convert(input.FullName, output.FullName, password, method);
  sw.Stop();
  Console.WriteLine($"Done ({sw.ElapsedMilliseconds}ms, tier {tier}: {strategy}). Output: {FormatSize(new FileInfo(output.FullName).Length)}");
  return 0;
});

// ── benchmark ────────────────────────────────────────────────────────

var benchFileArg = new Argument<FileInfo>("file") { Description = "File to benchmark" };

var benchCmd = new Command("benchmark", "Compare compression across algorithms") { benchFileArg };
benchCmd.Aliases.Add("bench");
benchCmd.SetAction((ParseResult ctx) => {
  var file = ctx.GetValue(benchFileArg)!;
  if (!file.Exists) { Console.Error.WriteLine($"File not found: {file.FullName}"); return 1; }

  var data = File.ReadAllBytes(file.FullName);
  Console.WriteLine($"Benchmarking: {file.Name} ({FormatSize(data.Length)})");
  Console.WriteLine();
  Console.WriteLine($"{"Algorithm",-16} {"Compressed",12} {"Ratio",8} {"Compress",10} {"Decompress",12}");
  Console.WriteLine(new string('-', 62));

  FormatRegistration.EnsureInitialized();
  foreach (var block in BuildingBlockRegistry.All.OrderBy(b => b.DisplayName)) {
    BenchmarkBlock(block.DisplayName, data, block);
  }
  return 0;
});

// ── formats ──────────────────────────────────────────────────────────

var formatsCmd = new Command("formats", "List all supported formats");
formatsCmd.SetAction((ParseResult _) => {
  FormatRegistration.EnsureInitialized();

  var archives = FormatRegistry.GetByCategory(FormatCategory.Archive).Select(d => d.DisplayName.ToLowerInvariant());
  var streams = FormatRegistry.GetByCategory(FormatCategory.Stream)
    .Concat(FormatRegistry.GetByCategory(FormatCategory.Wrapper))
    .Select(d => d.DisplayName.ToLowerInvariant());
  var compounds = FormatRegistry.GetByCategory(FormatCategory.CompoundTar).Select(d => d.DisplayName.ToLowerInvariant());

  Console.WriteLine("Supported archive formats:");
  Console.WriteLine("  " + string.Join(", ", archives));
  Console.WriteLine();
  Console.WriteLine("Supported compression/stream formats:");
  Console.WriteLine("  " + string.Join(", ", streams));
  Console.WriteLine();
  Console.WriteLine("Compound formats:");
  Console.WriteLine("  " + string.Join(", ", compounds));
  Console.WriteLine();
  Console.WriteLine("Convert between any pair. (*) = detection only.");
  Console.WriteLine("Compound tar conversions use fast restreaming.");
  Console.WriteLine();
  Console.WriteLine("Conversion tiers:");
  Console.WriteLine("  Tier 1: Bitstream transfer — same codec, different container (zero decompression)");
  Console.WriteLine("  Tier 2: Container restream — decompress + recompress, preserving inner payload");
  Console.WriteLine("  Tier 3: Full recompress — extract + re-encode (also used for method changes and '+')");
  Console.WriteLine();
  Console.WriteLine("Optimized methods (append '+' for optimal encoding, e.g. --method deflate+):");
  Console.WriteLine("  deflate+   Zopfli optimal Deflate (ZIP, Gzip, Zlib)");
  Console.WriteLine("  lzma+      Best LZMA (7z, XZ, LZMA, Lzip)");
  Console.WriteLine("  zstd+      Best Zstd");
  Console.WriteLine("  brotli+    Best Brotli");
  Console.WriteLine("  lz4+       HC maximum (LZ4)");
  Console.WriteLine("  lzw+       Optimal LZW (Unix .Z)");
  Console.WriteLine("  lzo+       LZO1X-999 (LZOP)");
  Console.WriteLine();
  Console.WriteLine("Incompressibility detection:");
  Console.WriteLine("  Files are tested with a chi-square byte distribution test before compression.");
  Console.WriteLine("  Already-compressed, encrypted, or random files are auto-stored (ZIP: Store method).");
  Console.WriteLine("  Use --force-compress to override and compress all files regardless.");
  Console.WriteLine();
  Console.WriteLine("Parallel compression (--threads N, --solid-size SIZE):");
  Console.WriteLine("  ZIP:  Each entry compressed independently on a separate thread.");
  Console.WriteLine("  7z:   Entries split into solid blocks (default 64MB), each block compressed in parallel.");
  Console.WriteLine("        --solid-size 0 = single solid block (no splitting, best ratio).");
  Console.WriteLine("        --solid-size 128m = 128MB blocks (balance speed/ratio).");
  Console.WriteLine("  Files are grouped by extension for better compression within solid blocks.");
  Console.WriteLine();
  Console.WriteLine("Fine-tuning (matching 7-Zip options):");
  Console.WriteLine("  --dict-size SIZE  Dictionary size (e.g. 64k, 8m, 64m, 128m)");
  Console.WriteLine("                    LZMA: 64k-1g (default 8m, 64m with +)");
  Console.WriteLine("                    BZip2: mapped to block size 100k-900k");
  Console.WriteLine("  --word-size N     Word size / fast bytes / PPMd model order");
  Console.WriteLine("                    Deflate: 3-258 (default 32). LZMA: 5-273 (default 32)");
  Console.WriteLine("                    PPMd ZIP: 2-16 (default 6). PPMd 7z: 2-32 (default 6)");
  Console.WriteLine("  --level N         Compression level 0-9");
  Console.WriteLine("                    Deflate: 0=none, 1=fast, 6=default, 9=best");
  Console.WriteLine("                    LZMA: 1=fast, 5=normal, 7+=best");
  Console.WriteLine();
  Console.WriteLine("Self-extracting archives (--sfx / --sfx-ui):");
  Console.WriteLine("  Produces a single standalone executable with the archive embedded.");
  Console.WriteLine("  No DLLs or runtime required — fully self-contained.");
  Console.WriteLine("  --sfx-target RID  Target platform (default: current platform)");
  Console.WriteLine($"    Supported: {string.Join(", ", SfxBuilder.SupportedTargets)}");
  Console.WriteLine("  SFX stubs are embedded; for dev builds run: .\\publish-sfx-stubs.ps1");
  Console.WriteLine();
  Console.WriteLine("Third-party SFX reading:");
  Console.WriteLine("  cwb can list, extract, and test SFX executables from other tools.");
  Console.WriteLine("  Supported: 7-Zip SFX, WinRAR SFX, WinZip SFX, PKZIP SFX, ARJ SFX,");
  Console.WriteLine("             LHA SFX, ACE SFX, CAB SFX (any PE with embedded archive).");
  Console.WriteLine("  Detection: parses PE overlay and scans for archive signatures.");
  return 0;
});

// ── optimize ─────────────────────────────────────────────────────────

var optimizeInputArg = new Argument<FileInfo>("input") { Description = "Archive to optimize" };
var optimizeOutputArg = new Argument<FileInfo>("output") { Description = "Optimized output (same format)" };

var optimizeCmd = new Command("optimize", "Re-encode with optimal compression (Zopfli for Deflate, Best for LZMA)") {
  optimizeInputArg, optimizeOutputArg, passwordOpt
};
optimizeCmd.Aliases.Add("opt");
optimizeCmd.SetAction((ParseResult ctx) => {
  var input = ctx.GetValue(optimizeInputArg)!;
  var output = ctx.GetValue(optimizeOutputArg)!;
  var password = ctx.GetValue(passwordOpt);

  if (!input.Exists) { Console.Error.WriteLine($"File not found: {input.FullName}"); return 1; }

  var format = FormatDetector.Detect(input.FullName);
  Console.Write($"Optimizing {input.Name} ({format})...");
  var sw = Stopwatch.StartNew();
  var (origSize, optSize, count) = ArchiveOperations.Optimize(input.FullName, output.FullName, password);
  sw.Stop();

  var saving = origSize > 0 ? (1.0 - (double)optSize / origSize) * 100 : 0;
  Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
  Console.WriteLine($"  Original:  {FormatSize(origSize)}");
  Console.WriteLine($"  Optimized: {FormatSize(optSize)} ({saving:F1}% smaller, {count} entries)");
  return 0;
});

// ── analyze ──────────────────────────────────────────────────────────

var analyzeFileArg = new Argument<FileInfo>("file") { Description = "File to analyze" };
var deepScanOpt = new Option<bool>("--deep-scan") { Description = "Scan for known format signatures at every offset" };
var fingerprintOpt = new Option<bool>("--fingerprint") { Description = "Run algorithm fingerprinting heuristics" };
var trialOpt = new Option<bool>("--trial") { Description = "Try decompressing with all known algorithms" };
var entropyMapOpt = new Option<bool>("--entropy-map") { Description = "Show per-region entropy map" };
var chainOpt = new Option<bool>("--chain") { Description = "Attempt chain reconstruction (peel layers)" };
var allOpt = new Option<bool>("--all") { Description = "Enable all analysis modes" };
var maxDepthOpt = new Option<int>("--max-depth") { Description = "Chain reconstruction depth limit", DefaultValueFactory = _ => 10 };
var windowOpt = new Option<int>("--window") { Description = "Entropy map window size", DefaultValueFactory = _ => 256 };
var analyzeOffsetOpt = new Option<long>("--offset") { Description = "Start analysis at byte offset", DefaultValueFactory = _ => 0L };
var analyzeLengthOpt = new Option<long>("--length") { Description = "Analyze only N bytes", DefaultValueFactory = _ => 0L };

var analyzeCmd = new Command("analyze", """
  Analyze binary data: signatures, fingerprinting, entropy map, trial decompression, chain reconstruction.
  Example: cwb analyze mystery.bin --all
  """) { analyzeFileArg, deepScanOpt, fingerprintOpt, trialOpt, entropyMapOpt, chainOpt, allOpt, maxDepthOpt, windowOpt, analyzeOffsetOpt, analyzeLengthOpt };
analyzeCmd.SetAction((ParseResult ctx) => {
  var file = ctx.GetValue(analyzeFileArg)!;
  if (!file.Exists) { Console.Error.WriteLine($"File not found: {file.FullName}"); return 1; }

  var options = new Compression.Analysis.AnalysisOptions {
    DeepScan = ctx.GetValue(deepScanOpt),
    Fingerprint = ctx.GetValue(fingerprintOpt),
    Trial = ctx.GetValue(trialOpt),
    EntropyMap = ctx.GetValue(entropyMapOpt),
    Chain = ctx.GetValue(chainOpt),
    All = ctx.GetValue(allOpt),
    MaxDepth = ctx.GetValue(maxDepthOpt),
    WindowSize = ctx.GetValue(windowOpt),
    Offset = ctx.GetValue(analyzeOffsetOpt),
    Length = ctx.GetValue(analyzeLengthOpt),
  };

  // If no specific mode selected, enable all
  if (!options.DeepScan && !options.Fingerprint && !options.Trial && !options.EntropyMap && !options.Chain && !options.All)
    options = new Compression.Analysis.AnalysisOptions {
      All = true, MaxDepth = options.MaxDepth, WindowSize = options.WindowSize,
      Offset = options.Offset, Length = options.Length,
    };

  var data = File.ReadAllBytes(file.FullName);
  var analyzer = new Compression.Analysis.BinaryAnalyzer(options);
  var result = analyzer.Analyze(data);

  Console.WriteLine($"File: {file.Name}  Size: {FormatSize(file.Length)}");
  Console.WriteLine();

  // Statistics
  if (result.Statistics != null) {
    var s = result.Statistics;
    Console.WriteLine("── Statistics ──");
    Console.WriteLine($"  Entropy:      {s.Entropy:F4} bits/byte");
    Console.WriteLine($"  Mean:         {s.Mean:F4}");
    Console.WriteLine($"  Chi-square:   {s.ChiSquare:F2}  (p={s.PValue:F6})");
    Console.WriteLine($"  Serial corr:  {s.SerialCorrelation:F6}");
    Console.WriteLine($"  Unique bytes: {s.UniqueBytesCount}/256");
    Console.WriteLine();
  }

  // Signatures
  if (result.Signatures is { Count: > 0 }) {
    Console.WriteLine("── Signatures ──");
    foreach (var sig in result.Signatures.Take(20))
      Console.WriteLine($"  [{sig.Offset,8}]  {sig.FormatName,-16}  conf={sig.Confidence:F2}  {sig.HeaderPreview}");
    Console.WriteLine();
  }

  // Fingerprints
  if (result.Fingerprints is { Count: > 0 }) {
    Console.WriteLine("── Fingerprinting ──");
    foreach (var fp in result.Fingerprints)
      Console.WriteLine($"  {fp.Algorithm,-20}  conf={fp.Confidence:F2}  {fp.Explanation}");
    Console.WriteLine();
  }

  // Entropy map
  if (result.EntropyMap is { Count: > 0 }) {
    Console.WriteLine("── Entropy Map ──");
    foreach (var r in result.EntropyMap.Take(64)) {
      var bar = new string('#', (int)(r.Entropy / 8.0 * 40));
      Console.WriteLine($"  [{r.Offset,8}..{r.Offset + r.Length - 1,8}]  {r.Entropy:F2}  {bar,-40}  {r.Classification}");
    }
    Console.WriteLine();
  }

  // Trial results
  if (result.TrialResults is { Count: > 0 }) {
    Console.WriteLine("── Trial Decompression ──");
    foreach (var t in result.TrialResults.Take(10))
      Console.WriteLine($"  {t.Algorithm,-16}  output={FormatSize(t.OutputSize)}  entropy={t.OutputEntropy:F2}");
    Console.WriteLine();
  }

  // Chain
  if (result.Chain is { Depth: > 0 }) {
    Console.WriteLine("── Chain Reconstruction ──");
    for (var i = 0; i < result.Chain.Layers.Count; i++) {
      var l = result.Chain.Layers[i];
      Console.WriteLine($"  Layer {i + 1}: {l.Algorithm,-16}  {FormatSize(l.InputSize)} → {FormatSize(l.OutputSize)}  conf={l.Confidence:F2}");
    }
    Console.WriteLine($"  Final: {FormatSize(result.Chain.FinalData.Length)}");
    Console.WriteLine();
  }
  else if (result.Chain != null) {
    Console.WriteLine("── Chain Reconstruction ──");
    Console.WriteLine("  No compression layers detected.");
    Console.WriteLine();
  }

  return 0;
});

// ── root ─────────────────────────────────────────────────────────────

var root = new RootCommand("""
  cwb — CompressionWorkbench CLI. A universal archive tool.

  Quick examples:
    cwb create backup.7z Documents/           Create 7z from a folder
    cwb create out.zip *.txt -m deflate       Create ZIP with Deflate
    cwb extract archive.rar -o output/        Extract RAR to output/
    cwb list archive.tar.gz                   List contents
    cwb convert in.zip out.7z -m lzma         Convert ZIP → 7z with LZMA
    cwb create app.7z files --sfx             Create self-extracting 7z
    cwb test archive.zip                      Verify integrity

  Format is auto-detected from extension. Run 'cwb formats' for full format list,
  or 'cwb create --help' for compression options and examples.
  """) {
  listCmd, extractCmd, createCmd, testCmd, infoCmd, convertCmd, optimizeCmd, benchCmd, formatsCmd, analyzeCmd
};

return root.Parse(args).Invoke();

// ── Utility functions ────────────────────────────────────────────────

static string FormatSize(long bytes) => bytes switch {
  < 1024 => $"{bytes} B",
  < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
  < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
  _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
};

static void BenchmarkBlock(string name, byte[] data, IBuildingBlock block) {
  try {
    // Compress
    var compSw = Stopwatch.StartNew();
    var compressed = block.Compress(data);
    compSw.Stop();

    // Decompress
    var decompSw = Stopwatch.StartNew();
    block.Decompress(compressed);
    decompSw.Stop();

    var ratio = data.Length > 0 ? 100.0 * compressed.Length / data.Length : 0;
    Console.WriteLine($"{name,-16} {FormatSize(compressed.Length),12} {ratio:F1}%{"",4} {compSw.ElapsedMilliseconds,7}ms {decompSw.ElapsedMilliseconds,9}ms");
  }
  catch (Exception ex) {
    Console.WriteLine($"{name,-16} {"FAILED",-12} {ex.Message}");
  }
}

static long ParseSize(string? sizeStr) {
  if (string.IsNullOrWhiteSpace(sizeStr)) return SolidBlockPlanner.DefaultMaxBlockSize;
  var s2 = sizeStr.Trim().ToLowerInvariant();
  if (s2 == "0") return 0; // single block (no splitting)
  long multiplier = 1;
  if (s2.EndsWith('k')) { multiplier = 1024; s2 = s2[..^1]; }
  else if (s2.EndsWith('m')) { multiplier = 1024 * 1024; s2 = s2[..^1]; }
  else if (s2.EndsWith('g')) { multiplier = 1024L * 1024 * 1024; s2 = s2[..^1]; }
  return long.TryParse(s2, out var val) ? val * multiplier : SolidBlockPlanner.DefaultMaxBlockSize;
}

