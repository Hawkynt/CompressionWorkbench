#pragma warning disable CS1591
using Compression.Core.Statistics;
using Compression.Registry;

namespace FileFormat.SevenZip;

/// <summary>
/// Groups files into solid blocks by content similarity for better compression.
/// Files with similar extensions are grouped together, incompressible files are
/// separated, and blocks are capped at a configurable maximum size.
/// </summary>
/// <remarks>
/// Lives in FileFormat.SevenZip because solid-block planning is intrinsic to
/// 7z (no other format the toolkit ships uses solid blocks the same way).
/// </remarks>
public static class SolidBlockPlanner {

  /// <summary>Default maximum solid block size (64 MB, matching WinRAR default).</summary>
  public const long DefaultMaxBlockSize = 64L * 1024 * 1024;

  // Extension group indices (used by RecommendCodec)
  private const int GroupSourceCode = 0;
  private const int GroupMarkup = 1;
  private const int GroupText = 2;
  private const int GroupExecutables = 3;
  private const int GroupImages = 4;
  private const int GroupAudioVideo = 5;
  private const int GroupArchives = 6;
  private const int GroupData = 7;

  private static readonly string[][] ExtensionGroups = [
    [".cs", ".java", ".c", ".cpp", ".h", ".hpp", ".py", ".js", ".ts", ".go", ".rs", ".rb",
     ".swift", ".kt", ".scala", ".lua", ".pl", ".r", ".m", ".mm", ".f", ".f90", ".asm", ".s"],
    [".xml", ".html", ".htm", ".xhtml", ".svg", ".xaml", ".csproj", ".sln", ".slnx", ".props",
     ".targets", ".json", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".properties"],
    [".txt", ".md", ".rst", ".tex", ".csv", ".tsv", ".log", ".rtf"],
    [".exe", ".dll", ".so", ".dylib", ".sys", ".obj", ".o", ".lib", ".a", ".pdb",
     ".elf", ".bin", ".wasm"],
    [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".ico", ".tif", ".tiff"],
    [".mp3", ".mp4", ".avi", ".mkv", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".webm",
     ".mov", ".wmv"],
    [".zip", ".rar", ".7z", ".gz", ".bz2", ".xz", ".zst", ".lz4", ".br", ".lzma", ".tar"],
    [".db", ".sqlite", ".mdb", ".dat", ".idx"],
  ];

  /// <summary>A block of files to be compressed together in one solid stream.</summary>
  public sealed class SolidBlock {
    public List<(ArchiveInputInfo Input, byte[] Data)> Files { get; } = [];
    public long TotalSize { get; private set; }
    public bool IsIncompressible { get; init; }
    /// <summary>Extension group index (-1 for catch-all, -2 for incompressible).</summary>
    public int GroupIndex { get; init; } = -1;

    public void Add(ArchiveInputInfo input, byte[] data) {
      Files.Add((input, data));
      TotalSize += data.Length;
    }
  }

  /// <summary>Recommends the optimal 7z codec for a solid block based on content type.</summary>
  public static SevenZipCodec RecommendCodec(SolidBlock block, SevenZipCodec defaultCodec) {
    if (block.IsIncompressible) return SevenZipCodec.Copy;
    return defaultCodec;
  }

  /// <summary>Recommends the optimal 7z filter (e.g. BCJ for x86 binaries).</summary>
  public static SevenZipFilter RecommendFilter(SolidBlock block) {
    if (block.IsIncompressible) return SevenZipFilter.None;
    return block.GroupIndex switch {
      GroupExecutables => SevenZipFilter.BcjX86,
      _ => SevenZipFilter.None,
    };
  }

  /// <summary>
  /// Plans solid blocks from the given archive inputs. Files grouped by extension
  /// similarity, split at <paramref name="maxBlockSize"/> boundaries, with
  /// incompressible files segregated.
  /// </summary>
  public static List<SolidBlock> Plan(IReadOnlyList<ArchiveInputInfo> inputs,
      long maxBlockSize = DefaultMaxBlockSize, HashSet<string>? incompressible = null) {
    var files = inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.FullPath)).ToList();
    if (files.Count == 0) return [];

    var compressibleFiles = new List<ArchiveInputInfo>();
    var incompressibleFiles = new List<ArchiveInputInfo>();
    foreach (var f in files) {
      if (incompressible != null && incompressible.Contains(f.FullPath))
        incompressibleFiles.Add(f);
      else
        compressibleFiles.Add(f);
    }

    var blocks = new List<SolidBlock>();
    foreach (var (groupIndex, group) in GroupByExtension(compressibleFiles))
      SplitIntoBlocks(blocks, group, maxBlockSize, isIncompressible: false, groupIndex);
    if (incompressibleFiles.Count > 0)
      SplitIntoBlocks(blocks, incompressibleFiles, maxBlockSize, isIncompressible: true, groupIndex: -2);
    return blocks;
  }

  /// <summary>
  /// Detects incompressible files from the input list using entropy analysis.
  /// Returns the set of full paths that appear incompressible.
  /// </summary>
  public static HashSet<string> DetectIncompressible(IReadOnlyList<ArchiveInputInfo> inputs) {
    var result = new HashSet<string>();
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.FullPath)) continue;
      if (EntropyDetector.IsIncompressible(input.FullPath))
        result.Add(input.FullPath);
    }
    return result;
  }

  private static List<(int GroupIndex, List<ArchiveInputInfo> Files)> GroupByExtension(
      List<ArchiveInputInfo> files) {
    var extToGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    for (var g = 0; g < ExtensionGroups.Length; g++)
      foreach (var ext in ExtensionGroups[g])
        extToGroup[ext] = g;

    var buckets = new Dictionary<int, List<ArchiveInputInfo>>();
    var catchAll = new List<ArchiveInputInfo>();
    foreach (var f in files) {
      var ext = Path.GetExtension(f.ArchiveName);
      if (!string.IsNullOrEmpty(ext) && extToGroup.TryGetValue(ext, out var groupIdx)) {
        if (!buckets.TryGetValue(groupIdx, out var list)) {
          list = [];
          buckets[groupIdx] = list;
        }
        list.Add(f);
      } else {
        catchAll.Add(f);
      }
    }

    var result = new List<(int, List<ArchiveInputInfo>)>();
    foreach (var key in buckets.Keys.OrderBy(k => k))
      result.Add((key, buckets[key]));
    if (catchAll.Count > 0)
      result.Add((-1, catchAll));
    return result;
  }

  /// <summary>
  /// Plans solid blocks by statistical similarity of file contents rather than extension.
  /// Reads each file, computes a fingerprint, and groups similar-content files together.
  /// </summary>
  /// <param name="inputs">Files to plan into solid blocks.</param>
  /// <param name="maxBlockSize">Maximum total size of a single solid block.</param>
  /// <returns>Solid blocks grouped by content similarity.</returns>
  public static List<SolidBlock> PlanBySimilarity(
      IReadOnlyList<ArchiveInputInfo> inputs, long maxBlockSize = DefaultMaxBlockSize) {
    var files = inputs.Where(i => !i.IsDirectory && !string.IsNullOrEmpty(i.FullPath)).ToList();
    if (files.Count == 0) return [];

    // Read all file contents
    var contents = new byte[files.Count][];
    for (var i = 0; i < files.Count; i++)
      contents[i] = File.ReadAllBytes(files[i].FullPath);

    // Determine max groups: aim for ~DefaultMaxBlockSize per group
    var totalSize = contents.Sum(c => (long)c.Length);
    var maxGroups = Math.Max(1, (int)Math.Ceiling((double)totalSize / maxBlockSize));

    var groups = FileSimilarityGrouper.GroupBySimilarity(contents, maxGroups, maxBlockSize);

    var blocks = new List<SolidBlock>();
    foreach (var group in groups) {
      var block = new SolidBlock { GroupIndex = -1 };
      foreach (var idx in group)
        block.Add(files[idx], contents[idx]);
      blocks.Add(block);
    }

    return blocks;
  }

  private static void SplitIntoBlocks(List<SolidBlock> blocks, List<ArchiveInputInfo> files,
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
