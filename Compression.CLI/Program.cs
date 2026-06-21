using System.CommandLine;
using System.Diagnostics;
using Compression.Core.DiskImage;
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

// ── add / remove / replace ───────────────────────────────────────────

var addArchiveArg = new Argument<FileInfo>("archive") { Description = "Archive to modify" };
var addFilesArg = new Argument<string[]>("files") { Description = "Files to add (or replace by name)", Arity = ArgumentArity.OneOrMore };

var addCmd = new Command("add", """
  Add (or replace by name) files inside an existing archive.
  Routes through IArchiveModifiable for true random-access I/O when the
  format supports it (retro disk filesystems: D64/D71/D81/AppleDOS/ProDOS/
  Atari8/BBC/ADF/etc.); falls back to extract-add-recreate for ZIP/7z/RAR/
  tar variants.
  Examples:
    cwb add disk.d81 game.prg                     Add into 1581 disk
    cwb add archive.zip notes.txt -m deflate      Add with re-encoding
  """) { addArchiveArg, addFilesArg, methodOpt, levelOpt, dictSizeOpt, passwordOpt };
addCmd.SetAction((ParseResult ctx) => {
  var archive = ctx.GetValue(addArchiveArg)!;
  var files = ctx.GetValue(addFilesArg)!;
  if (!archive.Exists) { Console.Error.WriteLine($"File not found: {archive.FullName}"); return 1; }

  List<ArchiveInput> resolved;
  try { resolved = ArchiveInput.Resolve(files); }
  catch (FileNotFoundException ex) { Console.Error.WriteLine(ex.Message); return 1; }

  var opts = new CompressionOptions {
    Method = MethodSpec.Parse(ctx.GetValue(methodOpt)),
    Level = ctx.GetValue(levelOpt),
    DictSize = ParseSize(ctx.GetValue(dictSizeOpt)),
    Password = ctx.GetValue(passwordOpt),
  };

  Console.Write($"Adding {resolved.Count} file(s) to {archive.Name}...");
  var sw = Stopwatch.StartNew();
  ArchiveOperations.Add(archive.FullName, resolved, opts);
  sw.Stop();
  Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
  return 0;
});

var removeArchiveArg = new Argument<FileInfo>("archive") { Description = "Archive to modify" };
var removeNamesArg = new Argument<string[]>("names") { Description = "Entry names to remove", Arity = ArgumentArity.OneOrMore };

var removeCmd = new Command("remove", """
  Remove named entries from an existing archive. Prefers the modifier path
  for true random-access I/O; falls back to extract-skip-recreate.
  Examples:
    cwb remove disk.d64 OLDFILE                   Drop one entry
    cwb remove archive.zip readme.txt notes.txt   Drop multiple entries
  """) { removeArchiveArg, removeNamesArg, methodOpt, levelOpt, passwordOpt };
removeCmd.Aliases.Add("rm");
removeCmd.SetAction((ParseResult ctx) => {
  var archive = ctx.GetValue(removeArchiveArg)!;
  var names = ctx.GetValue(removeNamesArg)!;
  if (!archive.Exists) { Console.Error.WriteLine($"File not found: {archive.FullName}"); return 1; }

  var opts = new CompressionOptions {
    Method = MethodSpec.Parse(ctx.GetValue(methodOpt)),
    Level = ctx.GetValue(levelOpt),
    Password = ctx.GetValue(passwordOpt),
  };

  Console.Write($"Removing {names.Length} entry(ies) from {archive.Name}...");
  var sw = Stopwatch.StartNew();
  ArchiveOperations.Remove(archive.FullName, names, opts);
  sw.Stop();
  Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
  return 0;
});

var replaceArchiveArg = new Argument<FileInfo>("archive") { Description = "Archive to modify" };
var replaceNameArg = new Argument<string>("name") { Description = "Existing entry name to replace" };
var replaceFileArg = new Argument<FileInfo>("file") { Description = "Replacement source file" };

var replaceCmd = new Command("replace", """
  Replace an existing entry with the contents of a new file. Sugar for
  remove + add — uses the modifier path when available so the operation
  touches only the metadata sectors and the new file's data.
  Example:
    cwb replace disk.d64 INTRO patched.prg
  """) { replaceArchiveArg, replaceNameArg, replaceFileArg, methodOpt, levelOpt, passwordOpt };
replaceCmd.SetAction((ParseResult ctx) => {
  var archive = ctx.GetValue(replaceArchiveArg)!;
  var name = ctx.GetValue(replaceNameArg)!;
  var file = ctx.GetValue(replaceFileArg)!;
  if (!archive.Exists) { Console.Error.WriteLine($"File not found: {archive.FullName}"); return 1; }
  if (!file.Exists) { Console.Error.WriteLine($"File not found: {file.FullName}"); return 1; }

  var opts = new CompressionOptions {
    Method = MethodSpec.Parse(ctx.GetValue(methodOpt)),
    Level = ctx.GetValue(levelOpt),
    Password = ctx.GetValue(passwordOpt),
  };

  Console.Write($"Replacing '{name}' in {archive.Name}...");
  var sw = Stopwatch.StartNew();
  ArchiveOperations.Replace(archive.FullName, name, file.FullName, opts);
  sw.Stop();
  Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
  return 0;
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
var optimizeOutputArg = new Argument<FileInfo?>("output") {
  Description = "Optimized output (same format); optional with --search-blocks",
  Arity = ArgumentArity.ZeroOrOne,
};
var searchBlocksOpt = new Option<bool>("--search-blocks", "--best") {
  Description = "Instead of same-format re-encode, find the best building block for this data",
};
var applyOpt = new Option<FileInfo?>("--apply") {
  Description = "With --search-blocks: write the winning block's compressed output to this path",
};

var optimizeCmd = new Command("optimize", "Re-encode with optimal compression (Zopfli for Deflate, Best for LZMA)") {
  optimizeInputArg, optimizeOutputArg, passwordOpt, searchBlocksOpt, applyOpt
};
optimizeCmd.Aliases.Add("opt");
optimizeCmd.SetAction((ParseResult ctx) => {
  var input = ctx.GetValue(optimizeInputArg)!;
  var output = ctx.GetValue(optimizeOutputArg);
  var password = ctx.GetValue(passwordOpt);

  if (!input.Exists) { Console.Error.WriteLine($"File not found: {input.FullName}"); return 1; }

  // --search-blocks / --best: cross-block auto-selection on the raw bytes.
  if (ctx.GetValue(searchBlocksOpt)) {
    var apply = ctx.GetValue(applyOpt);
    return RunBestFit(input, apply);
  }

  if (output is null) {
    Console.Error.WriteLine("An output path is required (or use --search-blocks to report the best building block).");
    return 1;
  }

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

// ── bestfit ──────────────────────────────────────────────────────────

var bestfitInputArg = new Argument<FileInfo>("file") { Description = "File whose data to fit a compressor to" };
var bestfitApplyOpt = new Option<FileInfo?>("--apply") {
  Description = "Write the winning building block's compressed output to this path",
};
var bestfitRatioOpt = new Option<bool>("--ratio") {
  Description = "Objective: best ratio within a speed window of the fastest block (default: smallest output)",
};

var bestfitCmd = new Command("bestfit", """
  Benchmark every building block on a file's data, print the ranked table, and
  report the winning compressor. Use --apply to write the winner's output.
  Example: cwb bestfit data.bin --apply data.best
  """) { bestfitInputArg, bestfitApplyOpt, bestfitRatioOpt };
bestfitCmd.SetAction((ParseResult ctx) => {
  var file = ctx.GetValue(bestfitInputArg)!;
  if (!file.Exists) { Console.Error.WriteLine($"File not found: {file.FullName}"); return 1; }
  var objective = ctx.GetValue(bestfitRatioOpt)
    ? Compression.Analysis.BestBlockSelector.Objective.BestRatioWithinSpeedWindow
    : Compression.Analysis.BestBlockSelector.Objective.SmallestOutput;
  return RunBestFit(file, ctx.GetValue(bestfitApplyOpt), objective);
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

var analyzeRecursiveOpt = new Option<bool>("--recursive") { Description = "Recursively descend into disk image partitions" };
var clusterHintOpt = new Option<bool>("--cluster-hint") { Description = "Analyze FAT image file sizes and suggest optimal cluster size to minimize slack" };

var analyzeCmd = new Command("analyze", """
  Analyze binary data: signatures, fingerprinting, entropy map, trial decompression, chain reconstruction.
  Use --recursive to descend into disk image partitions (VHD, VMDK, QCOW2, VDI).
  Use --cluster-hint to suggest optimal cluster size for a FAT image.
  Examples:
    cwb analyze mystery.bin --all
    cwb analyze disk.vhd --recursive
    cwb analyze disk.img --cluster-hint
  """) { analyzeFileArg, deepScanOpt, fingerprintOpt, trialOpt, entropyMapOpt, chainOpt, allOpt, maxDepthOpt, windowOpt, analyzeOffsetOpt, analyzeLengthOpt, analyzeRecursiveOpt, clusterHintOpt };
analyzeCmd.SetAction((ParseResult ctx) => {
  var file = ctx.GetValue(analyzeFileArg)!;
  if (!file.Exists) { Console.Error.WriteLine($"File not found: {file.FullName}"); return 1; }

  // --cluster-hint: dedicated FAT cluster size analysis
  if (ctx.GetValue(clusterHintOpt)) {
    try {
      FormatRegistration.EnsureInitialized();
      using var stream = File.OpenRead(file.FullName);
      var hint = FileSystem.Fat.FatShrinkHelper.AnalyzeClusterSizes(stream);
      Console.WriteLine($"File: {file.Name}");
      Console.WriteLine($"Current: {hint.CurrentClusterSize}-byte clusters, {hint.CurrentSlackPercent:F1}% slack");
      Console.WriteLine($"Optimal: {hint.RecommendedClusterSize}-byte clusters, {hint.RecommendedSlackPercent:F1}% slack");
      Console.WriteLine();
      Console.WriteLine($"{"Cluster Size",14} {"Slack",10} {"Allocated",12} {"Slack %",8}");
      Console.WriteLine(new string('-', 48));
      foreach (var s in hint.AllStats) {
        var marker = s.ClusterSize == hint.CurrentClusterSize ? " <-current" :
                     s.ClusterSize == hint.RecommendedClusterSize ? " <-optimal" : "";
        Console.WriteLine($"{s.ClusterSize,14} {FormatSize(s.TotalSlack),10} {FormatSize(s.TotalAllocated),12} {s.SlackPercent,7:F1}%{marker}");
      }
      return 0;
    } catch (Exception ex) {
      Console.Error.WriteLine($"Cluster hint analysis failed: {ex.Message}");
      return 1;
    }
  }

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

  // Recursive disk image analysis
  if (ctx.GetValue(analyzeRecursiveOpt)) {
    FormatRegistration.EnsureInitialized();
    using var fs = File.OpenRead(file.FullName);
    var extractor = new Compression.Analysis.AutoExtractor();
    var extractResult = extractor.Extract(fs);

    if (extractResult?.PartitionTable != null) {
      var pt = extractResult.PartitionTable;
      Console.WriteLine($"── Partition Table ({pt.Scheme}) ──");
      Console.WriteLine($"  {"#",-3} {"Type",-24} {"Offset",12} {"Size",12} {"Filesystem",-16}");
      Console.WriteLine("  " + new string('-', 70));
      foreach (var p in pt.Partitions) {
        var fsLabel = p.NestedResult != null ? p.NestedResult.FormatName : "(unrecognized)";
        Console.WriteLine($"  {p.Index,-3} {p.TypeName,-24} {FormatSize(p.Offset),12} {FormatSize(p.Size),12} {fsLabel,-16}");
        if (p.NestedResult != null) {
          Console.WriteLine($"      Entries: {p.NestedResult.Entries.Count}");
          foreach (var entry in p.NestedResult.Entries.Take(10)) {
            Console.WriteLine($"        {entry.Name,-40} {FormatSize(entry.Data.Length),12}");
          }
          if (p.NestedResult.Entries.Count > 10)
            Console.WriteLine($"        ... and {p.NestedResult.Entries.Count - 10} more");
          if (p.NestedResult.NestedResults.Count > 0)
            Console.WriteLine($"      Nested archives: {p.NestedResult.NestedResults.Count}");
        }
      }
      Console.WriteLine();
    } else if (extractResult != null) {
      Console.WriteLine("── Disk Image ──");
      Console.WriteLine("  No partition table detected (may be a raw filesystem image).");
      Console.WriteLine();
    }
  }

  return 0;
});

// ── auto-extract ─────────────────────────────────────────────────────

var autoExtractFileArg = new Argument<FileInfo>("file") { Description = "File to auto-detect and extract" };
var autoExtractOutputOpt = new Option<DirectoryInfo?>("-o") { Description = "Output directory" };
var autoExtractRecursiveOpt = new Option<bool>("--recursive") { Description = "Recursively extract nested archives" };

var autoExtractCmd = new Command("auto-extract", """
  Auto-detect format and extract. Optionally recurse into nested archives.
  Example: cwb auto-extract mystery.bin -o output/ --recursive
  """) { autoExtractFileArg, autoExtractOutputOpt, autoExtractRecursiveOpt };
autoExtractCmd.SetAction((ParseResult ctx) => {
  var file = ctx.GetValue(autoExtractFileArg)!;
  if (!file.Exists) { Console.Error.WriteLine($"File not found: {file.FullName}"); return 1; }

  var outputDir = ctx.GetValue(autoExtractOutputOpt)?.FullName
    ?? Path.Combine(Environment.CurrentDirectory, Path.GetFileNameWithoutExtension(file.Name));
  Directory.CreateDirectory(outputDir);

  using var fs = File.OpenRead(file.FullName);
  var extractor = new Compression.Analysis.AutoExtractor();
  var result = extractor.Extract(fs);

  if (result == null) {
    Console.Error.WriteLine("Could not detect format.");
    return 1;
  }

  Console.WriteLine($"Detected: {result.FormatName}");
  Console.WriteLine($"Entries: {result.Entries.Count}");

  foreach (var entry in result.Entries) {
    if (entry.IsDirectory) continue;
    var outPath = Path.Combine(outputDir, entry.Name.Replace('/', Path.DirectorySeparatorChar));
    var dir = Path.GetDirectoryName(outPath);
    if (dir != null) Directory.CreateDirectory(dir);
    File.WriteAllBytes(outPath, entry.Data);
    Console.WriteLine($"  {entry.Name} ({FormatSize(entry.Data.Length)})");
  }

  if (ctx.GetValue(autoExtractRecursiveOpt)) {
    // Show partition table info if detected.
    if (result.PartitionTable != null) {
      var pt = result.PartitionTable;
      Console.WriteLine($"\nPartition table detected: {pt.Scheme} ({pt.Partitions.Count} partitions)");
      foreach (var p in pt.Partitions) {
        var fsLabel = p.NestedResult != null ? p.NestedResult.FormatName : "(unrecognized)";
        Console.WriteLine($"  Partition {p.Index}: {p.TypeName} ({FormatSize(p.Size)}) -> {fsLabel}");
        if (p.NestedResult != null) {
          var partDir = Path.Combine(outputDir, $"partition_{p.Index}_{p.TypeName.Replace('/', '_').Replace(' ', '_')}");
          Directory.CreateDirectory(partDir);
          foreach (var entry in p.NestedResult.Entries) {
            if (entry.IsDirectory) continue;
            var outPath = Path.Combine(partDir, entry.Name.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(outPath);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outPath, entry.Data);
            Console.WriteLine($"    {entry.Name} ({FormatSize(entry.Data.Length)})");
          }
        }
      }
    }

    // Extract nested archives.
    if (result.NestedResults.Count > 0) {
      Console.WriteLine($"\nNested archives found: {result.NestedResults.Count}");
      foreach (var nested in result.NestedResults) {
        Console.WriteLine($"  {nested.EntryName} -> {nested.Result.FormatName} ({nested.Result.Entries.Count} entries)");
        var nestedDir = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(nested.EntryName) + "_extracted");
        Directory.CreateDirectory(nestedDir);
        foreach (var entry in nested.Result.Entries) {
          if (entry.IsDirectory) continue;
          var outPath = Path.Combine(nestedDir, entry.Name.Replace('/', Path.DirectorySeparatorChar));
          var dir = Path.GetDirectoryName(outPath);
          if (dir != null) Directory.CreateDirectory(dir);
          File.WriteAllBytes(outPath, entry.Data);
        }
      }
    }
  }

  return 0;
});

// ── batch ────────────────────────────────────────────────────────────

var batchDirArg = new Argument<DirectoryInfo>("directory") { Description = "Directory to analyze" };
var batchRecursiveOpt = new Option<bool>("--recursive") { Description = "Recurse into subdirectories" };

var batchCmd = new Command("batch", """
  Analyze all files in a directory, detecting formats and showing statistics.
  Example: cwb batch /path/to/files --recursive
  """) { batchDirArg, batchRecursiveOpt };
batchCmd.SetAction((ParseResult ctx) => {
  var dir = ctx.GetValue(batchDirArg)!;
  if (!dir.Exists) { Console.Error.WriteLine($"Directory not found: {dir.FullName}"); return 1; }

  var analyzer = new Compression.Analysis.BatchAnalyzer();
  var result = analyzer.AnalyzeDirectory(dir.FullName, ctx.GetValue(batchRecursiveOpt));

  Console.WriteLine($"Directory: {dir.FullName}");
  Console.WriteLine($"Total files: {result.TotalFiles}");
  Console.WriteLine($"Total size: {FormatSize(result.TotalSize)}");
  Console.WriteLine($"Unknown files: {result.UnknownFiles.Count}");
  Console.WriteLine();

  if (result.FormatDistribution.Count > 0) {
    Console.WriteLine("── Format Distribution ──");
    foreach (var (fmt, count) in result.FormatDistribution.OrderByDescending(kv => kv.Value))
      Console.WriteLine($"  {fmt,-20} {count,5} files");
    Console.WriteLine();
  }

  if (result.UnknownFiles.Count > 0 && result.UnknownFiles.Count <= 20) {
    Console.WriteLine("── Unknown Files ──");
    foreach (var f in result.UnknownFiles)
      Console.WriteLine($"  {f}");
    Console.WriteLine();
  }

  return 0;
});

// ── suggest ──────────────────────────────────────────────────────────

var suggestFilesArg = new Argument<string[]>("files") { Description = "Files or directories to package" };
var suggestPlatformOpt = new Option<string>("--platform") { Description = "Target platform: any, windows, linux, macos, cross", DefaultValueFactory = _ => "any" };

var suggestCmd = new Command("suggest", """
  Suggest the best archive format for the given files.
  Example: cwb suggest Documents/ --platform linux
  """) { suggestFilesArg, suggestPlatformOpt };
suggestCmd.SetAction((ParseResult ctx) => {
  var files = ctx.GetValue(suggestFilesArg)!;
  var platformStr = ctx.GetValue(suggestPlatformOpt) ?? "any";
  var platform = platformStr.ToLowerInvariant() switch {
    "windows" or "win" => Compression.Analysis.FormatSuggester.Platform.Windows,
    "linux" or "unix" => Compression.Analysis.FormatSuggester.Platform.Linux,
    "macos" or "mac" => Compression.Analysis.FormatSuggester.Platform.MacOS,
    "cross" or "crossplatform" => Compression.Analysis.FormatSuggester.Platform.CrossPlatform,
    _ => Compression.Analysis.FormatSuggester.Platform.Any,
  };

  var suggester = new Compression.Analysis.FormatSuggester();
  var suggestions = suggester.Suggest(files, platform);

  Console.WriteLine($"Suggestions for {files.Length} input(s), platform: {platformStr}");
  Console.WriteLine();

  foreach (var s in suggestions)
    Console.WriteLine($"  {s.Score,3}  {s.DisplayName,-12} ({s.Extension})  {s.Rationale}");

  return 0;
});

// ── root ─────────────────────────────────────────────────────────────

// ── tool ─────────────────────────────────────────────────────────────

var toolConfigOpt = new Option<string?>("--config", "-c") { Description = "Path to tool templates JSON file (default: ~/.cwb-tools.json)" };
var defaultToolConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cwb-tools.json");

var toolListCmd = new Command("list", "List all registered tool templates") { toolConfigOpt };
toolListCmd.SetAction((ParseResult ctx) => {
  var configPath = ctx.GetValue(toolConfigOpt) ?? defaultToolConfig;
  var registry = Compression.Analysis.ExternalTools.ToolTemplateRegistry.CreateDefaults();
  registry.Load(configPath);
  Console.WriteLine($"{"Name",-25} {"Action",-12} {"Executable",-15} {"Extensions",-20} {"Description"}");
  Console.WriteLine(new string('-', 100));
  foreach (var t in registry.Templates) {
    var exts = t.Extensions.Count > 0 ? string.Join(",", t.Extensions) : "(any)";
    Console.WriteLine($"{t.Name,-25} {t.Action,-12} {t.Executable,-15} {exts,-20} {t.Description}");
  }
  return 0;
});

var toolAddName = new Argument<string>("name") { Description = "Template name" };
var toolAddExe = new Argument<string>("executable") { Description = "Executable name or path" };
var toolAddArgs = new Argument<string>("arguments") { Description = "Argument template with {input}, {output}, {outputDir} placeholders" };
var toolAddAction = new Option<string>("--action", "-a") { Description = "Action type: extract, decompress, identify, list, compress", DefaultValueFactory = _ => "extract" };
var toolAddExts = new Option<string[]>("--ext") { Description = "File extensions this template handles" };
var toolAddStdout = new Option<bool>("--stdout") { Description = "Capture stdout as decompressed output" };
var toolAddStdin = new Option<bool>("--stdin") { Description = "Pipe input via stdin" };
var toolAddTimeout = new Option<int>("--timeout") { Description = "Timeout in milliseconds (0 = default)" };
var toolAddDesc = new Option<string?>("--desc") { Description = "Description" };

var toolAddCmd = new Command("add", """
  Add a custom tool template. Use placeholders in arguments:
    {input}    — input file path
    {output}   — output file path
    {outputDir} — output directory

  Examples:
    cwb tool add my-zstd zstd "-d -c {input}" --action decompress --ext .zst --stdout
    cwb tool add my-7z 7z "x {input} -o{outputDir} -y" --ext .rar .zip .7z
    cwb tool add upx upx "-d {input} -o{output}" --action decompress --ext .exe
  """) { toolAddName, toolAddExe, toolAddArgs, toolAddAction, toolAddExts, toolAddStdout, toolAddStdin, toolAddTimeout, toolAddDesc, toolConfigOpt };
toolAddCmd.SetAction((ParseResult ctx) => {
  var configPath = ctx.GetValue(toolConfigOpt) ?? defaultToolConfig;
  var registry = Compression.Analysis.ExternalTools.ToolTemplateRegistry.CreateDefaults();
  registry.Load(configPath);
  var template = new Compression.Analysis.ExternalTools.ToolTemplate {
    Name = ctx.GetValue(toolAddName)!,
    Executable = ctx.GetValue(toolAddExe)!,
    Arguments = ctx.GetValue(toolAddArgs)!,
    Action = ctx.GetValue(toolAddAction) ?? "extract",
    Extensions = [.. (ctx.GetValue(toolAddExts) ?? [])],
    CaptureStdout = ctx.GetValue(toolAddStdout),
    PipeStdin = ctx.GetValue(toolAddStdin),
    TimeoutMs = ctx.GetValue(toolAddTimeout),
    Description = ctx.GetValue(toolAddDesc) ?? ""
  };
  registry.Register(template);
  registry.Save(configPath);
  Console.WriteLine($"Added template '{template.Name}': {template.Executable} {template.Arguments}");
  Console.WriteLine($"Saved to {configPath}");
  return 0;
});

var toolRemoveName = new Argument<string>("name") { Description = "Template name to remove" };
var toolRemoveCmd = new Command("remove", "Remove a tool template") { toolRemoveName, toolConfigOpt };
toolRemoveCmd.SetAction((ParseResult ctx) => {
  var configPath = ctx.GetValue(toolConfigOpt) ?? defaultToolConfig;
  var registry = Compression.Analysis.ExternalTools.ToolTemplateRegistry.CreateDefaults();
  registry.Load(configPath);
  var name = ctx.GetValue(toolRemoveName)!;
  if (registry.Remove(name)) {
    registry.Save(configPath);
    Console.WriteLine($"Removed template '{name}'");
  } else {
    Console.Error.WriteLine($"Template '{name}' not found");
    return 1;
  }
  return 0;
});

var toolRunFile = new Argument<FileInfo>("file") { Description = "File to process" };
var toolRunName = new Option<string?>("--template", "-t") { Description = "Run a specific template by name (otherwise tries all matching)" };
var toolRunAction2 = new Option<string>("--action", "-a") { Description = "Action type to match", DefaultValueFactory = _ => "extract" };
var toolRunOutput = new Option<DirectoryInfo?>("--output", "-o") { Description = "Output directory" };
var toolRunCmd = new Command("run", "Run a tool template against a file") { toolRunFile, toolRunName, toolRunAction2, toolRunOutput, toolConfigOpt };
toolRunCmd.SetAction(async (ParseResult ctx) => {
  var configPath = ctx.GetValue(toolConfigOpt) ?? defaultToolConfig;
  var registry = Compression.Analysis.ExternalTools.ToolTemplateRegistry.CreateDefaults();
  registry.Load(configPath);
  var file = ctx.GetValue(toolRunFile)!;
  if (!file.Exists) { Console.Error.WriteLine($"File not found: {file.FullName}"); return 1; }
  var outDir = ctx.GetValue(toolRunOutput)?.FullName ?? Path.Combine(Path.GetDirectoryName(file.FullName)!, Path.GetFileNameWithoutExtension(file.Name) + "_out");

  var templateName = ctx.GetValue(toolRunName);
  if (templateName != null) {
    var template = registry.GetByName(templateName);
    if (template == null) { Console.Error.WriteLine($"Template '{templateName}' not found"); return 1; }
    Directory.CreateDirectory(outDir);
    var result = await template.RunAsync(file.FullName, outputDir: outDir);
    Console.WriteLine($"[{template.Name}] exit={result.ExitCode} {(result.Success ? "OK" : "FAILED")}");
    if (!string.IsNullOrWhiteSpace(result.Stdout)) Console.WriteLine(result.Stdout);
    if (!string.IsNullOrWhiteSpace(result.Stderr)) Console.Error.WriteLine(result.Stderr);
    return result.Success ? 0 : 1;
  }

  // Auto-match by extension + action.
  var action = ctx.GetValue(toolRunAction2) ?? "extract";
  Directory.CreateDirectory(outDir);
  var (matched, matchResult) = await registry.TryMatchingAsync(file.FullName, action, outDir);
  if (matched != null && matchResult != null) {
    Console.WriteLine($"[{matched.Name}] exit={matchResult.ExitCode} {(matchResult.Success ? "OK" : "FAILED")}");
    if (!string.IsNullOrWhiteSpace(matchResult.Stdout)) Console.WriteLine(matchResult.Stdout);
    return matchResult.Success ? 0 : 1;
  }
  Console.Error.WriteLine($"No matching template for '{file.Name}' with action '{action}'");
  return 1;
});

var toolInitCmd = new Command("init", "Create default tool templates config file") { toolConfigOpt };
toolInitCmd.SetAction((ParseResult ctx) => {
  var configPath = ctx.GetValue(toolConfigOpt) ?? defaultToolConfig;
  var registry = Compression.Analysis.ExternalTools.ToolTemplateRegistry.CreateDefaults();
  registry.Save(configPath);
  Console.WriteLine($"Default templates saved to {configPath}");
  Console.WriteLine($"{registry.Templates.Count} templates configured. Edit the JSON to customize.");
  return 0;
});

var toolCmd = new Command("tool", """
  Manage and run configurable external tool templates.
  Templates map CLI commands to actions (extract, decompress, identify, list).

  Quick start:
    cwb tool init                             Create default config
    cwb tool list                             Show all templates
    cwb tool add my-tool upx "-d {input}"     Add custom template
    cwb tool run archive.rar                  Run matching template
    cwb tool run file.bin -t binwalk-scan     Run specific template
  """) { toolListCmd, toolAddCmd, toolRemoveCmd, toolRunCmd, toolInitCmd };

// ── reverse-engineer ────────────────────────────────────────────────

var revExe = new Argument<string>("executable") { Description = "Tool executable (name or path)" };
var revArgs = new Argument<string>("arguments") { Description = "Argument template: use {input} for input file, {output} for output file" };
var revTimeout = new Option<int>("--timeout") { Description = "Timeout per probe in ms", DefaultValueFactory = _ => 30000 };

var reverseCmd = new Command("reverse-engineer", """
  Reverse-engineer an unknown tool's output format.
  Runs the tool with ~40 controlled probe inputs, then analyzes the outputs
  to discover: magic bytes, header structure, size fields, compression algorithm,
  filename storage, and more.

  The argument template must include {input} and {output} placeholders.

  Examples:
    cwb reverse-engineer MyTool.exe "{input} {output}"
    cwb reverse-engineer packer.exe "--pack {input} --out {output}"
    cwb reverse-engineer compress.exe "-c -i {input} -o {output}" --timeout 10000
  """) { revExe, revArgs, revTimeout };
reverseCmd.Aliases.Add("reveng");
reverseCmd.SetAction(async (ParseResult ctx) => {
  FormatRegistration.EnsureInitialized();
  var exe = ctx.GetValue(revExe)!;
  var args = ctx.GetValue(revArgs)!;
  var timeout = ctx.GetValue(revTimeout);

  Console.WriteLine($"Reverse-engineering: {exe} {args}");
  Console.WriteLine();

  var reverser = new Compression.Analysis.ReverseEngineering.FormatReverser(exe, args, timeout);
  var report = await reverser.AnalyzeAsync((name, current, total) => {
    Console.Write($"\r  Probe {current}/{total}: {name,-30}");
  });

  Console.WriteLine();
  Console.WriteLine();
  Console.WriteLine(report.Summary);
  Console.WriteLine();
  Console.WriteLine($"Probes: {report.ProbesSucceeded}/{report.ProbesRun} succeeded");

  return report.ProbesSucceeded > 0 ? 0 : 1;
});

// ── carve ────────────────────────────────────────────────────────────

var carveFileArg = new Argument<FileInfo>("file") { Description = "Binary file to scan for embedded payloads" };
var carveOutOpt = new Option<DirectoryInfo?>("--out", "-o") { Description = "Directory to write carved payloads (default: don't extract, just list)" };
var carveMinConfOpt = new Option<double>("--min-confidence") { Description = "Minimum signature confidence (0.0–1.0)", DefaultValueFactory = _ => 0.7 };
var carveFormatFilterOpt = new Option<string[]>("--format") { Description = "Limit to specific format IDs (comma-separated or repeated)" };

var carveCmd = new Command("carve", """
  Find embedded file payloads (ZIP, PNG, MP4, GZIP, …) anywhere inside an arbitrary
  binary — executable, firmware image, disk sector dump, corrupted file. Reports
  each hit's offset + length + format, and optionally extracts it to a directory.

  Examples:
    cwb carve firmware.bin                               List embedded payloads
    cwb carve virus.exe --out extracted/                  Extract everything found
    cwb carve disk.img --format Zip,Gzip --min-confidence 0.9
  """) { carveFileArg, carveOutOpt, carveMinConfOpt, carveFormatFilterOpt };

carveCmd.SetAction((ParseResult ctx) => {
  var file = ctx.GetValue(carveFileArg)!;
  var outDir = ctx.GetValue(carveOutOpt);
  var minConf = ctx.GetValue(carveMinConfOpt);
  var formats = ctx.GetValue(carveFormatFilterOpt);

  if (!file.Exists) { Console.Error.WriteLine($"File not found: {file.FullName}"); return 1; }

  var buffer = File.ReadAllBytes(file.FullName);
  var options = new Compression.Analysis.PayloadCarver.CarveOptions(
    MinConfidence: minConf,
    FormatFilter: formats is { Length: > 0 } ? formats : null,
    IncludeData: outDir != null);
  var results = Compression.Analysis.PayloadCarver.Carve(buffer, options);

  Console.WriteLine($"Scanned {FormatSize(buffer.Length)}; {results.Count} payload(s) discovered.");
  Console.WriteLine();
  Console.WriteLine($"{"Offset",12} {"Length",12} {"Conf",6} {"Format",-20}");
  Console.WriteLine(new string('-', 60));
  foreach (var p in results) {
    Console.WriteLine($"0x{p.Offset:X10} {FormatSize(p.Length),12} {p.Confidence,6:F2} {p.FormatId,-20}");
  }

  if (outDir != null && results.Count > 0) {
    Console.WriteLine();
    var written = Compression.Analysis.PayloadCarver.Extract(results, outDir.FullName);
    Console.WriteLine($"Wrote {written.Count} file(s) to {outDir.FullName}");
  }
  return 0;
});

// ── visualize ────────────────────────────────────────────────────────

var vizFileArg = new Argument<FileInfo>("input") { Description = "Binary / disk-image to visualize" };
var vizOutOpt = new Option<FileInfo?>("--out") { Description = "Output file path (required for svg/html)" };
var vizFormatOpt = new Option<string>("--format") { Description = "Output format: ascii | svg | html", DefaultValueFactory = _ => "ascii" };
var vizBlockSizeOpt = new Option<int>("--block-size") { Description = "Block size in bytes (default 4096)", DefaultValueFactory = _ => 4096 };
var vizMaxDepthOpt = new Option<int>("--max-depth") { Description = "Maximum recursion depth", DefaultValueFactory = _ => 5 };

var visualizeCmd = new Command("visualize", """
  Render a colored block-view of how filesystems and containers are stacked
  inside a binary. Runs RecursiveFilesystemCarver, builds a BlockMap, then
  renders ASCII (stdout), SVG, or HTML.

  Examples:
    cwb visualize disk.img                                    ASCII strip on stdout
    cwb visualize disk.img --format svg --out map.svg          Per-layer SVG
    cwb visualize triple-nested.img --format html --out map.html
  """) { vizFileArg, vizOutOpt, vizFormatOpt, vizBlockSizeOpt, vizMaxDepthOpt };

visualizeCmd.SetAction((ParseResult ctx) => {
  var file = ctx.GetValue(vizFileArg)!;
  var outFile = ctx.GetValue(vizOutOpt);
  var fmt = (ctx.GetValue(vizFormatOpt) ?? "ascii").ToLowerInvariant();
  var blockSize = ctx.GetValue(vizBlockSizeOpt);
  var maxDepth = ctx.GetValue(vizMaxDepthOpt);

  if (!file.Exists) { Console.Error.WriteLine($"File not found: {file.FullName}"); return 1; }
  if (fmt is not ("ascii" or "svg" or "html")) {
    Console.Error.WriteLine($"Unknown format '{fmt}' — expected ascii | svg | html.");
    return 2;
  }
  if (fmt is "svg" or "html" && outFile is null) {
    Console.Error.WriteLine($"--out is required for --format {fmt}.");
    return 2;
  }

  Console.Error.WriteLine($"Carving {file.Name} ({FormatSize(file.Length)})…");

  IReadOnlyList<Compression.Analysis.NestedHit> hits;
  using (var fs = File.OpenRead(file.FullName)) {
    var carver = new Compression.Analysis.RecursiveFilesystemCarver { MaxDepth = maxDepth };
    hits = carver.CarveStream(fs);
  }
  Console.Error.WriteLine($"Found {hits.Count} top-level hit(s).");

  var map = new Compression.Analysis.BlockMap(file.Length, blockSize);
  map.MarkRecursive(hits);

  switch (fmt) {
    case "ascii":
      Console.WriteLine(Compression.Analysis.BlockMapRenderer.RenderAscii(map));
      if (map.MaxDepth > 1) {
        Console.WriteLine();
        Console.WriteLine(Compression.Analysis.BlockMapRenderer.RenderAsciiLayered(map));
      }
      break;
    case "svg":
      File.WriteAllText(outFile!.FullName, Compression.Analysis.BlockMapRenderer.RenderSvg(map));
      Console.Error.WriteLine($"Wrote SVG to {outFile.FullName}");
      break;
    case "html":
      File.WriteAllText(outFile!.FullName, Compression.Analysis.BlockMapRenderer.RenderHtml(map, hits));
      Console.Error.WriteLine($"Wrote HTML to {outFile.FullName}");
      break;
  }
  return 0;
});

// ── defragment ────────────────────────────────────────────────────────

var defragImageArg = new Argument<string>("image") { Description = "Filesystem image (or glob pattern / directory) to defragment in place" };
var defragModeOpt = new Option<string>("--mode") {
  Description = "Layout strategy: pack-start (default), pack-end, fill-holes, carve-hole",
  DefaultValueFactory = _ => "pack-start",
};
var defragHoleSizeOpt = new Option<long>("--hole-size") {
  Description = "For --mode carve-hole: bytes to reserve as a contiguous free region",
  DefaultValueFactory = _ => 0L,
};
var defragHoleAtOpt = new Option<long>("--hole-at") {
  Description = "For --mode carve-hole: byte offset of the carved region (-1 = auto, at end)",
  DefaultValueFactory = _ => -1L,
};
var defragStrideOpt = new Option<int>("--stride") {
  Description = "Block interleave factor (1 = contiguous, 2+ = interleaved). Range 1-256",
  DefaultValueFactory = _ => 1,
};
var defragBatchOpt = new Option<bool>("--batch") {
  Description = "Treat argument as a glob pattern; defragment all matching files",
};
var defragRecursiveOpt = new Option<bool>("--recursive") {
  Description = "When argument is a directory, recurse into subdirectories",
};
var defragCmd = new Command("defragment", """
  Defragment a filesystem image in place using one of four layout strategies:

    pack-start    Pack live extents at the data origin; trailing free space.
                  The default; closest match to a traditional defrag tool.
    pack-end      Pack live extents at the end of the image; leading free
                  space. Useful before injecting boot sectors or installer
                  payloads at low offsets.
    fill-holes    Lazy compaction — best-fit fill of existing holes from tail
                  extents. Doesn't guarantee contiguity but moves the minimum
                  number of bytes. Use on huge images with a few small holes.
    carve-hole    Reserve a contiguous free region of --hole-size bytes at
                  --hole-at (or auto-pick at the end). Live extents in the
                  way are relocated to existing free space or appended.

  Batch mode:
    cwb defragment *.img --mode pack-start --stride 2
    cwb defragment images/ --mode pack-end --recursive

  Only descriptors that implement IArchiveDefragmentable accept this command.
  Currently: FAT12 / FAT16 / FAT32 (all four modes), other R/W filesystems
  (pack-start only via the default-impl fallback).
  """) {
  defragImageArg, defragModeOpt, defragHoleSizeOpt, defragHoleAtOpt, defragStrideOpt, defragBatchOpt, defragRecursiveOpt
};
// Canonical short verb name from the taxonomy in docs/ARCHIVE-MODEL.md.
defragCmd.Aliases.Add("defrag");
defragCmd.SetAction((ParseResult ctx) => {
  var imageArg = ctx.GetValue(defragImageArg)!;
  var modeStr = ctx.GetValue(defragModeOpt) ?? "pack-start";
  var holeSize = ctx.GetValue(defragHoleSizeOpt);
  var holeAt = ctx.GetValue(defragHoleAtOpt);
  var stride = ctx.GetValue(defragStrideOpt);
  var isBatch = ctx.GetValue(defragBatchOpt);
  var isRecursive = ctx.GetValue(defragRecursiveOpt);

  var mode = modeStr.ToLowerInvariant() switch {
    "pack-start" => DefragMode.ConsolidateAtStart,
    "pack-end" => DefragMode.ConsolidateAtEnd,
    "fill-holes" => DefragMode.FillHolesLazy,
    "carve-hole" => DefragMode.CarveHole,
    _ => throw new ArgumentException($"Unknown mode '{modeStr}'. Use one of: pack-start, pack-end, fill-holes, carve-hole."),
  };

  // Resolve file list: single file, glob, or directory
  var files = ResolveDefragTargets(imageArg, isBatch, isRecursive);
  if (files.Count == 0) {
    Console.Error.WriteLine($"No files matched: {imageArg}");
    return 1;
  }

  var totalOk = 0;
  var totalFail = 0;
  foreach (var file in files) {
    try {
      var format = FormatDetector.Detect(file);
      var ops = FormatRegistry.GetById(format.ToString());
      if (ops is not IArchiveDefragmentable defragmentable) {
        if (files.Count > 1) {
          Console.Error.WriteLine($"  SKIP {Path.GetFileName(file)}: {format} does not support defragmentation.");
          totalFail++;
          continue;
        }
        Console.Error.WriteLine($"{format} does not support defragmentation.");
        return 1;
      }

      Console.Write($"Defragmenting {Path.GetFileName(file)} ({format}, mode={modeStr})...");
      var sw = Stopwatch.StartNew();
      using var stream = File.Open(file, FileMode.Open, FileAccess.ReadWrite);
      defragmentable.Defragment(stream, new DefragOptions {
        Mode = mode,
        HoleSize = holeSize,
        HoleAt = holeAt,
        InterleaveStride = Math.Clamp(stride, 1, 256),
      });
      sw.Stop();
      Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
      totalOk++;
    } catch (Exception ex) {
      Console.Error.WriteLine($" FAILED: {ex.Message}");
      totalFail++;
    }
  }

  if (files.Count > 1) {
    Console.WriteLine($"Batch complete: {totalOk} succeeded, {totalFail} failed out of {files.Count} file(s).");
  }

  return totalFail > 0 && totalOk == 0 ? 1 : 0;
});

// ── shrink ────────────────────────────────────────────────────────────

var shrinkImageArg = new Argument<string>("image") { Description = "Filesystem image or VHD to shrink" };
var shrinkCompactOpt = new Option<bool>("--compact") { Description = "Also compact the container (remove all-zero blocks from dynamic VHD)" };

var shrinkCmd = new Command("shrink", """
  Defragment and truncate a filesystem image to remove trailing free space.
  For FAT images: defragments, finds last used cluster, truncates, updates BPB.
  For ext images: defragments, finds last used block, truncates, updates superblock.
  For VHD with --compact: also scans for all-zero blocks and rebuilds as sparse.

  Examples:
    cwb shrink disk.img                    Defrag + truncate FAT or ext image
    cwb shrink disk.vhd --compact          Also compact container (VHD sparse)
  """) { shrinkImageArg, shrinkCompactOpt };
shrinkCmd.SetAction((ParseResult ctx) => {
  var imageArg = ctx.GetValue(shrinkImageArg)!;
  var compact = ctx.GetValue(shrinkCompactOpt);

  if (!File.Exists(imageArg)) { Console.Error.WriteLine($"File not found: {imageArg}"); return 1; }

  FormatRegistration.EnsureInitialized();
  var format = FormatDetector.Detect(imageArg);
  var formatId = format.ToString();

  Console.Write($"Shrinking {Path.GetFileName(imageArg)} ({formatId})...");
  var sw = Stopwatch.StartNew();
  var origSize = new FileInfo(imageArg).Length;

  try {
    if (formatId == "Fat") {
      using var stream = File.Open(imageArg, FileMode.Open, FileAccess.ReadWrite);
      var result = FileSystem.Fat.FatShrinkHelper.Shrink(stream);
      sw.Stop();
      Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
      Console.WriteLine($"  {FormatSize(result.OriginalSize)} -> {FormatSize(result.NewSize)} ({(result.WasReduced ? "reduced" : "no change")})");
    } else if (formatId is "Ext" or "Ext1") {
      using var stream = File.Open(imageArg, FileMode.Open, FileAccess.ReadWrite);
      var result = FileSystem.Ext.ExtShrinkHelper.Shrink(stream);
      sw.Stop();
      Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
      Console.WriteLine($"  {FormatSize(result.OriginalSize)} -> {FormatSize(result.NewSize)} ({(result.WasReduced ? "reduced" : "no change")})");
    } else if (formatId == "Vhd" && compact) {
      using var stream = File.Open(imageArg, FileMode.Open, FileAccess.ReadWrite);
      var result = FileFormat.Vhd.VhdCompactor.Compact(stream);
      sw.Stop();
      Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
      Console.WriteLine($"  {FormatSize(result.OriginalSize)} -> {FormatSize(result.NewSize)} ({result.BlocksFreed} blocks freed)");
    } else if (formatId == "Vhd") {
      // Without --compact, defragment the inner FS via the VHD descriptor
      var desc = FormatRegistry.GetById("Vhd");
      if (desc is IArchiveDefragmentable defrag) {
        using var stream = File.Open(imageArg, FileMode.Open, FileAccess.ReadWrite);
        defrag.Defragment(stream);
        sw.Stop();
        Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
        Console.WriteLine($"  Inner FS defragmented. Use --compact to also remove sparse blocks.");
      } else {
        sw.Stop();
        Console.WriteLine(" skipped");
        Console.Error.WriteLine($"  VHD descriptor does not support defragmentation.");
        return 1;
      }
    } else {
      sw.Stop();
      Console.WriteLine(" skipped");
      Console.Error.WriteLine($"  Format {formatId} does not support shrink. Supported: Fat, Ext, Vhd.");
      return 1;
    }
  } catch (Exception ex) {
    sw.Stop();
    Console.Error.WriteLine($" FAILED: {ex.Message}");
    return 1;
  }

  return 0;
});

// ── wipe-empty ────────────────────────────────────────────────────────

var wipeImageArg = new Argument<string>("image") { Description = "Filesystem image or archive to wipe unused space from" };
var wipeNoClusterTipsOpt = new Option<bool>("--no-cluster-tips") {
  Description = "Skip cluster-tip wiping (only zero free clusters / gaps)",
};
var wipeNoDeletedOpt = new Option<bool>("--no-deleted-entries") {
  Description = "Skip wiping of deleted directory entries",
};

var wipeCmd = new Command("wipe-empty", """
  Zero-fill all unused space in a filesystem image or archive. Ensures no
  deleted file remnants, cluster-tip slack, or padding bytes survive.

  What gets wiped:
    - Free clusters / sectors not allocated to any file
    - Cluster-tip slack (file < cluster → trailing bytes zeroed)
    - Dead bytes in archives (orphan data after file removal, padding)
    - Gaps between archive entries

  Examples:
    cwb wipe-empty disk.img                    Zero all unused space
    cwb wipe-empty disk.img --no-cluster-tips  Skip cluster-tip wiping
    cwb wipe-empty archive.zip                 Zero dead bytes in archive

  Works with any format that implements IWipeEmpty (FAT, ZIP, and others).
  For formats without a dedicated implementation but with an extent/layout
  map, the generic wiper zeros all gaps between live extents.
  """) { wipeImageArg, wipeNoClusterTipsOpt, wipeNoDeletedOpt };
// Canonical maintenance-verb name (the taxonomy in docs/ARCHIVE-MODEL.md calls
// this verb "wipe"); "wipe-empty" stays as the primary descriptive name.
wipeCmd.Aliases.Add("wipe");
wipeCmd.SetAction((ParseResult ctx) => {
  var imageArg = ctx.GetValue(wipeImageArg)!;
  var noClusterTips = ctx.GetValue(wipeNoClusterTipsOpt);
  var noDeleted = ctx.GetValue(wipeNoDeletedOpt);

  if (!File.Exists(imageArg)) { Console.Error.WriteLine($"File not found: {imageArg}"); return 1; }

  FormatRegistration.EnsureInitialized();
  var format = FormatDetector.Detect(imageArg);
  var formatId = format.ToString();
  var descriptor = FormatRegistry.GetById(formatId);

  Console.Write($"Wiping unused space in {Path.GetFileName(imageArg)} ({formatId})...");
  var sw = Stopwatch.StartNew();
  var origSize = new FileInfo(imageArg).Length;

  try {
    long wiped;
    var totalUnused = -1L;
    if (descriptor is IWipeEmpty wiper) {
      using var stream = File.Open(imageArg, FileMode.Open, FileAccess.ReadWrite);
      // Report total unused alongside bytes written when the descriptor
      // also exposes an extent/layout map — otherwise a mostly-empty image
      // looks "mostly used" because the wiper skips already-zero chunks.
      if (descriptor is IFilesystemExtentMap fsMap) {
        stream.Position = 0;
        totalUnused = UnusedSpaceWiper.ComputeUnusedBytes(fsMap.EnumerateExtents(stream), stream.Length);
      } else if (descriptor is IArchiveLayoutMap arMap) {
        stream.Position = 0;
        totalUnused = UnusedSpaceWiper.ComputeUnusedBytes(arMap.EnumerateLayout(stream), stream.Length);
      }
      stream.Position = 0;
      wiped = wiper.WipeUnusedSpace(stream, wipeClusterTips: !noClusterTips, wipeDeletedEntries: !noDeleted);
    } else if (descriptor is IFilesystemExtentMap extentMap) {
      using var stream = File.Open(imageArg, FileMode.Open, FileAccess.ReadWrite);
      stream.Position = 0;
      var extents = extentMap.EnumerateExtents(stream).ToList();
      totalUnused = UnusedSpaceWiper.ComputeUnusedBytes(extents, stream.Length);
      wiped = UnusedSpaceWiper.Wipe(stream, extents, stream.Length, wipeClusterTips: !noClusterTips);
    } else if (descriptor is IArchiveLayoutMap layoutMap) {
      using var stream = File.Open(imageArg, FileMode.Open, FileAccess.ReadWrite);
      stream.Position = 0;
      var extents = layoutMap.EnumerateLayout(stream).ToList();
      totalUnused = UnusedSpaceWiper.ComputeUnusedBytes(extents, stream.Length);
      wiped = UnusedSpaceWiper.Wipe(stream, extents, stream.Length, wipeClusterTips: false);
    } else {
      sw.Stop();
      Console.WriteLine(" skipped");
      Console.Error.WriteLine($"  Format {formatId} does not support wipe-empty (no extent/layout map).");
      return 1;
    }

    sw.Stop();
    Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
    if (totalUnused >= 0) {
      var unusedPct = origSize > 0 ? 100.0 * totalUnused / origSize : 0;
      var alreadyZero = Math.Max(0, totalUnused - wiped);
      Console.WriteLine($"  Unused space: {FormatSize(totalUnused)} ({unusedPct:F1}% of image)");
      Console.WriteLine($"  Newly zeroed: {FormatSize(wiped)} ({wiped:N0} bytes); {FormatSize(alreadyZero)} was already zero");
    } else {
      var pct = origSize > 0 ? 100.0 * wiped / origSize : 0;
      Console.WriteLine($"  Wiped {FormatSize(wiped)} ({wiped:N0} bytes, {pct:F1}% of image)");
    }
  } catch (Exception ex) {
    sw.Stop();
    Console.Error.WriteLine($" FAILED: {ex.Message}");
    return 1;
  }

  return 0;
});

// ── deploy ──────────────────────────────────────────────────────────────

var deployImageArg = new Argument<FileInfo>("image") { Description = "Source image file to write" };
var deployDeviceArg = new Argument<string>("device") { Description = @"Target block device (\\.\PhysicalDriveN on Windows, /dev/sdX on Linux)" };
var deployYesOpt = new Option<bool>("--yes", "-y") { Description = "Skip interactive confirmation" };
var deployVerifyOpt = new Option<bool>("--verify") { Description = "Read back written data and verify CRC-32 against source" };

var deployCmd = new Command("deploy", """
  Raw-write an image file to a block device.

  *** WARNING: THIS WILL DESTROY ALL DATA ON THE TARGET DEVICE! ***

  Safety guards:
    - Refuses to write to system drives (C:\ on Windows, / or /boot on Linux)
    - Shows source size + target info before writing
    - Requires --yes flag or interactive confirmation
    - Computes CRC-32 of written bytes for verification
    - Optional --verify flag reads back the written data to double-check

  Examples:
    cwb deploy disk.img \\.\PhysicalDrive2 --yes
    cwb deploy disk.img /dev/sdb --yes --verify
  """) {
  deployImageArg, deployDeviceArg, deployYesOpt, deployVerifyOpt
};
deployCmd.SetAction((ParseResult ctx) => {
  var image = ctx.GetValue(deployImageArg)!;
  var device = ctx.GetValue(deployDeviceArg)!;
  var autoConfirm = ctx.GetValue(deployYesOpt);
  var verify = ctx.GetValue(deployVerifyOpt);

  if (!image.Exists) {
    Console.Error.WriteLine($"Image file not found: {image.FullName}");
    return 1;
  }

  // Safety: refuse system drives
  if (IsSystemDrive(device)) {
    Console.Error.WriteLine($"REFUSED: '{device}' appears to be a system drive. Aborting.");
    return 1;
  }

  var imageSize = image.Length;
  Console.WriteLine($"Source:  {image.FullName} ({FormatSize(imageSize)})");
  Console.WriteLine($"Target:  {device}");
  Console.WriteLine();
  Console.WriteLine("*** WARNING: ALL DATA ON THE TARGET DEVICE WILL BE DESTROYED! ***");
  Console.WriteLine();

  if (!autoConfirm) {
    Console.Write("Type 'yes' to continue: ");
    var response = Console.ReadLine()?.Trim();
    if (!string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase)) {
      Console.WriteLine("Aborted.");
      return 1;
    }
  }

  const int ChunkSize = 64 * 1024; // 64 KB chunks
  var buffer = new byte[ChunkSize];
  var writeCrc = new Compression.Core.Checksums.Crc32();

  try {
    using var src = File.OpenRead(image.FullName);
    using var dst = new FileStream(device, FileMode.Open, FileAccess.Write, FileShare.None);

    var written = 0L;
    var sw = Stopwatch.StartNew();
    int bytesRead;
    while ((bytesRead = src.Read(buffer, 0, ChunkSize)) > 0) {
      dst.Write(buffer, 0, bytesRead);
      writeCrc.Update(buffer.AsSpan(0, bytesRead));
      written += bytesRead;

      if (sw.ElapsedMilliseconds > 500 || written == imageSize) {
        var elapsed = sw.Elapsed.TotalSeconds;
        var mbps = elapsed > 0 ? written / (1024.0 * 1024) / elapsed : 0;
        var pct = imageSize > 0 ? 100.0 * written / imageSize : 100;
        Console.Write($"\rWriting: {FormatSize(written)} / {FormatSize(imageSize)} ({pct:F1}%) {mbps:F1} MB/s  ");
      }
    }
    dst.Flush();
    sw.Stop();

    var totalMbps = sw.Elapsed.TotalSeconds > 0 ? written / (1024.0 * 1024) / sw.Elapsed.TotalSeconds : 0;
    Console.WriteLine();
    Console.WriteLine($"Write complete: {FormatSize(written)} in {sw.Elapsed.TotalSeconds:F1}s ({totalMbps:F1} MB/s)");
    Console.WriteLine($"Write CRC-32:  0x{writeCrc.Value:X8}");

    // Source CRC for comparison
    src.Position = 0;
    var srcCrc = new Compression.Core.Checksums.Crc32();
    while ((bytesRead = src.Read(buffer, 0, ChunkSize)) > 0)
      srcCrc.Update(buffer.AsSpan(0, bytesRead));
    Console.WriteLine($"Source CRC-32: 0x{srcCrc.Value:X8}");

    if (writeCrc.Value != srcCrc.Value) {
      Console.Error.WriteLine("*** CRC-32 MISMATCH — write may be corrupted! ***");
      return 1;
    }
    Console.WriteLine("CRC-32 match: OK");
  } catch (UnauthorizedAccessException) {
    Console.Error.WriteLine($"Access denied to '{device}'. Run as administrator/root.");
    return 1;
  } catch (Exception ex) {
    Console.Error.WriteLine($"Deploy failed: {ex.Message}");
    return 1;
  }

  // Verify pass
  if (verify) {
    Console.WriteLine();
    Console.Write("Verifying...");
    try {
      using var src = File.OpenRead(image.FullName);
      using var dst = new FileStream(device, FileMode.Open, FileAccess.Read, FileShare.None);

      var verifyCrc = new Compression.Core.Checksums.Crc32();
      var verified = 0L;
      var sw2 = Stopwatch.StartNew();
      int bytesRead2;
      while ((bytesRead2 = dst.Read(buffer, 0, ChunkSize)) > 0 && verified < imageSize) {
        var toCheck = (int)Math.Min(bytesRead2, imageSize - verified);
        verifyCrc.Update(buffer.AsSpan(0, toCheck));
        verified += toCheck;

        if (sw2.ElapsedMilliseconds > 500 || verified >= imageSize) {
          var mbps = sw2.Elapsed.TotalSeconds > 0 ? verified / (1024.0 * 1024) / sw2.Elapsed.TotalSeconds : 0;
          Console.Write($"\rVerifying: {FormatSize(verified)} / {FormatSize(imageSize)} {mbps:F1} MB/s  ");
        }
      }
      sw2.Stop();
      Console.WriteLine();

      Console.WriteLine($"Verify CRC-32: 0x{verifyCrc.Value:X8}");
      if (verifyCrc.Value != writeCrc.Value) {
        Console.Error.WriteLine("*** VERIFY FAILED — read-back CRC does not match! ***");
        return 1;
      }
      Console.WriteLine("Verify: OK");
    } catch (Exception ex) {
      Console.Error.WriteLine($"Verify failed: {ex.Message}");
      return 1;
    }
  }

  return 0;
});

// ── convert-clusters ──────────────────────────────────────────────────

var ccDiskArg = new Argument<FileInfo>("image") { Description = "FAT filesystem image to convert" };
var ccClusterSizeOpt = new Option<int>("--cluster-size") { Description = "Target cluster size in bytes (power of 2, e.g. 512, 1024, 4096)" };
var ccOutputOpt = new Option<FileInfo?>("--output", "-o") { Description = "Output path (default: overwrite in place)" };
var ccYesOpt = new Option<bool>("--yes", "-y") { Description = "Skip interactive confirmation" };

var convertClustersCmd = new Command("convert-clusters", """
  Rebuild a FAT image with a different cluster size. Shows a before/after
  waste preview before modifying the image.

  Examples:
    cwb convert-clusters disk.img --cluster-size 1024
    cwb convert-clusters disk.img --cluster-size 4096 -o output.img
    cwb convert-clusters disk.img --cluster-size 512 --yes
  """) { ccDiskArg, ccClusterSizeOpt, ccOutputOpt, ccYesOpt };

convertClustersCmd.SetAction((ParseResult ctx) => {
  var image = ctx.GetValue(ccDiskArg)!;
  var clusterSize = ctx.GetValue(ccClusterSizeOpt);
  var output = ctx.GetValue(ccOutputOpt);
  var autoConfirm = ctx.GetValue(ccYesOpt);

  if (!image.Exists) { Console.Error.WriteLine($"File not found: {image.FullName}"); return 1; }
  if (clusterSize <= 0 || (clusterSize & (clusterSize - 1)) != 0) {
    Console.Error.WriteLine($"Cluster size must be a positive power of 2, got: {clusterSize}");
    return 1;
  }

  FormatRegistration.EnsureInitialized();

  // Preview
  try {
    var (current, target) = ArchiveOperations.PreviewClusterConversion(image.FullName, clusterSize);
    Console.WriteLine($"Current: {current.ClusterSize}-byte clusters, {current.SlackPercent:F0}% tip slack ({FormatSize(current.TotalSlack)} wasted)");
    Console.WriteLine($"Target:  {target.ClusterSize}-byte clusters, {target.SlackPercent:F0}% tip slack ({FormatSize(target.TotalSlack)} wasted)");
  } catch (Exception ex) {
    Console.Error.WriteLine($"Preview failed: {ex.Message}");
    return 1;
  }

  if (!autoConfirm) {
    Console.Write("Proceed? [y/N] ");
    var response = Console.ReadLine()?.Trim();
    if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase)) {
      Console.WriteLine("Aborted.");
      return 0;
    }
  }

  var outPath = output?.FullName ?? image.FullName;
  Console.Write($"Rebuilding with {clusterSize}-byte clusters...");
  var sw = Stopwatch.StartNew();
  ArchiveOperations.ConvertClusters(image.FullName, outPath, clusterSize);
  sw.Stop();
  Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
  Console.WriteLine($"Output: {outPath} ({FormatSize(new FileInfo(outPath).Length)})");
  return 0;
});

// ── resize ──────────────────────────────────────────────────────────

var resizeDiskArg = new Argument<FileInfo>("image") { Description = "Filesystem image to resize" };
var resizeProfileOpt = new Option<string?>("--profile") { Description = "Media profile: 3.5-hd, 3.5-dd, 5.25-hd, 5.25-dd, cd, dvd, bd" };
var resizeSizeOpt = new Option<string?>("--size") { Description = "Custom target size (e.g. 1440k, 100m, 4g)" };
var resizeYesOpt = new Option<bool>("--yes", "-y") { Description = "Skip interactive confirmation" };

var resizeCmd2 = new Command("resize", """
  Resize a filesystem image to a media profile or custom size. Extracts all
  files, rebuilds the image at the target size. Refuses if content doesn't fit.

  Profiles:
    3.5-hd   = 1,474,560 bytes (1.44 MB, FAT12)
    3.5-dd   = 737,280 bytes (720 KB)
    5.25-hd  = 1,228,800 bytes (1.2 MB)
    5.25-dd  = 368,640 bytes (360 KB)
    cd       = 681,984,000 bytes (650 MB)
    dvd      = 4,700,000,000 bytes (4.7 GB)
    bd       = 25,025,314,816 bytes (25 GB)

  Examples:
    cwb resize disk.img --profile 3.5-hd
    cwb resize disk.img --size 1440k
    cwb resize disk.img --size 100m --yes
  """) { resizeDiskArg, resizeProfileOpt, resizeSizeOpt, resizeYesOpt };

resizeCmd2.SetAction((ParseResult ctx) => {
  var image = ctx.GetValue(resizeDiskArg)!;
  var profileStr = ctx.GetValue(resizeProfileOpt);
  var sizeStr = ctx.GetValue(resizeSizeOpt);
  var autoConfirm = ctx.GetValue(resizeYesOpt);

  if (!image.Exists) { Console.Error.WriteLine($"File not found: {image.FullName}"); return 1; }
  if (string.IsNullOrEmpty(profileStr) && string.IsNullOrEmpty(sizeStr)) {
    Console.Error.WriteLine("Specify --profile or --size.");
    return 1;
  }

  long targetSize;
  if (!string.IsNullOrEmpty(profileStr)) {
    if (!MediaProfileLookup.TryParse(profileStr, out var profile)) {
      Console.Error.WriteLine($"Unknown profile '{profileStr}'. Known: 3.5-hd, 3.5-dd, 5.25-hd, 5.25-dd, cd, dvd, bd");
      return 1;
    }
    targetSize = MediaProfileLookup.GetSize(profile);
  } else {
    targetSize = ParseSizeGeneric(sizeStr!);
    if (targetSize <= 0) {
      Console.Error.WriteLine($"Invalid size: {sizeStr}");
      return 1;
    }
  }

  FormatRegistration.EnsureInitialized();

  // Preview
  try {
    var preview = ArchiveOperations.PreviewResize(image.FullName, targetSize);
    Console.WriteLine($"Current: {FormatSize(preview.CurrentSize)}");
    Console.WriteLine($"Target:  {FormatSize(preview.TargetSize)}");
    Console.WriteLine($"Content: {FormatSize(preview.ContentSize)}");
    if (!preview.Fits) {
      Console.Error.WriteLine("ERROR: Content does not fit in target size.");
      return 1;
    }
  } catch (Exception ex) {
    Console.Error.WriteLine($"Preview failed: {ex.Message}");
    return 1;
  }

  if (!autoConfirm) {
    Console.Write("Proceed? [y/N] ");
    var response = Console.ReadLine()?.Trim();
    if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase)) {
      Console.WriteLine("Aborted.");
      return 0;
    }
  }

  Console.Write($"Resizing to {FormatSize(targetSize)}...");
  var sw = Stopwatch.StartNew();
  ArchiveOperations.Resize(image.FullName, targetSize);
  sw.Stop();
  Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
  Console.WriteLine($"Output: {image.FullName} ({FormatSize(new FileInfo(image.FullName).Length)})");
  return 0;
});

// ── convert-archive (was: convert-fs) ───────────────────────────────

var cfsInputArg = new Argument<FileInfo>("input") { Description = "Source archive or filesystem image" };
var cfsOutputArg = new Argument<FileInfo>("output") { Description = "Destination archive or filesystem image" };
var cfsFormatOpt = new Option<string?>("--format", "-f") { Description = "Target format ID (e.g. fat, ext, d64, zip, tar, 7z). Auto-detected from extension if omitted" };
var cfsOptOpt = new Option<string[]>("--opt") {
  Description = "Format-specific tunable as KEY=VALUE (repeatable). Bare KEY = true. Later --opt for the same KEY overrides earlier ones."
};

var convertArchiveHelp = """
  Convert between any listable/creatable format pair. Works across categories:

    FS -> FS:        cwb convert-archive disk.d64 output.img --format fat
    FS -> Archive:   cwb convert-archive disk.d64 output.zip
    Archive -> FS:   cwb convert-archive archive.zip output.img --format fat
    Archive -> Arc:  cwb convert-archive in.zip out.tar

  Extracts all files from the source (any format with List+Extract), then
  creates the target (any format with Create). Metadata (timestamps,
  permissions) is preserved where both formats support them; lost metadata
  is logged.

  Same-format / FAT-variant / ext-variant pairs take the in-place metadata
  fast path; everything else uses extract + rebuild.

  For archive-to-archive conversion that exploits bitstream / container
  restreaming (e.g. gz -> zlib without recompressing the Deflate stream),
  use 'cwb convert' instead — it has smart tier 1/2/3 dispatch.

  Format-specific tunables (e.g. FAT type, cluster size, volume label) are
  passed via repeatable --opt KEY=VALUE flags. The target descriptor
  publishes the schema; unknown keys are forwarded to the writer as-is and
  may be ignored. Use 'cwb convert-archive source.tar disk.fat --opt FatType=FAT16 --opt ClusterSize=4096'
  to drive variants and geometry from the command line.

  Examples:
    cwb convert-archive source.zip target.img                        # target type from extension
    cwb convert-archive source.tar disk.fat --opt FatType=FAT16 --opt ClusterSize=4096
    cwb convert-archive input.d64 output.img --format fat
    cwb convert-archive disk.img output.d64
    cwb convert-archive old.img new.img --format ext
    cwb convert-archive archive.zip output.img --format fat
    cwb convert-archive disk.d64 output.7z
  """;

var convertArchiveCmd = new Command("convert-archive", convertArchiveHelp) { cfsInputArg, cfsOutputArg, cfsFormatOpt, cfsOptOpt };

Func<ParseResult, int> convertArchiveAction = (ParseResult ctx) => {
  var input = ctx.GetValue(cfsInputArg)!;
  var output = ctx.GetValue(cfsOutputArg)!;
  var formatId = ctx.GetValue(cfsFormatOpt);
  var optPairs = ctx.GetValue(cfsOptOpt) ?? [];

  if (!input.Exists) { Console.Error.WriteLine($"File not found: {input.FullName}"); return 1; }

  FormatRegistration.EnsureInitialized();

  // Parse --opt KEY=VALUE pairs. Bare KEY (no '=') is treated as a boolean
  // flag set to "true". Later occurrences of the same KEY win — last write
  // wins matches the convention of CLI override flags everywhere else.
  Compression.Registry.FormatCreateOptions? createOptions = null;
  if (optPairs.Length > 0) {
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var raw in optPairs) {
      if (string.IsNullOrEmpty(raw)) continue;
      var eq = raw.IndexOf('=');
      if (eq < 0) {
        dict[raw] = "true";
      } else {
        var key = raw[..eq];
        var value = raw[(eq + 1)..];
        if (!string.IsNullOrEmpty(key)) dict[key] = value;
      }
    }
    if (dict.Count > 0)
      createOptions = new Compression.Registry.FormatCreateOptions { FormatSpecific = dict };
  }

  var srcFormat = FormatDetector.Detect(input.FullName);
  Console.WriteLine($"Source: {input.Name} ({srcFormat})");

  Console.Write($"Converting...");
  var sw = Stopwatch.StartNew();
  var warnings = ArchiveOperations.ConvertArchive(input.FullName, output.FullName, formatId, createOptions);
  sw.Stop();
  Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
  Console.WriteLine($"Output: {output.FullName} ({FormatSize(new FileInfo(output.FullName).Length)})");

  if (warnings.Count > 0) {
    Console.WriteLine();
    Console.WriteLine("Metadata warnings:");
    foreach (var w in warnings)
      Console.WriteLine($"  {w}");
  }

  return 0;
};

convertArchiveCmd.SetAction(convertArchiveAction);

// Hidden back-compat alias for the old name; same handler, same arguments.
var convertFsCmd = new Command("convert-fs", "[Deprecated] Alias for 'convert-archive'.") { cfsInputArg, cfsOutputArg, cfsFormatOpt, cfsOptOpt };
convertFsCmd.Hidden = true;
convertFsCmd.SetAction(convertArchiveAction);

// ── dedup ────────────────────────────────────────────────────────────

var dedupImageArg = new Argument<FileInfo>("image") { Description = "Filesystem image or archive to deduplicate" };
var dedupDryRunOpt = new Option<bool>("--dry-run") { Description = "Report duplicates without modifying the image" };
var dedupStrategyOpt = new Option<string>("--keep") {
  Description = "Which file to keep: first (default) or shallowest (shallowest directory depth)",
  DefaultValueFactory = _ => "first",
};

var dedupCmd = new Command("dedup", """
  Scan a filesystem image or archive for duplicate files (by SHA-256 content hash).

  Dry run (default with --dry-run): reports duplicate groups with sizes and potential savings.
  Execute (without --dry-run): for formats that support creation, rebuilds the image
  without duplicates. For formats without creation support, only reporting is available.

  Examples:
    cwb dedup disk.img                          Execute dedup (keep first)
    cwb dedup disk.img --dry-run                Report only
    cwb dedup disk.zip --keep first             Remove dupes, keep first occurrence
    cwb dedup disk.img --keep shallowest        Keep file at shallowest path
  """) { dedupImageArg, dedupDryRunOpt, dedupStrategyOpt };
dedupCmd.SetAction((ParseResult ctx) => {
  var image = ctx.GetValue(dedupImageArg)!;
  var dryRun = ctx.GetValue(dedupDryRunOpt);
  var strategyStr = ctx.GetValue(dedupStrategyOpt) ?? "first";

  if (!image.Exists) { Console.Error.WriteLine($"File not found: {image.FullName}"); return 1; }

  var strategy = strategyStr.ToLowerInvariant() switch {
    "shallowest" or "largest-path" or "keep-largest-path" => DeduplicationStrategy.KeepLargestPath,
    _ => DeduplicationStrategy.KeepFirst,
  };

  Console.Write($"Scanning {image.Name} for duplicates...");
  var sw = Stopwatch.StartNew();
  var report = DeduplicationScanner.Analyze(image.FullName);
  sw.Stop();
  Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
  Console.WriteLine();

  Console.WriteLine($"Total files:      {report.TotalFiles}");
  Console.WriteLine($"Unique files:     {report.UniqueFiles}");
  Console.WriteLine($"Duplicate files:  {report.DuplicateFiles}");
  Console.WriteLine($"Total size:       {FormatSize(report.TotalSize)}");
  Console.WriteLine($"Wasted bytes:     {FormatSize(report.WastedBytes)}");
  Console.WriteLine();

  if (report.Groups.Count == 0) {
    Console.WriteLine("No duplicates found.");
    return 0;
  }

  Console.WriteLine($"{"Hash (first 8)",16} {"Size",12} {"Count",6} {"Wasted",12}  Files");
  Console.WriteLine(new string('-', 90));
  foreach (var g in report.Groups.Take(50)) {
    var hashStr = Convert.ToHexString(g.ContentHash)[..16];
    Console.WriteLine($"{hashStr,16} {FormatSize(g.Size),12} {g.FileNames.Count,6} {FormatSize(g.WastedBytes),12}  {g.FileNames[0]}");
    foreach (var f in g.FileNames.Skip(1))
      Console.WriteLine($"{"",16} {"",12} {"",6} {"",12}  {f}");
  }
  if (report.Groups.Count > 50)
    Console.WriteLine($"  ... and {report.Groups.Count - 50} more groups");
  Console.WriteLine();

  if (dryRun) {
    Console.WriteLine($"Potential savings: {FormatSize(report.PotentialSavings)} (dry run, no changes made)");
    return 0;
  }

  // Execute deduplication
  try {
    Console.Write($"Deduplicating (strategy: {strategyStr})...");
    var sw2 = Stopwatch.StartNew();
    var saved = DeduplicationScanner.Execute(image.FullName, strategy);
    sw2.Stop();
    Console.WriteLine($" done ({sw2.ElapsedMilliseconds}ms)");
    Console.WriteLine($"Bytes saved: {FormatSize(saved)}");
  } catch (NotSupportedException ex) {
    Console.Error.WriteLine($"Cannot execute deduplication: {ex.Message}");
    Console.Error.WriteLine("Use --dry-run to view the report without modifying the image.");
    return 1;
  }

  return 0;
});

// ── sparsify ────────────────────────────────────────────────────────

var sparsifyImageArg = new Argument<FileInfo>("image") { Description = "Container image to sparsify (VHD, QCOW2, VDI, VMDK)" };

var sparsifyCmd = new Command("sparsify", """
  Sparsify a container disk image: scan all allocated blocks, detect all-zero
  blocks, and rebuild the container without them.

  Supported formats: VHD (dynamic), QCOW2, VDI, VMDK.
  For VHD: converts fixed VHD to dynamic if needed, then removes zero blocks.
  For QCOW2/VDI/VMDK: extracts virtual disk and rewrites with sparse detection.

  Examples:
    cwb sparsify disk.vhd          Compact VHD (same as shrink --compact)
    cwb sparsify disk.qcow2        Remove zero-filled clusters
    cwb sparsify disk.vdi          Remove zero-filled blocks
    cwb sparsify disk.vmdk         Remove zero-filled grains
  """) { sparsifyImageArg };
sparsifyCmd.SetAction((ParseResult ctx) => {
  var image = ctx.GetValue(sparsifyImageArg)!;
  if (!image.Exists) { Console.Error.WriteLine($"File not found: {image.FullName}"); return 1; }

  var origSize = image.Length;
  Console.Write($"Sparsifying {image.Name} ({FormatSize(origSize)})...");
  var sw = Stopwatch.StartNew();

  try {
    var freed = SparseConverter.Sparsify(image.FullName);
    sw.Stop();
    Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
    var newSize = new FileInfo(image.FullName).Length;
    Console.WriteLine($"  {FormatSize(origSize)} -> {FormatSize(newSize)} ({FormatSize(freed)} freed)");
  } catch (NotSupportedException ex) {
    sw.Stop();
    Console.Error.WriteLine($" FAILED: {ex.Message}");
    return 1;
  } catch (Exception ex) {
    sw.Stop();
    Console.Error.WriteLine($" FAILED: {ex.Message}");
    return 1;
  }
  return 0;
});

// ── densify ─────────────────────────────────────────────────────────

var densifyImageArg = new Argument<FileInfo>("image") { Description = "Container image to densify (VHD, QCOW2, VDI, VMDK)" };

var densifyCmd = new Command("densify", """
  Densify a container disk image: ensure all virtual blocks are physically
  allocated. Useful before deploying to hardware that doesn't support sparse.

  Supported formats: VHD, QCOW2, VDI, VMDK.
  For VHD: rebuilds as a fixed VHD (every block physically present).
  For QCOW2/VDI/VMDK: rewrites with all blocks allocated.

  Examples:
    cwb densify disk.vhd          Convert dynamic VHD to fixed
    cwb densify disk.qcow2        Pre-allocate all clusters
    cwb densify disk.vdi          Pre-allocate all blocks
    cwb densify disk.vmdk         Pre-allocate all grains
  """) { densifyImageArg };
densifyCmd.SetAction((ParseResult ctx) => {
  var image = ctx.GetValue(densifyImageArg)!;
  if (!image.Exists) { Console.Error.WriteLine($"File not found: {image.FullName}"); return 1; }

  var origSize = image.Length;
  Console.Write($"Densifying {image.Name} ({FormatSize(origSize)})...");
  var sw = Stopwatch.StartNew();

  try {
    var allocated = SparseConverter.Densify(image.FullName);
    sw.Stop();
    Console.WriteLine($" done ({sw.ElapsedMilliseconds}ms)");
    var newSize = new FileInfo(image.FullName).Length;
    Console.WriteLine($"  {FormatSize(origSize)} -> {FormatSize(newSize)} ({FormatSize(allocated)} allocated)");
  } catch (NotSupportedException ex) {
    sw.Stop();
    Console.Error.WriteLine($" FAILED: {ex.Message}");
    return 1;
  } catch (Exception ex) {
    sw.Stop();
    Console.Error.WriteLine($" FAILED: {ex.Message}");
    return 1;
  }
  return 0;
});

// ── partition ────────────────────────────────────────────────────────

var partitionImageArg = new Argument<FileInfo>("image") { Description = "Disk image (raw .img/.raw, or virtual disk: VHD/VHDX/VMDK/QCOW2/VDI)" };

var partitionListCmd = new Command("list", """
  List all partitions in a disk image. Auto-detects the host container
  (VHD/VHDX/VMDK/QCOW2/VDI) and opens its guest disk; raw images are read
  directly.
  Example: cwb partition list disk.vhd
  """) { partitionImageArg };
partitionListCmd.Aliases.Add("ls");
partitionListCmd.SetAction((ParseResult ctx) => {
  var image = ctx.GetValue(partitionImageArg)!;
  if (!image.Exists) { Console.Error.WriteLine($"File not found: {image.FullName}"); return 1; }

  try {
    var result = PartitionOperations.List(image.FullName);
    Console.WriteLine($"Image:  {image.Name}");
    Console.WriteLine($"Scheme: {result.Scheme}");
    if (result.Partitions.Count == 0) { Console.WriteLine("(no partitions)"); return 0; }

    Console.WriteLine();
    Console.WriteLine($"{"#",-3} {"Source",-24} {"Type",-10} {"Start",14} {"Size",14} {"Bootable",8} {"Label"}");
    Console.WriteLine(new string('-', 90));
    foreach (var p in result.Partitions) {
      var boot = p.IsActive ? "yes" : "";
      Console.WriteLine($"{p.Index,-3} {p.Source,-24} {p.TypeCode,-10} {FormatSize(p.StartOffset),14} {FormatSize(p.Size),14} {boot,8} {p.Name}");
    }
    return 0;
  } catch (Exception ex) {
    Console.Error.WriteLine($"List failed: {ex.Message}");
    return 1;
  }
});

var partitionAddImageArg = new Argument<FileInfo>("image") { Description = "Disk image to modify" };
var partitionAddStartOpt = new Option<long>("--start") { Description = "Start sector (LBA, multiplied by 512 internally)", Required = true };
var partitionAddLengthOpt = new Option<long>("--length") { Description = "Length in sectors", Required = true };
var partitionAddTypeOpt = new Option<string>("--type") { Description = "Partition type: Linux, Fat32Lba, NtfsExfat, EfiSystem, ExtendedLba, …", Required = true };
var partitionAddLabelOpt = new Option<string?>("--label") { Description = "Partition label (GPT only)" };

var partitionAddCmd = new Command("add", """
  Add a primary or logical partition. If an extended container exists and the
  range falls inside it, the new entry is added as a logical partition.
  Sectors are 512 bytes each.
  Example:
    cwb partition add disk.vhd --start 2048 --length 2048 --type Linux
    cwb partition add disk.img --start 4096 --length 1024 --type Fat32Lba --label boot
  """) { partitionAddImageArg, partitionAddStartOpt, partitionAddLengthOpt, partitionAddTypeOpt, partitionAddLabelOpt };
partitionAddCmd.SetAction((ParseResult ctx) => {
  var image = ctx.GetValue(partitionAddImageArg)!;
  if (!image.Exists) { Console.Error.WriteLine($"File not found: {image.FullName}"); return 1; }
  try {
    var type = PartitionOperations.ParseType(ctx.GetValue(partitionAddTypeOpt)!);
    var startBytes = ctx.GetValue(partitionAddStartOpt) * PartitionEditor.SectorSize;
    var lengthBytes = ctx.GetValue(partitionAddLengthOpt) * PartitionEditor.SectorSize;
    PartitionOperations.Add(image.FullName, startBytes, lengthBytes, type, ctx.GetValue(partitionAddLabelOpt));
    Console.WriteLine($"Added {type} partition: start={startBytes:N0} bytes, size={lengthBytes:N0} bytes.");
    return 0;
  } catch (Exception ex) {
    Console.Error.WriteLine($"Add failed: {ex.Message}");
    return 1;
  }
});

var partitionDelImageArg = new Argument<FileInfo>("image") { Description = "Disk image to modify" };
var partitionDelIndexOpt = new Option<int>("--index") { Description = "Zero-based partition index to delete", Required = true };

var partitionDeleteCmd = new Command("delete", """
  Delete a partition by index. Data bytes on disk are left untouched — use
  'cwb partition purge' to also zero-fill the byte range.
  Example: cwb partition delete disk.vhd --index 1
  """) { partitionDelImageArg, partitionDelIndexOpt };
partitionDeleteCmd.Aliases.Add("rm");
partitionDeleteCmd.SetAction((ParseResult ctx) => {
  var image = ctx.GetValue(partitionDelImageArg)!;
  if (!image.Exists) { Console.Error.WriteLine($"File not found: {image.FullName}"); return 1; }
  try {
    var idx = ctx.GetValue(partitionDelIndexOpt);
    PartitionOperations.Delete(image.FullName, idx);
    Console.WriteLine($"Deleted partition #{idx}.");
    return 0;
  } catch (Exception ex) {
    Console.Error.WriteLine($"Delete failed: {ex.Message}");
    return 1;
  }
});

var partitionPurgeImageArg = new Argument<FileInfo>("image") { Description = "Disk image to modify" };
var partitionPurgeIndexOpt = new Option<int>("--index") { Description = "Zero-based partition index to purge", Required = true };

var partitionPurgeCmd = new Command("purge", """
  Delete a partition and zero-fill its on-disk byte range. Slower than delete
  but ensures no remnants of the previous filesystem survive.
  Example: cwb partition purge disk.vhd --index 0
  """) { partitionPurgeImageArg, partitionPurgeIndexOpt };
partitionPurgeCmd.SetAction((ParseResult ctx) => {
  var image = ctx.GetValue(partitionPurgeImageArg)!;
  if (!image.Exists) { Console.Error.WriteLine($"File not found: {image.FullName}"); return 1; }
  try {
    var idx = ctx.GetValue(partitionPurgeIndexOpt);
    PartitionOperations.Purge(image.FullName, idx);
    Console.WriteLine($"Purged partition #{idx} (table entry removed, bytes zeroed).");
    return 0;
  } catch (Exception ex) {
    Console.Error.WriteLine($"Purge failed: {ex.Message}");
    return 1;
  }
});

var partitionConvImageArg = new Argument<FileInfo>("image") { Description = "Disk image to convert" };
var partitionConvToOpt = new Option<string>("--to") { Description = "Target scheme: mbr or gpt", Required = true };

var partitionConvertCmd = new Command("convert", """
  Convert the partition scheme between MBR and GPT. MBR→GPT promotes the
  protective MBR and translates type bytes to GUIDs; GPT→MBR works if the GPT
  has at most 4 entries.
  Examples:
    cwb partition convert disk.img --to gpt
    cwb partition convert disk.vhd --to mbr
  """) { partitionConvImageArg, partitionConvToOpt };
partitionConvertCmd.SetAction((ParseResult ctx) => {
  var image = ctx.GetValue(partitionConvImageArg)!;
  if (!image.Exists) { Console.Error.WriteLine($"File not found: {image.FullName}"); return 1; }
  try {
    var target = PartitionOperations.ParseScheme(ctx.GetValue(partitionConvToOpt)!);
    PartitionOperations.Convert(image.FullName, target);
    Console.WriteLine($"Converted partition scheme to {target}.");
    return 0;
  } catch (Exception ex) {
    Console.Error.WriteLine($"Convert failed: {ex.Message}");
    return 1;
  }
});

var partitionFormatImageArg = new Argument<FileInfo>("image") { Description = "Disk image whose partition to format" };
var partitionFormatIndexOpt = new Option<int>("--index") { Description = "Zero-based partition index to format", Required = true };
var partitionFormatFsOpt = new Option<string>("--fs") { Description = "Filesystem format ID (e.g. Fat, Ext, Ntfs, ExFat) — must be a creatable format", Required = true };

var partitionFormatCmd = new Command("format", """
  Write a fresh filesystem image of --fs into the partition's byte range.
  The format ID must be registered and support creation; the generated bytes
  must fit within the partition.
  Example: cwb partition format disk.vhd --index 0 --fs Fat
  """) { partitionFormatImageArg, partitionFormatIndexOpt, partitionFormatFsOpt };
partitionFormatCmd.SetAction((ParseResult ctx) => {
  var image = ctx.GetValue(partitionFormatImageArg)!;
  if (!image.Exists) { Console.Error.WriteLine($"File not found: {image.FullName}"); return 1; }
  try {
    var idx = ctx.GetValue(partitionFormatIndexOpt);
    var fs = ctx.GetValue(partitionFormatFsOpt)!;
    PartitionOperations.Format(image.FullName, idx, fs);
    Console.WriteLine($"Formatted partition #{idx} as {fs}.");
    return 0;
  } catch (Exception ex) {
    Console.Error.WriteLine($"Format failed: {ex.Message}");
    return 1;
  }
});

var partitionVerifyImageArg = new Argument<FileInfo>("image") { Description = "Disk image to verify" };

var partitionVerifyCmd = new Command("verify", """
  Check MBR/GPT integrity: signature presence, GPT header/entry-array CRCs,
  primary/backup consistency, out-of-range extents.
  Example: cwb partition verify disk.vhd
  """) { partitionVerifyImageArg };
partitionVerifyCmd.SetAction((ParseResult ctx) => {
  var image = ctx.GetValue(partitionVerifyImageArg)!;
  if (!image.Exists) { Console.Error.WriteLine($"File not found: {image.FullName}"); return 1; }
  try {
    var result = PartitionOperations.Verify(image.FullName);
    Console.WriteLine($"Scheme: {result.Scheme}");
    if (result.IsValid) {
      Console.WriteLine("Status: OK");
      return 0;
    }
    Console.WriteLine("Status: ISSUES");
    foreach (var issue in result.Issues)
      Console.WriteLine($"  - {issue}");
    return 1;
  } catch (Exception ex) {
    Console.Error.WriteLine($"Verify failed: {ex.Message}");
    return 1;
  }
});

var partitionCmd = new Command("partition", """
  Edit the MBR/GPT partition table of a raw disk image or a virtual-disk
  container (VHD/VHDX/VMDK/QCOW2/VDI).

  Subcommands:
    list      Show all primaries + logicals in disk-table order.
    add       Add a primary (or logical, if inside an extended container).
    delete    Remove a partition entry; bytes left untouched.
    purge     Remove + zero-fill the partition's bytes.
    convert   Switch between MBR and GPT schemes.
    format    Write a fresh filesystem image into a partition.
    verify    Check MBR/GPT signature, CRCs, and extent bounds.
  """) {
  partitionListCmd, partitionAddCmd, partitionDeleteCmd, partitionPurgeCmd,
  partitionConvertCmd, partitionFormatCmd, partitionVerifyCmd
};

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
    cwb defragment disk.img --mode pack-end   Repack files at end of image
    cwb shrink disk.img                      Defrag + truncate trailing free space
    cwb shrink disk.vhd --compact            Also compact container (VHD sparse)
    cwb wipe-empty disk.img                  Zero all unused space in image
    cwb dedup disk.img --dry-run             Find duplicate files in image
    cwb sparsify disk.vhd                    Remove zero-filled blocks
    cwb densify disk.qcow2                   Pre-allocate all blocks
    cwb analyze disk.img --cluster-hint      Suggest optimal cluster size
    cwb tool init                             Set up external tool templates
    cwb reverse-engineer MyTool.exe "{input} {output}"  Auto-discover format

  Format is auto-detected from extension. Run 'cwb formats' for full format list,
  or 'cwb create --help' for compression options and examples.
  """) {
  listCmd, extractCmd, createCmd, testCmd, addCmd, removeCmd, replaceCmd, infoCmd, convertCmd, optimizeCmd, bestfitCmd, benchCmd, formatsCmd, analyzeCmd, autoExtractCmd, batchCmd, suggestCmd, toolCmd, reverseCmd, carveCmd, visualizeCmd, defragCmd, shrinkCmd, wipeCmd, deployCmd, convertClustersCmd, resizeCmd2, convertArchiveCmd, convertFsCmd, dedupCmd, sparsifyCmd, densifyCmd, partitionCmd
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

static int RunBestFit(
    FileInfo file,
    FileInfo? apply,
    Compression.Analysis.BestBlockSelector.Objective objective
      = Compression.Analysis.BestBlockSelector.Objective.SmallestOutput) {
  var data = File.ReadAllBytes(file.FullName);
  Console.WriteLine($"Searching building blocks for: {file.Name} ({FormatSize(data.Length)})");
  Console.WriteLine();

  Compression.Analysis.BestBlockSelector.Result result;
  try {
    result = Compression.Analysis.BestBlockSelector.Select(data, new Compression.Analysis.BestBlockSelector.Options {
      Objective = objective,
    });
  } catch (Exception ex) {
    Console.Error.WriteLine($"Selection failed: {ex.Message}");
    return 1;
  }

  Console.WriteLine($"{"Algorithm",-16} {"Compressed",12} {"Ratio",8} {"Compress",10} {"Status",10}");
  Console.WriteLine(new string('-', 60));
  foreach (var c in result.Table) {
    var marker = c.BlockId == result.WinningBlockId ? " <-best" : "";
    if (c.Succeeded)
      Console.WriteLine($"{c.DisplayName,-16} {FormatSize(c.CompressedSize),12} {c.Ratio * 100,7:F1}% {c.CompressTimeMs,7:F0}ms {"ok",10}{marker}");
    else
      Console.WriteLine($"{c.DisplayName,-16} {"-",12} {"-",8} {"-",10} {c.Error ?? "failed",10}");
  }

  Console.WriteLine();
  var saving = result.OriginalSize > 0 ? (1.0 - result.Ratio) * 100 : 0;
  Console.WriteLine($"Winner: {result.WinningDisplayName} -> {FormatSize(result.CompressedSize)} ({saving:F1}% smaller, ratio {result.Ratio * 100:F1}%)");
  if (result.BestParameters is { Count: > 0 } && result.OptimizedFormatId is not null) {
    var paramStr = string.Join(", ", result.BestParameters.Select(kv => $"{kv.Key}={kv.Value}"));
    Console.WriteLine($"Tuned via {result.OptimizedFormatId}: {paramStr}");
  }

  if (apply is not null) {
    File.WriteAllBytes(apply.FullName, result.CompressedBytes);
    Console.WriteLine($"Wrote winning output: {apply.FullName} ({FormatSize(result.CompressedBytes.LongLength)})");
  }
  return 0;
}

static List<string> ResolveDefragTargets(string imageArg, bool isBatch, bool isRecursive) {
  // Single file path?
  if (!isBatch && !isRecursive && File.Exists(imageArg))
    return [imageArg];

  // Directory?
  if (Directory.Exists(imageArg)) {
    var searchOption = isRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    return [.. Directory.GetFiles(imageArg, "*", searchOption)];
  }

  // Glob pattern: split into directory + pattern
  var dir = Path.GetDirectoryName(imageArg);
  var pattern = Path.GetFileName(imageArg);
  if (string.IsNullOrEmpty(dir)) dir = ".";
  if (!Directory.Exists(dir)) return [];
  var searchOpt = isRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
  return [.. Directory.GetFiles(dir, pattern, searchOpt)];
}

static bool IsSystemDrive(string device) {
  var d = device.ToLowerInvariant().Trim();
  // Windows: refuse PhysicalDrive0 (typically the system disk)
  if (d.Contains("physicaldrive0")) return true;
  // Windows: refuse C:\ paths
  if (d.StartsWith("c:") || d.StartsWith(@"\\.\c:")) return true;
  // Linux: refuse the root or boot devices
  if (d == "/dev/sda" || d == "/dev/nvme0n1" || d == "/dev/vda") return true;
  // Mount points
  if (d == "/" || d == "/boot") return true;
  return false;
}

static long ParseSizeGeneric(string sizeStr) {
  if (string.IsNullOrWhiteSpace(sizeStr)) return 0;
  var s = sizeStr.Trim().ToLowerInvariant();
  long multiplier = 1;
  if (s.EndsWith('k')) { multiplier = 1024; s = s[..^1]; }
  else if (s.EndsWith('m')) { multiplier = 1024 * 1024; s = s[..^1]; }
  else if (s.EndsWith('g')) { multiplier = 1024L * 1024 * 1024; s = s[..^1]; }
  return long.TryParse(s, out var val) ? val * multiplier : 0;
}

static long ParseSize(string? sizeStr) {
  if (string.IsNullOrWhiteSpace(sizeStr)) return FileFormat.SevenZip.SolidBlockPlanner.DefaultMaxBlockSize;
  var s2 = sizeStr.Trim().ToLowerInvariant();
  if (s2 == "0") return 0; // single block (no splitting)
  long multiplier = 1;
  if (s2.EndsWith('k')) { multiplier = 1024; s2 = s2[..^1]; }
  else if (s2.EndsWith('m')) { multiplier = 1024 * 1024; s2 = s2[..^1]; }
  else if (s2.EndsWith('g')) { multiplier = 1024L * 1024 * 1024; s2 = s2[..^1]; }
  return long.TryParse(s2, out var val) ? val * multiplier : FileFormat.SevenZip.SolidBlockPlanner.DefaultMaxBlockSize;
}

