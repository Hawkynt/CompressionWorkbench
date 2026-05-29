#pragma warning disable CS1591
using System.Security.Cryptography;
using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Strategy for selecting which file to keep when deduplicating.
/// </summary>
public enum DeduplicationStrategy {
  /// <summary>Keep the first occurrence (by listing order); link/remove the rest.</summary>
  KeepFirst,
  /// <summary>Keep the file at the shallowest directory depth; link/remove the rest.</summary>
  KeepLargestPath,
}

/// <summary>
/// A group of files that share identical content.
/// </summary>
public sealed record DuplicateGroup(byte[] ContentHash, long Size, IReadOnlyList<string> FileNames) {
  /// <summary>Total wasted bytes: (count - 1) * size.</summary>
  public long WastedBytes => (FileNames.Count - 1) * Size;
}

/// <summary>
/// Report from a deduplication analysis (dry-run).
/// </summary>
public sealed record DeduplicationReport(
  IReadOnlyList<DuplicateGroup> Groups,
  int TotalFiles,
  int UniqueFiles,
  int DuplicateFiles,
  long TotalSize,
  long WastedBytes,
  long PotentialSavings);

/// <summary>
/// Scans filesystem images for duplicate files and optionally deduplicates via rebuild.
/// </summary>
public static class DeduplicationScanner {

  /// <summary>
  /// Finds all groups of duplicate files in the given image.
  /// </summary>
  public static List<DuplicateGroup> FindDuplicates(string imagePath) {
    var entries = ArchiveOperations.List(imagePath, password: null);
    var fileEntries = entries.Where(e => !e.IsDirectory && e.OriginalSize > 0).ToList();

    // Group by size first (quick filter)
    var sizeGroups = fileEntries.GroupBy(e => e.OriginalSize).Where(g => g.Count() > 1);

    var hashGroups = new Dictionary<string, List<string>>();
    var hashSizes = new Dictionary<string, long>();

    foreach (var sg in sizeGroups) {
      foreach (var entry in sg) {
        try {
          var data = ArchiveOperations.ExtractEntry(imagePath, entry.Name, password: null);
          var hash = Convert.ToHexString(SHA256.HashData(data));
          if (!hashGroups.TryGetValue(hash, out var list)) {
            list = [];
            hashGroups[hash] = list;
            hashSizes[hash] = entry.OriginalSize;
          }
          list.Add(entry.Name);
        } catch {
          // Skip files that can't be extracted
        }
      }
    }

    return hashGroups
      .Where(kv => kv.Value.Count > 1)
      .Select(kv => new DuplicateGroup(
        Convert.FromHexString(kv.Key),
        hashSizes[kv.Key],
        kv.Value))
      .OrderByDescending(g => g.WastedBytes)
      .ToList();
  }

  /// <summary>
  /// Dry-run analysis: reports duplicates and potential savings without modifying the image.
  /// </summary>
  public static DeduplicationReport Analyze(string imagePath) {
    var entries = ArchiveOperations.List(imagePath, password: null);
    var totalFiles = entries.Count(e => !e.IsDirectory);
    var totalSize = entries.Where(e => !e.IsDirectory).Sum(e => e.OriginalSize);

    var groups = FindDuplicates(imagePath);
    var duplicateFiles = groups.Sum(g => g.FileNames.Count - 1);
    var wastedBytes = groups.Sum(g => g.WastedBytes);

    return new DeduplicationReport(
      Groups: groups,
      TotalFiles: totalFiles,
      UniqueFiles: totalFiles - duplicateFiles,
      DuplicateFiles: duplicateFiles,
      TotalSize: totalSize,
      WastedBytes: wastedBytes,
      PotentialSavings: wastedBytes);
  }

  /// <summary>
  /// Executes deduplication by rebuilding the image with only unique files.
  /// Returns bytes saved. Only works for formats that support IArchiveCreatable.
  /// </summary>
  public static long Execute(string imagePath, DeduplicationStrategy strategy) {
    var format = FormatDetector.Detect(imagePath);
    FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps(format.ToString());

    if (ops is not IArchiveCreatable)
      throw new NotSupportedException(
        $"Format {format} does not support creation. Deduplication can only report duplicates for this format.");

    var originalSize = new FileInfo(imagePath).Length;
    var groups = FindDuplicates(imagePath);
    if (groups.Count == 0)
      return 0; // nothing to deduplicate

    // Build the set of files to remove (duplicates, keeping the selected one)
    var filesToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var group in groups) {
      var keeper = SelectKeeper(group.FileNames, strategy);
      foreach (var file in group.FileNames) {
        if (!file.Equals(keeper, StringComparison.OrdinalIgnoreCase))
          filesToRemove.Add(file);
      }
    }

    // Extract all unique files to temp, rebuild the image
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb_dedup_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tempDir);
      ArchiveOperations.Extract(imagePath, tempDir, password: null, files: null);

      // Remove duplicate files from temp
      foreach (var rel in filesToRemove) {
        var path = Path.Combine(tempDir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path))
          File.Delete(path);
      }

      // Rebuild
      var inputs = new List<ArchiveInput>();
      foreach (var dir in Directory.GetDirectories(tempDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tempDir, dir).Replace('\\', '/');
        inputs.Add(new ArchiveInput("", rel + "/"));
      }
      foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)) {
        var rel = Path.GetRelativePath(tempDir, file).Replace('\\', '/');
        inputs.Add(new ArchiveInput(file, rel));
      }

      ArchiveOperations.Create(imagePath, inputs, new CompressionOptions(), format);
    } finally {
      if (Directory.Exists(tempDir))
        Directory.Delete(tempDir, true);
    }

    var newSize = new FileInfo(imagePath).Length;
    return Math.Max(0, originalSize - newSize);
  }

  private static string SelectKeeper(IReadOnlyList<string> fileNames, DeduplicationStrategy strategy) {
    return strategy switch {
      DeduplicationStrategy.KeepFirst => fileNames[0],
      DeduplicationStrategy.KeepLargestPath => fileNames
        .OrderBy(f => f.Count(c => c == '/' || c == '\\'))
        .ThenBy(f => f.Length)
        .First(),
      _ => fileNames[0],
    };
  }
}
