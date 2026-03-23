namespace Compression.Lib;

/// <summary>
/// Groups files into solid blocks by content similarity for better compression.
/// Files with similar extensions are grouped together, incompressible files are separated,
/// and blocks are capped at a configurable maximum size.
/// </summary>
internal static class SolidBlockPlanner {

  /// <summary>Default maximum solid block size (64 MB, matching WinRAR default).</summary>
  internal const long DefaultMaxBlockSize = 64L * 1024 * 1024;

  // Extension group indices (used by RecommendCodec)
  private const int GroupSourceCode = 0;
  private const int GroupMarkup = 1;
  private const int GroupText = 2;
  private const int GroupExecutables = 3;
  private const int GroupImages = 4;
  private const int GroupAudioVideo = 5;
  private const int GroupArchives = 6;
  private const int GroupData = 7;

  /// <summary>
  /// Extension groups ranked by type similarity.
  /// Files within the same group compress well together in a solid block.
  /// </summary>
  private static readonly string[][] ExtensionGroups = [
    // 0: Source code
    [".cs", ".java", ".c", ".cpp", ".h", ".hpp", ".py", ".js", ".ts", ".go", ".rs", ".rb",
     ".swift", ".kt", ".scala", ".lua", ".pl", ".r", ".m", ".mm", ".f", ".f90", ".asm", ".s"],
    // 1: Markup / config
    [".xml", ".html", ".htm", ".xhtml", ".svg", ".xaml", ".csproj", ".sln", ".slnx", ".props",
     ".targets", ".json", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".properties"],
    // 2: Text / docs
    [".txt", ".md", ".rst", ".tex", ".csv", ".tsv", ".log", ".rtf"],
    // 3: Executables / libraries
    [".exe", ".dll", ".so", ".dylib", ".sys", ".obj", ".o", ".lib", ".a", ".pdb",
     ".elf", ".bin", ".wasm"],
    // 4: Images (lossy — typically incompressible)
    [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".ico", ".tif", ".tiff"],
    // 5: Audio/video (typically incompressible)
    [".mp3", ".mp4", ".avi", ".mkv", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".webm",
     ".mov", ".wmv"],
    // 6: Archives (incompressible)
    [".zip", ".rar", ".7z", ".gz", ".bz2", ".xz", ".zst", ".lz4", ".br", ".lzma", ".tar"],
    // 7: Data / databases
    [".db", ".sqlite", ".mdb", ".dat", ".idx"],
  ];

  /// <summary>A block of files to be compressed together in one solid stream.</summary>
  internal sealed class SolidBlock {
    internal List<(ArchiveInput Input, byte[] Data)> Files { get; } = [];
    internal long TotalSize { get; private set; }
    internal bool IsIncompressible { get; init; }
    /// <summary>Extension group index (-1 for catch-all, -2 for incompressible).</summary>
    internal int GroupIndex { get; init; } = -1;

    internal void Add(ArchiveInput input, byte[] data) {
      Files.Add((input, data));
      TotalSize += data.Length;
    }
  }

  /// <summary>
  /// Recommends the optimal 7z codec for a solid block based on its content type.
  /// </summary>
  internal static FileFormat.SevenZip.SevenZipCodec RecommendCodec(SolidBlock block,
      FileFormat.SevenZip.SevenZipCodec defaultCodec) {
    if (block.IsIncompressible)
      return FileFormat.SevenZip.SevenZipCodec.Copy;
    return defaultCodec;
  }

  /// <summary>
  /// Recommends the optimal 7z filter for a solid block based on its content type.
  /// </summary>
  internal static FileFormat.SevenZip.SevenZipFilter RecommendFilter(SolidBlock block) {
    if (block.IsIncompressible)
      return FileFormat.SevenZip.SevenZipFilter.None;
    return block.GroupIndex switch {
      GroupExecutables => FileFormat.SevenZip.SevenZipFilter.BcjX86,
      _ => FileFormat.SevenZip.SevenZipFilter.None,
    };
  }

  /// <summary>
  /// Plans solid blocks from the given archive inputs.
  /// Files are grouped by content similarity (extension) and split at maxBlockSize boundaries.
  /// Incompressible files are placed into separate blocks.
  /// </summary>
  internal static List<SolidBlock> Plan(IReadOnlyList<ArchiveInput> inputs,
      long maxBlockSize = DefaultMaxBlockSize, HashSet<string>? incompressible = null) {
    var files = inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.FullPath)).ToList();
    if (files.Count == 0) return [];

    // Separate incompressible from compressible
    var compressibleFiles = new List<ArchiveInput>();
    var incompressibleFiles = new List<ArchiveInput>();

    foreach (var f in files) {
      if (incompressible != null && incompressible.Contains(f.FullPath))
        incompressibleFiles.Add(f);
      else
        compressibleFiles.Add(f);
    }

    var blocks = new List<SolidBlock>();

    // Group compressible files by extension similarity
    var grouped = GroupByExtension(compressibleFiles);
    foreach (var (groupIndex, group) in grouped)
      SplitIntoBlocks(blocks, group, maxBlockSize, isIncompressible: false, groupIndex);

    // Incompressible files go into their own blocks (will use Store/Copy)
    if (incompressibleFiles.Count > 0)
      SplitIntoBlocks(blocks, incompressibleFiles, maxBlockSize, isIncompressible: true, groupIndex: -2);

    return blocks;
  }

  /// <summary>
  /// Detects incompressible files from the input list using entropy analysis.
  /// Returns the set of full paths that appear incompressible.
  /// </summary>
  internal static HashSet<string> DetectIncompressible(IReadOnlyList<ArchiveInput> inputs) {
    var result = new HashSet<string>();
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.FullPath)) continue;
      if (EntropyDetector.IsIncompressible(input.FullPath))
        result.Add(input.FullPath);
    }
    return result;
  }

  /// <summary>
  /// Groups files by extension similarity, returning groups with their group index.
  /// </summary>
  private static List<(int GroupIndex, List<ArchiveInput> Files)> GroupByExtension(List<ArchiveInput> files) {
    var extToGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var g = 0; g < ExtensionGroups.Length; g++)
      foreach (var ext in ExtensionGroups[g])
        extToGroup[ext] = g;

    var buckets = new Dictionary<int, List<ArchiveInput>>();
    var catchAll = new List<ArchiveInput>();

    foreach (var f in files) {
      var ext = Path.GetExtension(f.EntryName);
      if (!string.IsNullOrEmpty(ext) && extToGroup.TryGetValue(ext, out var groupIdx)) {
        if (!buckets.TryGetValue(groupIdx, out var list)) {
          list = [];
          buckets[groupIdx] = list;
        }
        list.Add(f);
      }
      else {
        catchAll.Add(f);
      }
    }

    var result = new List<(int, List<ArchiveInput>)>();
    foreach (var key in buckets.Keys.OrderBy(k => k))
      result.Add((key, buckets[key]));
    if (catchAll.Count > 0)
      result.Add((-1, catchAll));
    return result;
  }

  /// <summary>
  /// Splits a list of files into blocks that don't exceed maxBlockSize.
  /// </summary>
  private static void SplitIntoBlocks(List<SolidBlock> blocks, List<ArchiveInput> files,
      long maxBlockSize, bool isIncompressible, int groupIndex) {
    var current = new SolidBlock { IsIncompressible = isIncompressible, GroupIndex = groupIndex };

    foreach (var f in files) {
      var data = File.ReadAllBytes(f.FullPath);

      if (current.Files.Count > 0 && current.TotalSize + data.Length > maxBlockSize) {
        blocks.Add(current);
        current = new SolidBlock { IsIncompressible = isIncompressible, GroupIndex = groupIndex };
      }

      current.Add(f, data);
    }

    if (current.Files.Count > 0)
      blocks.Add(current);
  }
}
