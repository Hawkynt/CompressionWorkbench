using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Compression.Lib;
using F = Compression.Lib.FormatDetector.Format;

namespace Compression.UI;

public partial class CreateOptionsWindow : Window {
  internal CreateOptionsViewModel Options { get; }

  internal CreateOptionsWindow(F format) {
    Options = new CreateOptionsViewModel(format);
    DataContext = Options;
    InitializeComponent();
  }

  private void OnCreate(object sender, RoutedEventArgs e) {
    DialogResult = true;
    Close();
  }

  private void OnPasswordChanged(object sender, RoutedEventArgs e) {
    if (sender is PasswordBox pb)
      Options.Password = pb.Password;
  }
}

internal sealed class CreateOptionsViewModel : INotifyPropertyChanged {
  private readonly F _format;
  private string _selectedMethod = "";
  private string _selectedLevel = "";
  private string _selectedDictSize = "";
  private string _selectedWordSize = "";
  private string _selectedSolidSize = "";
  private string _selectedThreads = "";
  private string _selectedSfxType = "";
  private string _selectedSfxTarget = "";
  private string _selectedEncryptionMethod = "";
  private bool _makeSfx;

  private static readonly long AvailableMemory = GetAvailableMemory();

  internal CreateOptionsViewModel(F format) {
    _format = format;
    SfxTypeOptions = ["Console (CLI)", "GUI (windowed)"];
    SfxTargetOptions = BuildSfxTargets();
    _selectedSfxType = SfxTypeOptions[0];
    _selectedSfxTarget = SfxTargetOptions[0];
    PopulateAll();
  }

  // ── Bound properties ────────────────────────────────────────────────

  public string FormatLabel => _format switch {
    F.SevenZip => "Create 7z Archive",
    F.Zip => "Create ZIP Archive",
    F.Rar => "Create RAR Archive",
    F.Cab => "Create CAB Cabinet",
    F.Lzh => "Create LHA Archive",
    F.Arj => "Create ARJ Archive",
    F.Arc => "Create ARC Archive",
    F.Zoo => "Create Zoo Archive",
    F.Ace => "Create ACE Archive",
    F.Sqx => "Create SQX Archive",
    F.TarGz => "Create TAR.GZ Archive",
    F.TarBz2 => "Create TAR.BZ2 Archive",
    F.TarXz => "Create TAR.XZ Archive",
    F.TarZst => "Create TAR.ZST Archive",
    _ when FormatDetector.IsStreamFormat(_format) => $"Compress with {_format}",
    _ => $"Create {_format} Archive",
  };

  public string[] Methods { get; private set; } = [];
  public string SelectedMethod {
    get => _selectedMethod;
    set { if (SetField(ref _selectedMethod, value)) OnMethodChanged(); }
  }

  public string[] LevelOptions { get; private set; } = [];
  public string SelectedLevel {
    get => _selectedLevel;
    set { if (SetField(ref _selectedLevel, value)) UpdateMemoryEstimate(); }
  }

  public string[] DictSizeOptions { get; private set; } = [];
  public string SelectedDictSize {
    get => _selectedDictSize;
    set { if (SetField(ref _selectedDictSize, value)) UpdateMemoryEstimate(); }
  }
  public string DictSizeLabel { get; private set; } = "Dictionary:";

  public string[] WordSizeOptions { get; private set; } = [];
  public string SelectedWordSize {
    get => _selectedWordSize;
    set { if (SetField(ref _selectedWordSize, value)) UpdateMemoryEstimate(); }
  }

  public string[] SolidSizeOptions { get; private set; } = [];
  public string SelectedSolidSize {
    get => _selectedSolidSize;
    set { if (SetField(ref _selectedSolidSize, value)) UpdateMemoryEstimate(); }
  }

  public string[] ThreadOptions { get; private set; } = [];
  public string SelectedThreads {
    get => _selectedThreads;
    set { if (SetField(ref _selectedThreads, value)) UpdateMemoryEstimate(); }
  }

  public string Password { get; set; } = "";
  public bool EncryptFilenames { get; set; }

  public string[] EncryptionMethodOptions { get; private set; } = [];
  public string SelectedEncryptionMethod { get => _selectedEncryptionMethod; set => SetField(ref _selectedEncryptionMethod, value); }

  public bool MakeSfx {
    get => _makeSfx;
    set { if (SetField(ref _makeSfx, value)) OnPropertyChanged(nameof(ShowSfxOptions)); }
  }

  public string[] SfxTypeOptions { get; }
  public string SelectedSfxType { get => _selectedSfxType; set => SetField(ref _selectedSfxType, value); }
  public string[] SfxTargetOptions { get; }
  public string SelectedSfxTarget { get => _selectedSfxTarget; set => SetField(ref _selectedSfxTarget, value); }

  internal bool SfxIsGui => SelectedSfxType?.Contains("GUI") == true;
  internal string? ResolvedSfxTargetRid {
    get {
      if (string.IsNullOrEmpty(SelectedSfxTarget) || SelectedSfxTarget.StartsWith("Current"))
        return null;
      return SelectedSfxTarget;
    }
  }

  // ── Visibility ──────────────────────────────────────────────────────

  public bool ShowMethod => Methods.Length > 1;
  public bool ShowLevel => LevelOptions.Length > 1;
  public bool ShowDictSize => DictSizeOptions.Length > 1;
  public bool ShowWordSize => WordSizeOptions.Length > 1;
  public bool ShowSolidSize => SolidSizeOptions.Length > 1;
  public bool ShowThreads => _format is F.SevenZip or F.Zip;
  public bool ShowPassword => _format is F.SevenZip or F.Zip or F.Rar or F.Arj or F.Ace or F.Sqx;
  public bool PasswordEnabled => SupportsEncryption();
  public bool ShowEncryptionMethod => _format is F.Zip;
  public bool ShowEncryptFilenames => _format is F.SevenZip or F.Rar;
  public bool ShowSfx => _format is F.SevenZip or F.Zip or F.Rar;
  public bool ShowSfxOptions => _makeSfx && ShowSfx;
  public bool ShowMemoryEstimate => !string.IsNullOrEmpty(MemoryEstimate);

  // ── Memory estimation ─────────────────────────────────────────────

  public string MemoryEstimate {
    get {
      var (compress, decompress) = EstimateMemoryBytes();
      if (compress <= 0 && decompress <= 0) return "";
      var compStr = FormatByteSize(compress);
      var decStr = FormatByteSize(decompress);
      var availStr = FormatByteSize(AvailableMemory);
      var msg = $"Memory: Compress ~{compStr}, Decompress ~{decStr}  (available: {availStr})";
      if (decompress > AvailableMemory)
        msg += "\n\u26a0 Decompression exceeds available RAM — recipients may not be able to extract!";
      else if (compress > AvailableMemory)
        msg += "\n\u26a0 Compression exceeds available RAM — may use swap and run very slowly.";
      return msg;
    }
  }

  /// <summary>Text foreground: dark red if decompress exceeds, dark orange if compress exceeds, green otherwise.</summary>
  public System.Windows.Media.Brush MemoryBrush {
    get {
      var (compress, decompress) = EstimateMemoryBytes();
      if (compress <= 0 && decompress <= 0) return System.Windows.Media.Brushes.Gray;
      if (decompress > AvailableMemory)
        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 0, 0)); // dark red
      if (compress > AvailableMemory)
        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 80, 0)); // dark amber
      if (compress > AvailableMemory / 2)
        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 80, 0)); // dark amber
      return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 100, 0)); // dark green
    }
  }

  /// <summary>Background: salmon if decompress exceeds memory, yellow if compress exceeds, neutral otherwise.</summary>
  public System.Windows.Media.Brush MemoryBackground {
    get {
      var (compress, decompress) = EstimateMemoryBytes();
      if (decompress > AvailableMemory)
        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 160, 140)); // salmon
      if (compress > AvailableMemory)
        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 180)); // yellow
      return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 248, 248)); // neutral gray
    }
  }

  private (long compress, long decompress) EstimateMemoryBytes() {
    string method = ResolveMethodName();
    long dict = GetEffectiveDictSize(method);
    int threads = ParseThreads();
    long solidBlock = GetEffectiveSolidBlock();

    // For 7z: each thread works on one solid block simultaneously.
    // Encoder state (dict-dependent) + solid block data buffer per thread.
    // Decompression: single thread, but must hold full decoded solid block.
    //
    // For ZIP: threads are per-entry (no solid blocks), so only encoder state scales.

    return (_format, method) switch {
      // LZMA/LZMA2 — heavy on compress side
      // Encoder: ~11.5x dict. Solid block data buffered per thread.
      (F.SevenZip, "lzma" or "lzma2") when dict > 0 =>
        ((dict * 23 / 2 + solidBlock) * threads, dict + solidBlock + 1024 * 1024),
      (_, "lzma" or "lzma2") when dict > 0 =>
        (dict * 23 / 2 * threads, dict + 1024 * 1024),
      // PPMd — symmetric, solid block data buffered
      (F.SevenZip, "ppmd") when dict > 0 =>
        ((dict + 2 * 1024 * 1024 + solidBlock) * threads, dict + solidBlock + 2 * 1024 * 1024),
      (_, "ppmd") when dict > 0 =>
        (dict + 2 * 1024 * 1024, dict + 2 * 1024 * 1024),
      // Deflate — light encoder, but 7z solid blocks still buffered
      (F.SevenZip, "deflate" or "deflate64" or "deflate+") =>
        ((16L * 1024 * 1024 + solidBlock) * threads, solidBlock + 256 * 1024),
      (_, "deflate" or "deflate64" or "deflate+") =>
        (16L * 1024 * 1024 * threads, 256 * 1024),
      // BZip2 — block-based, 7z solid blocks buffered
      (F.SevenZip, "bzip2") =>
        ((GetEffectiveBzip2Block() * 8 + solidBlock) * threads, solidBlock + GetEffectiveBzip2Block() * 5),
      (_, "bzip2") => (GetEffectiveBzip2Block() * 8, GetEffectiveBzip2Block() * 5),
      // Zstd
      (_, "zstd" or "zstd+") when dict > 0 =>
        (dict * 6 * threads, dict + 512 * 1024),
      // LZX
      (F.Cab, "lzx") when dict > 0 =>
        (dict * 10, dict + 512 * 1024),
      // RAR5 LZMA-like
      (F.Rar, _) when dict > 0 && method != "rar4" =>
        (dict * 12, dict + 1024 * 1024),
      // RAR4
      (F.Rar, "rar4") when dict > 0 =>
        (dict * 10, dict + 256 * 1024),
      // ACE
      (F.Ace, _) when dict > 0 =>
        (dict * 8, dict + 256 * 1024),
      // Store/Copy
      (_, "store" or "copy" or "none") => (0, 0),
      _ => (0, 0),
    };
  }

  private long GetEffectiveSolidBlock() {
    long solidBytes = ParseSolidSizeBytes(SelectedSolidSize);
    if (solidBytes == SmartSolidSentinel) return SolidBlockPlanner.DefaultMaxBlockSize;
    if (solidBytes <= 0) return 4L * 1024 * 1024 * 1024; // "Solid (single block)" — assume large
    if (solidBytes == 1) return 0; // "Non-solid" — no buffering
    return solidBytes;
  }

  private long GetEffectiveDictSize(string method) {
    long parsed = ParseDictSizeBytes(SelectedDictSize);
    if (parsed > 0) return parsed;
    return (_format, method) switch {
      (F.SevenZip, "lzma" or "lzma2") => 8L * 1024 * 1024,
      (F.SevenZip, "ppmd") => 16L * 1024 * 1024,
      (F.Zip, "lzma") => 8L * 1024 * 1024,
      (F.Zip, "ppmd") => 16L * 1024 * 1024,
      (F.Rar, "rar4") => 1024 * 1024,
      (F.Rar, _) => 128 * 1024,
      (F.Cab, "lzx") => 32 * 1024,
      (F.TarXz, _) => 8L * 1024 * 1024,
      _ => 0,
    };
  }

  private long GetEffectiveBzip2Block() {
    long parsed = ParseDictSizeBytes(SelectedDictSize);
    return parsed > 0 ? parsed : 900 * 1024;
  }

  private static long GetAvailableMemory() {
    try { return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
    catch { return 8L * 1024 * 1024 * 1024; }
  }

  private void UpdateMemoryEstimate() {
    OnPropertyChanged(nameof(MemoryEstimate));
    OnPropertyChanged(nameof(MemoryBrush));
    OnPropertyChanged(nameof(MemoryBackground));
    OnPropertyChanged(nameof(ShowMemoryEstimate));
  }

  // ── Hint text ─────────────────────────────────────────────────────

  public string HintText {
    get {
      string method = ResolveMethodName();
      return (_format, method) switch {
        // 7z
        (F.SevenZip, "lzma" or "lzma2") =>
          "LZMA2: Best for most data types. Dictionary controls how far back the compressor "
          + "searches for repeated patterns — larger = better ratio but more memory. "
          + "Word size controls match search depth (5-273), higher = slower but better compression.\n"
          + "Opens with: 7-Zip, WinRAR 5+, PeaZip, p7zip, NanaZip",
        (F.SevenZip, "ppmd") =>
          "PPMd: Prediction-based method, excels on text and source code. "
          + "Memory controls the model size. Word size sets context depth — "
          + "how many preceding bytes are used for prediction (2-32). Higher order = more memory, better ratio.\n"
          + "Opens with: 7-Zip, PeaZip, p7zip, NanaZip",
        (F.SevenZip, "deflate") =>
          "Deflate: The most widely compatible method. Fixed 32 KB dictionary. "
          + "Word size controls match length (3-258). Compatible with virtually all decompressors.\n"
          + "Opens with: 7-Zip, WinRAR 5+, PeaZip, p7zip, NanaZip",
        (F.SevenZip, "bzip2") =>
          "BZip2: Block-sorting algorithm, good for text. Block size controls the amount of data "
          + "sorted at once (100-900 KB). Larger blocks = better ratio, more memory.\n"
          + "Opens with: 7-Zip, PeaZip, p7zip, NanaZip",
        (F.SevenZip, "copy") =>
          "Copy: Stores files without any compression. Use for data that's already compressed "
          + "(images, video, other archives).\n"
          + "Opens with: 7-Zip, WinRAR 5+, PeaZip, p7zip, NanaZip",
        (F.SevenZip, _) =>
          "LZMA2 is recommended for most files. PPMd excels on text and source code. "
          + "Solid blocks group files together for better compression but require more memory.\n"
          + "Opens with: 7-Zip, WinRAR 5+, PeaZip, p7zip, NanaZip",

        // ZIP
        (F.Zip, "store") =>
          "Store: Files are stored verbatim without compression. Fastest but largest output. "
          + "Use for pre-compressed data like images, video, or other archives.\n"
          + "Opens with: Everything — Windows Explorer, 7-Zip, WinRAR, WinZip, macOS Finder, unzip",
        (F.Zip, "deflate" or "deflate+") =>
          "Deflate: The universal standard — supported by every ZIP extractor ever made. "
          + "Deflate+ uses Zopfli, which finds the optimal Deflate encoding (much slower but ~5% smaller).\n"
          + "Opens with: Everything — Windows Explorer, 7-Zip, WinRAR, WinZip, macOS Finder, unzip",
        (F.Zip, "deflate64") =>
          "Deflate64: Extended Deflate with 64 KB window instead of 32 KB. Slightly better ratio, "
          + "but not all extractors support it (7-Zip and WinRAR do, Windows built-in doesn't).\n"
          + "Opens with: 7-Zip, WinRAR, PeaZip, NanaZip (not Windows Explorer or macOS Finder)",
        (F.Zip, "lzma" or "lzma+") =>
          "LZMA: Excellent compression, much better than Deflate. Requires a modern extractor. "
          + "Dictionary controls memory and ratio — 8 MB is a good default.\n"
          + "Opens with: 7-Zip, PeaZip, NanaZip, p7zip (not WinRAR, WinZip, or Windows Explorer)",
        (F.Zip, "ppmd") =>
          "PPMd: Best for text and source code. Requires modern extractors. "
          + "Word size controls context order (2-16). Memory controls model size.\n"
          + "Opens with: 7-Zip, PeaZip, NanaZip, p7zip",
        (F.Zip, "bzip2") =>
          "BZip2: Block-sorting algorithm, good for text. Supported by most modern extractors.\n"
          + "Opens with: 7-Zip, PeaZip, NanaZip, p7zip (not WinRAR or Windows Explorer)",
        (F.Zip, "zstd") =>
          "Zstd: Fast modern compression with good ratio. Requires extractors supporting ZIP method 93. "
          + "Not yet widely supported.\n"
          + "Opens with: 7-Zip 21+, PeaZip, NanaZip (very limited support elsewhere)",
        (F.Zip, "shrink") =>
          "Shrink: Legacy LZW method from PKZIP 1.0 (1989). Use only for vintage compatibility. "
          + "Very limited compression ratio by modern standards.\n"
          + "Opens with: 7-Zip, PKZIP, Info-ZIP unzip, most modern extractors",
        (F.Zip, "implode") =>
          "Implode: Legacy sliding-window + Shannon-Fano from PKZIP 1.01. "
          + "Only for retro compatibility with DOS-era tools.\n"
          + "Opens with: 7-Zip, PKZIP, Info-ZIP unzip",
        (F.Zip, _) =>
          "Deflate is the most compatible. LZMA and Zstd offer significantly better compression "
          + "but require modern extractors. Per-file methods are possible when adding files later.\n"
          + "Opens with: Depends on method — Deflate works everywhere, LZMA/Zstd need 7-Zip or similar",

        // RAR
        (F.Rar, "rar4") =>
          "RAR4 (v2.9): Legacy format compatible with older WinRAR versions. "
          + "Smaller window sizes (32 KB to 4 MB). AES-128 encryption.\n"
          + "Opens with: WinRAR 2.9+, 7-Zip, PeaZip, unrar, The Unarchiver",
        (F.Rar, _) =>
          "RAR5: Modern format with strong AES-256 encryption, dictionaries up to 256 MB, "
          + "and solid block support. Recommended for all new archives.\n"
          + "Opens with: WinRAR 5+, 7-Zip 15+, PeaZip, NanaZip, unrar 5+",

        // CAB
        (F.Cab, "mszip") =>
          "MSZIP: Deflate with 32 KB dictionary reset per folder. Fast, good compatibility with Windows.\n"
          + "Opens with: Windows Explorer (built-in), 7-Zip, expand.exe, cabarc",
        (F.Cab, "lzx") =>
          "LZX: Best CAB compression. Window size controls dictionary (32 KB to 2 MB). "
          + "The standard method for Windows installers and .cab distribution.\n"
          + "Opens with: Windows Explorer (built-in), 7-Zip, expand.exe, cabarc",
        (F.Cab, "quantum") =>
          "Quantum: Adaptive range-coded method with levels 1-7. "
          + "Good ratio but rarely used. May be incompatible with some older extractors.\n"
          + "Opens with: 7-Zip, Windows Explorer (partial), cabarc",
        (F.Cab, "none" or "store") =>
          "Store: No compression. Fastest but largest output.\n"
          + "Opens with: Windows Explorer, 7-Zip, expand.exe, cabarc",
        (F.Cab, _) =>
          "MSZIP is fastest and most compatible. LZX gives best compression. Quantum is rare.\n"
          + "Opens with: Windows Explorer (built-in), 7-Zip, expand.exe",

        // LHA
        (F.Lzh, "lh0" or "store") =>
          "lh0: Store mode — no compression. For pre-compressed or trivially small files.\n"
          + "Opens with: 7-Zip, LHA/lha, PeaZip, The Unarchiver",
        (F.Lzh, "lh1") =>
          "lh1: Adaptive Huffman with 4 KB sliding window. The original LHA method from 1988.\n"
          + "Opens with: LHA/lha, 7-Zip (read), Unlha32.dll",
        (F.Lzh, "lh2") =>
          "lh2: Adaptive Huffman variant. Rarely used — lh5 or lh7 are generally preferred.\n"
          + "Opens with: LHA/lha (some builds), Unlha32.dll",
        (F.Lzh, "lh3") =>
          "lh3: Adaptive Huffman with 8 KB window. Transitional method.\n"
          + "Opens with: LHA/lha (some builds), Unlha32.dll",
        (F.Lzh, "lh4") =>
          "lh4: Static Huffman with 4 KB window. Older format, good compatibility with vintage tools.\n"
          + "Opens with: 7-Zip, LHA/lha, PeaZip, Unlha32.dll",
        (F.Lzh, "lh5") =>
          "lh5: Static Huffman with 8 KB window. The standard default method, widely compatible.\n"
          + "Opens with: 7-Zip, LHA/lha, PeaZip, The Unarchiver, Unlha32.dll",
        (F.Lzh, "lh6") =>
          "lh6: Static Huffman with 32 KB window. Better ratio than lh5, good compatibility.\n"
          + "Opens with: 7-Zip, LHA/lha, PeaZip, The Unarchiver",
        (F.Lzh, "lh7") =>
          "lh7: Static Huffman with 64 KB window. Best standard LHA compression.\n"
          + "Opens with: 7-Zip, LHA/lha, PeaZip, The Unarchiver",
        (F.Lzh, "lzs") =>
          "lzs: LZSS variant with 2 KB window. Legacy format for very old systems (LArc).\n"
          + "Opens with: LHA/lha, LArc, Unlha32.dll",
        (F.Lzh, "lz5") =>
          "lz5: LZSS variant with 4 KB window. Legacy format for old systems (LArc).\n"
          + "Opens with: LHA/lha, LArc, Unlha32.dll",
        (F.Lzh, "pm0") =>
          "pm0: PMA store mode — no compression. PMA-compatible container format.\n"
          + "Opens with: PMarc, Unlha32.dll",
        (F.Lzh, "pm1") =>
          "pm1: PMA with PPMd order-2 prediction. Good for text files.\n"
          + "Opens with: PMarc, Unlha32.dll",
        (F.Lzh, "pm2") =>
          "pm2: PMA with PPMd order-3 prediction. Best LHA method for text and source code.\n"
          + "Opens with: PMarc, Unlha32.dll",
        (F.Lzh, _) =>
          "lh5 (8 KB) is the standard default. lh7 (64 KB) gives best general compression. "
          + "pm2 uses PPMd and excels on text. lh1-lh3 are legacy adaptive Huffman.\n"
          + "Opens with: 7-Zip (lh0/lh4-lh7), LHA/lha (all methods), PeaZip",

        // ARJ
        (F.Arj, "store") =>
          "Store: No compression. Files stored verbatim.\n"
          + "Opens with: 7-Zip, ARJ/arj, PeaZip, The Unarchiver",
        (F.Arj, _) =>
          "Method 1 is best compression (LZ77 + Huffman). Method 3 is fastest. "
          + "Supports XOR garble encryption for basic password protection.\n"
          + "Opens with: 7-Zip, ARJ/arj, PeaZip, The Unarchiver",

        // ARC
        (F.Arc, "store" or "stored") =>
          "Store: No compression. Files stored verbatim.\n"
          + "Opens with: ARC/arc, FreeArc, PeaZip, The Unarchiver",
        (F.Arc, "pack" or "packed") =>
          "Packed: Run-length encoding. Simple, fast, good for data with repeated byte patterns.\n"
          + "Opens with: ARC/arc, FreeArc, PeaZip, The Unarchiver",
        (F.Arc, "squeeze" or "squeezed") =>
          "Squeeze: Huffman coding. Better than RLE for varied data.\n"
          + "Opens with: ARC/arc, FreeArc, PeaZip, The Unarchiver",
        (F.Arc, _) =>
          "Crunch (LZW): Best ARC compression using Lempel-Ziv-Welch. "
          + "Squash is a faster LZW variant. Pack uses RLE, Squeeze uses Huffman.\n"
          + "Opens with: ARC/arc, FreeArc, PeaZip, The Unarchiver",

        // Zoo
        (F.Zoo, "store") =>
          "Store: No compression. Files stored verbatim.\n"
          + "Opens with: zoo (CLI), PeaZip, The Unarchiver",
        (F.Zoo, _) =>
          "LZW: The standard Zoo compression method (adaptive 12-bit LZW). "
          + "Store is available for pre-compressed files.\n"
          + "Opens with: zoo (CLI), PeaZip, The Unarchiver",

        // ACE
        (F.Ace, "store") =>
          "Store: No compression. For already-compressed data.\n"
          + "Opens with: WinACE, 7-Zip (read), PeaZip (read), unace",
        (F.Ace, "ace20" or "ace2") =>
          "ACE 2.0: Enhanced compression with multimedia filters (audio, image prediction). "
          + "Better for mixed content. Supports Blowfish encryption and solid mode.\n"
          + "Opens with: WinACE, unace (partial)",
        (F.Ace, _) =>
          "ACE 1.0: LZ77 + Huffman with configurable dictionary (1 KB to 4 MB). "
          + "Supports Blowfish encryption and solid mode.\n"
          + "Opens with: WinACE, 7-Zip (read), PeaZip (read), unace",

        // SQX
        (F.Sqx, _) =>
          "SQX LZH: LZSS + multi-table Huffman. Dictionary 32 KB to 4 MB. "
          + "Supports AES-128 encryption, solid mode, and recovery records.\n"
          + "Opens with: SQX Archiver (sqx.exe), PeaZip",

        // Compound tar
        (F.TarGz, _) =>
          "TAR with Gzip (Deflate) outer compression. "
          + "Deflate+ uses Zopfli for optimal encoding (much slower but ~5% smaller).\n"
          + "Opens with: Everything — 7-Zip, WinRAR, tar/gzip, macOS Finder, PeaZip",
        (F.TarBz2, _) =>
          "TAR with BZip2 outer compression. Good for text-heavy archives. "
          + "Fixed 900 KB block size.\n"
          + "Opens with: 7-Zip, WinRAR, tar/bzip2, PeaZip, The Unarchiver",
        (F.TarXz, _) =>
          "TAR with XZ (LZMA2) outer compression. Excellent ratio for most data. "
          + "Larger dictionaries improve compression but need more memory.\n"
          + "Opens with: 7-Zip, WinRAR 5+, tar/xz, PeaZip, NanaZip",
        (F.TarZst, _) =>
          "TAR with Zstd outer compression. Fast modern algorithm with good ratio. "
          + "Zstd+ selects maximum compression level (slower, ~10-15% smaller).\n"
          + "Opens with: 7-Zip 21+, tar/zstd, PeaZip, NanaZip",

        // Amiga / retro archive formats
        (F.Dms, _) =>
          "DMS (Disk Masher System): Amiga disk image archiver (1989). "
          + "Compresses Amiga floppy disk images track by track.\n"
          + "Opens with: xDMS, WinUAE, FS-UAE, The Unarchiver",
        (F.LzxAmiga, _) =>
          "LZX: Amiga archiver (1995). LZ77+Huffman compression with sliding window.\n"
          + "Opens with: unlzx, The Unarchiver",
        (F.CompactPro, _) =>
          "Compact Pro: Classic Macintosh archiver (1990-1998). "
          + "Preserves Mac resource forks and Finder metadata.\n"
          + "Opens with: Compact Pro (classic Mac), The Unarchiver, Stuffit Expander",
        (F.Spark, _) =>
          "Spark: RISC OS / Acorn Archimedes archiver (1989). "
          + "ARC-based format with RISC OS file type extensions.\n"
          + "Opens with: SparkFS, !SparkPlug, David Pilling's tools",
        (F.Lbr, _) =>
          "LBR: CP/M Library format (1982). One of the earliest micro archive formats. "
          + "No compression — just a directory with 128-byte sector alignment.\n"
          + "Opens with: LU, LAR, NULU (CP/M tools)",
        (F.Uharc, _) =>
          "UHARC: Ultra-high compression archiver (1997). "
          + "Uses LZP (Lempel-Ziv Prediction) for excellent compression ratios.\n"
          + "Opens with: UHARC (uharc.exe), PeaZip",
        (F.Wad, _) =>
          "WAD: id Software game archive (1993). Container for Doom/Heretic/Hexen game data. "
          + "No compression — stores lumps (data entries) with names up to 8 characters.\n"
          + "Opens with: SLADE, Doom Builder, XWE, any Doom source port",

        // Stream formats
        _ when FormatDetector.IsStreamFormat(_format) =>
          "Single-file compression. Default settings are fine for most use cases.",
        _ =>
          "Default settings will work well for most files.",
      };
    }
  }

  // ── Populate all controls ───────────────────────────────────────────

  private void PopulateAll() {
    PopulateMethods();
    PopulateLevels();
    PopulateDictSizes();
    PopulateWordSizes();
    PopulateSolidSizes();
    PopulateThreads();
    PopulateEncryptionMethods();
  }

  private void PopulateMethods() {
    Methods = _format switch {
      F.Zip => [
        "Deflate (default)", "Store", "Deflate+  (Zopfli optimal)",
        "Deflate64", "BZip2", "LZMA", "LZMA+", "PPMd", "Zstd",
        "Shrink  (legacy LZW)", "Implode  (legacy)"],
      F.SevenZip => [
        "LZMA2 (default)", "LZMA", "LZMA2+  (best)", "LZMA+",
        "PPMd", "Deflate", "BZip2", "Copy  (store)"],
      F.Rar => [
        "RAR5 (default)", "RAR4  (legacy v2.9)"],
      F.Cab => [
        "MSZIP (default)", "LZX", "Quantum", "Store"],
      F.Lzh => [
        "lh5  (default, 8 KB)", "lh7  (64 KB)", "lh6  (32 KB)",
        "lh4  (4 KB)", "lh1  (adaptive Huffman 4 KB)",
        "lh2  (adaptive Huffman)", "lh3  (adaptive Huffman 8 KB)",
        "lh0  (store)", "lzs  (LZSS 2 KB)", "lz5  (LZSS 4 KB)",
        "pm0  (PMA store)", "pm1  (PMA PPMd order-2)", "pm2  (PMA PPMd order-3)",
        "pm2  (PPMd order-3)", "pm1  (PPMd order-2)", "pm0  (PMA store)"],
      F.Arj => [
        "Method 1 (default, best)", "Method 2", "Method 3  (fastest)", "Store"],
      F.Arc => [
        "Crunch  (default, LZW)", "Store", "Pack  (RLE)", "Squeeze  (Huffman)",
        "Squash  (fast LZW)"],
      F.Zoo => [
        "LZW (default)", "Store"],
      F.Ace => [
        "ACE 1.0 (default)", "ACE 2.0", "Store"],
      F.Sqx => [
        "LZH (default)"],
      F.Ha => ["HA (store)"],
      F.Dms => ["Store (default)", "RLE", "Quick  (LZ77)"],
      F.LzxAmiga => ["LZX (default)"],
      F.CompactPro => ["Store (default)"],
      F.Spark => ["Crunch  (default, LZW)", "Store"],
      F.Lbr => ["Store"],
      F.Uharc => ["LZP (default)"],
      F.Wad => ["Store"],
      F.TarGz => ["Deflate (default)", "Deflate+  (Zopfli optimal)"],
      F.TarBz2 => ["BZip2 (default)"],
      F.TarXz => ["LZMA2 (default)", "LZMA+", "LZMA2+"],
      F.TarZst => ["Zstd (default)", "Zstd+  (best)"],
      _ => ["Default"],
    };
    SelectedMethod = Methods.Length > 0 ? Methods[0] : "Default";
  }

  private void PopulateLevels() {
    string effectiveMethod = ResolveMethodName();
    LevelOptions = (_format, effectiveMethod) switch {
      // 7z — all levels 0-9
      (F.SevenZip, "ppmd") => [
        "5 — Normal (default)", "1 — Fastest", "2", "3 — Fast", "4",
        "5 — Normal", "6", "7 — Maximum", "8", "9 — Ultra"],
      (F.SevenZip, "copy") => ["0 — Store"],
      (F.SevenZip, _) => [
        "5 — Normal (default)", "0 — Store", "1 — Fastest", "2", "3 — Fast", "4",
        "5 — Normal", "6", "7 — Maximum", "8", "9 — Ultra"],
      // ZIP
      (F.Zip, "store") => ["0 — Store"],
      (F.Zip, "shrink" or "implode") => ["Default"],
      (F.Zip, "deflate" or "deflate+") => [
        "6 — Normal (default)", "1 — Fastest", "2", "3 — Fast", "4", "5",
        "6 — Normal", "7", "8", "9 — Best"],
      (F.Zip, _) => [
        "5 — Normal (default)", "1 — Fastest", "2", "3 — Fast", "4",
        "5 — Normal", "6", "7 — Maximum", "8", "9 — Ultra"],
      // RAR — 0-5
      (F.Rar, _) => [
        "3 — Normal (default)", "0 — Store", "1 — Fastest", "2 — Fast",
        "3 — Normal", "4 — Good", "5 — Best"],
      // CAB LZX — window sizes
      (F.Cab, "lzx") => [
        "15 — 32 KB (default)", "15 — 32 KB", "16 — 64 KB", "17 — 128 KB",
        "18 — 256 KB", "19 — 512 KB", "20 — 1 MB", "21 — 2 MB"],
      // CAB Quantum — 1-7
      (F.Cab, "quantum") => [
        "4 — Normal (default)", "1 — Fastest", "2", "3", "4 — Normal", "5", "6", "7 — Best"],
      (F.Cab, _) => ["Default"],
      // Compound tar
      (F.TarGz, _) => [
        "6 — Normal (default)", "1 — Fastest", "2", "3", "4", "5",
        "6 — Normal", "7", "8", "9 — Best"],
      (F.TarXz, _) => [
        "5 — Normal (default)", "1 — Fastest", "2", "3", "4",
        "5 — Normal", "6", "7", "8", "9 — Ultra"],
      (F.TarZst, _) => [
        "3 — Normal (default)", "1 — Fastest", "2", "3 — Normal", "4", "5",
        "6", "7", "8", "9 — High", "10", "11", "12", "13", "14", "15",
        "16", "17", "18", "19 — Ultra"],
      (F.TarBz2, _) => ["9 — Maximum (default)"],
      _ => ["Default"],
    };
    SelectedLevel = LevelOptions[0];
    OnPropertyChanged(nameof(LevelOptions));
    OnPropertyChanged(nameof(ShowLevel));
  }

  // 2^n and 2^n * 1.5 series matching 7-Zip
  private static string[] DictSizeSeries(long min, long max) {
    var list = new List<string>();
    for (long pow2 = min; pow2 <= max; pow2 *= 2) {
      list.Add(FormatByteSize(pow2));
      long half = pow2 + pow2 / 2; // 1.5x
      if (half <= max && half > pow2)
        list.Add(FormatByteSize(half));
    }
    return [.. list];
  }

  private void PopulateDictSizes() {
    string effectiveMethod = ResolveMethodName();

    // Set label
    DictSizeLabel = (_format, effectiveMethod) switch {
      (_, "ppmd") => "Memory:",
      (_, "bzip2") => "Block size:",
      _ => "Dictionary:",
    };
    OnPropertyChanged(nameof(DictSizeLabel));

    DictSizeOptions = (_format, effectiveMethod) switch {
      // 7z LZMA/LZMA2 — 64 KB to 1 GB (2^n and 2^n*1.5)
      (F.SevenZip, "lzma" or "lzma2") =>
        ["Default (8 MB)", .. DictSizeSeries(64 * 1024, 1L * 1024 * 1024 * 1024)],
      // 7z PPMd — model memory
      (F.SevenZip, "ppmd") =>
        ["Default (16 MB)", .. DictSizeSeries(1024 * 1024, 1L * 1024 * 1024 * 1024)],
      // 7z BZip2 — block size 100-900 KB
      (F.SevenZip, "bzip2") =>
        ["Default (900 KB)", "100 KB", "200 KB", "300 KB", "400 KB",
         "500 KB", "600 KB", "700 KB", "800 KB", "900 KB"],
      // 7z Deflate/Copy — no dict option
      (F.SevenZip, "deflate" or "copy") => ["Default"],

      // ZIP LZMA — 64 KB to 64 MB
      (F.Zip, "lzma" or "lzma+") =>
        ["Default (8 MB)", .. DictSizeSeries(64 * 1024, 256L * 1024 * 1024)],
      // ZIP PPMd — 1 MB to 256 MB
      (F.Zip, "ppmd") =>
        ["Default (16 MB)", .. DictSizeSeries(1024 * 1024, 256L * 1024 * 1024)],

      // RAR5 — 128 KB to 256 MB (2^n and 2^n*1.5)
      (F.Rar, "rar4") =>
        ["Default (1 MB)", "32 KB", "64 KB", "128 KB", "256 KB", "512 KB",
         "1 MB", "2 MB", "4 MB"],
      (F.Rar, _) =>
        ["Default (128 KB)", .. DictSizeSeries(128 * 1024, 256L * 1024 * 1024)],

      // ACE — 1 KB to 4 MB
      (F.Ace, "store") => ["Default"],
      (F.Ace, _) =>
        ["Default (32 KB)", .. DictSizeSeries(1024, 4L * 1024 * 1024)],

      // SQX — 32 KB to 4 MB
      (F.Sqx, _) =>
        ["Default (256 KB)", .. DictSizeSeries(32 * 1024, 4L * 1024 * 1024)],

      // Tar.XZ — 64 KB to 256 MB
      (F.TarXz, _) =>
        ["Default (8 MB)", .. DictSizeSeries(64 * 1024, 256L * 1024 * 1024)],

      _ => ["Default"],
    };
    SelectedDictSize = DictSizeOptions[0];
    OnPropertyChanged(nameof(DictSizeOptions));
    OnPropertyChanged(nameof(ShowDictSize));
  }

  // Word size values matching 7-Zip
  private static readonly string[] LzmaWordSizes = [
    "Default (32)", "5", "8", "12", "16", "24", "32", "48", "64", "96", "128", "192", "256", "273"];
  private static readonly string[] DeflateWordSizes = [
    "Default (32)", "3", "8", "12", "16", "24", "32", "48", "64", "96", "128", "192", "256", "258"];
  private static readonly string[] PpmdOrders7z = [
    "Default (6)", "2", "3", "4", "5", "6", "7", "8", "10", "12", "16", "24", "32"];
  private static readonly string[] PpmdOrdersZip = [
    "Default (8)", "2", "3", "4", "5", "6", "7", "8", "10", "12", "16"];

  private void PopulateWordSizes() {
    string effectiveMethod = ResolveMethodName();
    WordSizeOptions = (_format, effectiveMethod) switch {
      (F.SevenZip, "ppmd") => PpmdOrders7z,
      (F.SevenZip, "lzma" or "lzma2") => LzmaWordSizes,
      (F.SevenZip, "deflate") => DeflateWordSizes,
      (F.Zip, "ppmd") => PpmdOrdersZip,
      (F.Zip, "lzma" or "lzma+") => LzmaWordSizes,
      (F.Zip, "deflate" or "deflate+" or "deflate64") => DeflateWordSizes,
      (F.TarXz, _) => LzmaWordSizes,
      _ => ["Default"],
    };
    SelectedWordSize = WordSizeOptions[0];
    OnPropertyChanged(nameof(WordSizeOptions));
    OnPropertyChanged(nameof(ShowWordSize));
  }

  private void PopulateSolidSizes() {
    SolidSizeOptions = _format switch {
      F.SevenZip => [
        "Default (64 MB)", "Non-solid",
        "Smart (by file type)",
        "1 MB", "2 MB", "4 MB", "8 MB", "16 MB", "32 MB", "64 MB",
        "128 MB", "256 MB", "512 MB", "1 GB", "2 GB", "4 GB",
        "Solid (single block)"],
      F.Rar => [
        "Default (64 MB)", "Non-solid",
        "Smart (by file type)",
        "1 MB", "2 MB", "4 MB", "8 MB", "16 MB", "32 MB", "64 MB",
        "128 MB", "256 MB",
        "Solid (single block)"],
      F.Ace => [
        "Default (non-solid)", "Non-solid", "Solid (single block)"],
      F.Sqx => [
        "Default (non-solid)", "Non-solid", "Solid (single block)"],
      _ => [],
    };
    SelectedSolidSize = SolidSizeOptions.Length > 0 ? SolidSizeOptions[0] : "";
    OnPropertyChanged(nameof(SolidSizeOptions));
    OnPropertyChanged(nameof(ShowSolidSize));
  }

  private void PopulateThreads() {
    int cores = Environment.ProcessorCount;
    var opts = new List<string> { $"Auto ({cores} cores)" };
    for (int i = 1; i <= cores; i++)
      opts.Add(i.ToString());
    ThreadOptions = [.. opts];
    SelectedThreads = ThreadOptions[0];
  }

  private void PopulateEncryptionMethods() {
    if (_format == F.Zip) {
      EncryptionMethodOptions = ["AES-256 (strong)", "ZipCrypto (legacy)"];
      SelectedEncryptionMethod = EncryptionMethodOptions[0];
    }
    else {
      EncryptionMethodOptions = [];
      SelectedEncryptionMethod = "";
    }
  }

  private static string[] BuildSfxTargets() {
    string current = GetCurrentRid();
    return [
      $"Current platform ({current})",
      "win-x64", "win-x86", "win-arm64",
      "linux-x64", "linux-arm64",
      "osx-x64", "osx-arm64",
    ];
  }

  private static string GetCurrentRid() {
    var arch = RuntimeInformation.OSArchitecture switch {
      Architecture.X64 => "x64",
      Architecture.X86 => "x86",
      Architecture.Arm64 => "arm64",
      _ => "x64",
    };
    if (OperatingSystem.IsWindows()) return $"win-{arch}";
    if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
    return $"linux-{arch}";
  }

  private bool SupportsEncryption() => _format switch {
    F.SevenZip => true,  // all 7z codecs support AES-256 encryption (including Copy)
    F.Zip => true,       // all ZIP methods support AES-256 and ZipCrypto (including Store)
    F.Rar => true,       // RAR5: AES-256, RAR4: AES-128
    F.Arj => true,
    F.Ace => true,
    F.Sqx => true,
    _ => false,
  };

  // ── React to method change ──────────────────────────────────────────

  private void OnMethodChanged() {
    PopulateLevels();
    PopulateDictSizes();
    PopulateWordSizes();
    OnPropertyChanged(nameof(HintText));
    OnPropertyChanged(nameof(PasswordEnabled));
    UpdateMemoryEstimate();
  }

  // ── Convert to CompressionOptions ───────────────────────────────────

  internal CompressionOptions ToOptions() {
    var method = ResolveMethodSpec();
    int? level = ParseLeadingInt(SelectedLevel);
    int? wordSize = ParseLeadingInt(SelectedWordSize);
    int threads = ParseThreads();
    long dictSize = ParseDictSizeBytes(SelectedDictSize);
    long solidSize = ParseSolidSizeBytes(SelectedSolidSize);

    return new CompressionOptions {
      Method = method,
      Level = level,
      WordSize = wordSize,
      DictSize = dictSize,
      SolidSize = solidSize,
      Threads = threads,
      Password = string.IsNullOrEmpty(Password) ? null : Password,
      EncryptFilenames = EncryptFilenames,
      ZipEncryption = SelectedEncryptionMethod?.Contains("ZipCrypto") == true ? "zipcrypto" : null,
    };
  }

  // ── Parsing helpers ─────────────────────────────────────────────────

  private string ResolveMethodName() {
    if (string.IsNullOrEmpty(_selectedMethod)) return "default";
    var s = _selectedMethod.Trim();
    var end = s.IndexOfAny([' ', '(']);
    if (end > 0) s = s[..end];
    s = s.TrimEnd('+').ToLowerInvariant();
    return s switch {
      "default" or "n/a" or "auto" => "default",
      "lz+huffman" or "lz77+huffman" => "default",
      "lzh" or "ha" => "default",
      "method" => ResolveArjMethod(),
      _ => s,
    };
  }

  private string ResolveArjMethod() {
    if (_selectedMethod.Contains("Store", StringComparison.OrdinalIgnoreCase)) return "store";
    if (_selectedMethod.Contains('3')) return "3";
    if (_selectedMethod.Contains('2')) return "2";
    return "1";
  }

  private MethodSpec ResolveMethodSpec() {
    if (string.IsNullOrEmpty(_selectedMethod)) return MethodSpec.Default;
    var s = _selectedMethod.Trim();

    if (s.StartsWith("RAR5", StringComparison.OrdinalIgnoreCase)) return MethodSpec.Default;
    if (s.StartsWith("RAR4", StringComparison.OrdinalIgnoreCase)) return new MethodSpec("rar4", false);
    if (s.StartsWith("Method", StringComparison.OrdinalIgnoreCase)) {
      if (s.Contains("Store", StringComparison.OrdinalIgnoreCase)) return new MethodSpec("store", false);
      if (s.Contains('3')) return new MethodSpec("3", false);
      if (s.Contains('2')) return new MethodSpec("2", false);
      return new MethodSpec("1", false);
    }
    if (s.StartsWith("ACE 2", StringComparison.OrdinalIgnoreCase)) return new MethodSpec("ace20", false);
    if (s.StartsWith("ACE 1", StringComparison.OrdinalIgnoreCase)) return MethodSpec.Default;

    var end = s.IndexOfAny([' ', '(']);
    if (end > 0) s = s[..end];
    s = s.Trim();
    bool optimize = s.EndsWith('+');
    if (optimize) s = s[..^1];
    s = s.ToLowerInvariant();
    if (s is "default" or "n/a" or "lz+huffman" or "lz77+huffman" or "lzh" or "ha" or "rar5")
      return MethodSpec.Default;
    return new MethodSpec(s, optimize);
  }

  private static int? ParseLeadingInt(string? s) {
    if (string.IsNullOrWhiteSpace(s)) return null;
    s = s.Trim();
    if (s.StartsWith("Default", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("N/A", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("Auto", StringComparison.OrdinalIgnoreCase))
      return null;
    int i = 0;
    while (i < s.Length && char.IsDigit(s[i])) i++;
    if (i > 0 && int.TryParse(s[..i], out var val)) return val;
    return null;
  }

  private int ParseThreads() {
    if (string.IsNullOrWhiteSpace(SelectedThreads) ||
        SelectedThreads.StartsWith("Auto", StringComparison.OrdinalIgnoreCase))
      return Environment.ProcessorCount;
    return int.TryParse(SelectedThreads.Trim(), out var t) && t > 0 ? t : Environment.ProcessorCount;
  }

  private static long ParseDictSizeBytes(string? s) {
    if (string.IsNullOrWhiteSpace(s)) return 0;
    if (s.StartsWith("Default", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("N/A", StringComparison.OrdinalIgnoreCase))
      return 0;
    return ParseSizeString(s);
  }

  /// <summary>Sentinel value indicating smart solid blocking by file type.</summary>
  internal const long SmartSolidSentinel = -2;

  private static long ParseSolidSizeBytes(string? s) {
    if (string.IsNullOrWhiteSpace(s)) return SolidBlockPlanner.DefaultMaxBlockSize;
    if (s.StartsWith("Default", StringComparison.OrdinalIgnoreCase))
      return SolidBlockPlanner.DefaultMaxBlockSize;
    if (s.StartsWith("Smart", StringComparison.OrdinalIgnoreCase))
      return SmartSolidSentinel;
    if (s.StartsWith("Non-solid", StringComparison.OrdinalIgnoreCase))
      return 1;
    if (s.StartsWith("Solid", StringComparison.OrdinalIgnoreCase))
      return 0;
    return ParseSizeString(s);
  }

  private static long ParseSizeString(string s) {
    s = s.Trim().ToLowerInvariant();
    var paren = s.IndexOf('(');
    if (paren >= 0) s = s[..paren].Trim();
    var dash = s.IndexOf('—');
    if (dash >= 0) s = s[..dash].Trim();
    if (string.IsNullOrEmpty(s)) return 0;
    long mult = 1;
    if (s.EndsWith("kb")) { mult = 1024; s = s[..^2].Trim(); }
    else if (s.EndsWith("mb")) { mult = 1024 * 1024; s = s[..^2].Trim(); }
    else if (s.EndsWith("gb")) { mult = 1024L * 1024 * 1024; s = s[..^2].Trim(); }
    else if (s.EndsWith('k')) { mult = 1024; s = s[..^1].Trim(); }
    else if (s.EndsWith('m')) { mult = 1024 * 1024; s = s[..^1].Trim(); }
    else if (s.EndsWith('g')) { mult = 1024L * 1024 * 1024; s = s[..^1].Trim(); }
    if (double.TryParse(s, CultureInfo.InvariantCulture, out var dval))
      return (long)(dval * mult);
    return 0;
  }

  /// <summary>Formats byte count for display: "64 KB", "1.5 MB", "2 GB", etc.</summary>
  private static string FormatByteSize(long bytes) {
    if (bytes < 1024) return $"{bytes} B";
    if (bytes < 1024 * 1024) {
      long kb = bytes / 1024;
      return $"{kb} KB";
    }
    if (bytes < 1024L * 1024 * 1024) {
      double mb = bytes / (1024.0 * 1024);
      return mb == Math.Floor(mb) ? $"{(long)mb} MB" : $"{mb:0.#} MB";
    }
    double gb = bytes / (1024.0 * 1024 * 1024);
    return gb == Math.Floor(gb) ? $"{(long)gb} GB" : $"{gb:0.#} GB";
  }

  // ── INotifyPropertyChanged ──────────────────────────────────────────

  public event PropertyChangedEventHandler? PropertyChanged;
  private void OnPropertyChanged([CallerMemberName] string? name = null)
    => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

  private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null) {
    if (EqualityComparer<T>.Default.Equals(field, value)) return false;
    field = value;
    OnPropertyChanged(name);
    return true;
  }
}
