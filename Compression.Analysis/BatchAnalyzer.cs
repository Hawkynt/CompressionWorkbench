using System.Collections.Concurrent;
using Compression.Registry;

namespace Compression.Analysis;

/// <summary>
/// Analyzes all files in a directory, detecting their formats and producing aggregate statistics.
/// </summary>
public sealed class BatchAnalyzer {

  /// <summary>
  /// Result for a single file in the batch.
  /// </summary>
  public sealed class FileResult {
    /// <summary>Full path of the file.</summary>
    public required string Path { get; init; }

    /// <summary>File size in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>Detected format Id, or null if unknown.</summary>
    public string? FormatId { get; init; }

    /// <summary>Detected format display name, or null if unknown.</summary>
    public string? FormatName { get; init; }

    /// <summary>Detection confidence (0.0 to 1.0).</summary>
    public double Confidence { get; init; }
  }

  /// <summary>
  /// Aggregate batch analysis result.
  /// </summary>
  public sealed class BatchResult {
    /// <summary>Results for each analyzed file.</summary>
    public required List<FileResult> FileResults { get; init; }

    /// <summary>Files that could not be identified.</summary>
    public required List<string> UnknownFiles { get; init; }

    /// <summary>Format distribution: FormatId → count.</summary>
    public required Dictionary<string, int> FormatDistribution { get; init; }

    /// <summary>Total number of files analyzed.</summary>
    public int TotalFiles { get; init; }

    /// <summary>Total size of all files in bytes.</summary>
    public long TotalSize { get; init; }
  }

  /// <summary>Default per-file timeout for parallel analysis (30 seconds).</summary>
  private const int DefaultPerFileTimeoutMs = 30_000;

  /// <summary>
  /// Analyzes all files in the given directory.
  /// </summary>
  public BatchResult AnalyzeDirectory(string path, bool recursive = false) {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    var files = Directory.GetFiles(path, "*", searchOption);

    var results = new List<FileResult>();
    var unknown = new List<string>();
    var distribution = new Dictionary<string, int>();
    long totalSize = 0;

    foreach (var file in files) {
      var info = new FileInfo(file);
      totalSize += info.Length;

      var (formatId, formatName, confidence) = DetectFile(file);

      results.Add(new FileResult {
        Path = file,
        Size = info.Length,
        FormatId = formatId,
        FormatName = formatName,
        Confidence = confidence
      });

      if (formatId != null) {
        distribution.TryGetValue(formatId, out var count);
        distribution[formatId] = count + 1;
      } else {
        unknown.Add(file);
      }
    }

    return new BatchResult {
      FileResults = results,
      UnknownFiles = unknown,
      FormatDistribution = distribution,
      TotalFiles = files.Length,
      TotalSize = totalSize
    };
  }

  /// <summary>
  /// Analyzes all files in the given directory concurrently using Parallel.ForEachAsync.
  /// Each file is processed with a per-file timeout to prevent hangs on problematic files.
  /// </summary>
  /// <param name="path">Directory path to analyze.</param>
  /// <param name="recursive">Whether to recurse into subdirectories.</param>
  /// <param name="maxDegreeOfParallelism">Maximum number of concurrent file analyses. Defaults to processor count.</param>
  /// <param name="perFileTimeoutMs">Per-file analysis timeout in milliseconds. Defaults to 30 seconds.</param>
  /// <param name="ct">Cancellation token for overall cancellation.</param>
  /// <returns>Aggregate batch analysis result.</returns>
  public async Task<BatchResult> AnalyzeDirectoryAsync(
    string path,
    bool recursive = false,
    int maxDegreeOfParallelism = 0,
    int perFileTimeoutMs = DefaultPerFileTimeoutMs,
    CancellationToken ct = default
  ) {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    var files = Directory.GetFiles(path, "*", searchOption);

    var concurrentResults = new ConcurrentBag<FileResult>();
    long totalSize = 0;

    var parallelOptions = new ParallelOptions {
      MaxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount,
      CancellationToken = ct
    };

    await Parallel.ForEachAsync(files, parallelOptions, async (file, token) => {
      var info = new FileInfo(file);
      Interlocked.Add(ref totalSize, info.Length);

      using var fileCts = CancellationTokenSource.CreateLinkedTokenSource(token);
      fileCts.CancelAfter(perFileTimeoutMs);

      (string? formatId, string? formatName, double confidence) detection;
      try {
        detection = await Task.Run(() => DetectFile(file), fileCts.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException) {
        detection = (null, null, 0.0);
      }

      concurrentResults.Add(new FileResult {
        Path = file,
        Size = info.Length,
        FormatId = detection.formatId,
        FormatName = detection.formatName,
        Confidence = detection.confidence
      });
    }).ConfigureAwait(false);

    // Build aggregates from concurrent results
    var results = concurrentResults.ToList();
    var unknown = new List<string>();
    var distribution = new Dictionary<string, int>();

    foreach (var result in results) {
      if (result.FormatId != null) {
        distribution.TryGetValue(result.FormatId, out var count);
        distribution[result.FormatId] = count + 1;
      } else {
        unknown.Add(result.Path);
      }
    }

    return new BatchResult {
      FileResults = results,
      UnknownFiles = unknown,
      FormatDistribution = distribution,
      TotalFiles = files.Length,
      TotalSize = Interlocked.Read(ref totalSize)
    };
  }

  private static (string? formatId, string? formatName, double confidence) DetectFile(string filePath) {
    try {
      var header = new byte[4096];
      int headerLen;
      using (var fs = File.OpenRead(filePath)) {
        headerLen = fs.Read(header, 0, header.Length);
      }

      var span = header.AsSpan(0, headerLen);
      IFormatDescriptor? bestDesc = null;
      var bestConf = 0.0;

      // Try magic-based detection.
      foreach (var desc in FormatRegistry.All) {
        foreach (var sig in desc.MagicSignatures) {
          if (sig.Offset + sig.Bytes.Length > headerLen) continue;
          var match = true;
          for (var j = 0; j < sig.Bytes.Length; j++) {
            var mask = sig.Mask != null && j < sig.Mask.Length ? sig.Mask[j] : (byte)0xFF;
            if ((span[sig.Offset + j] & mask) != (sig.Bytes[j] & mask)) { match = false; break; }
          }
          if (match && sig.Confidence > bestConf) {
            bestConf = sig.Confidence;
            bestDesc = desc;
          }
        }
      }

      // Fall back to extension-based detection.
      if (bestDesc == null) {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (!string.IsNullOrEmpty(ext)) {
          foreach (var desc in FormatRegistry.All) {
            if (desc.Extensions.Contains(ext)) {
              bestDesc = desc;
              bestConf = 0.40;
              break;
            }
          }
        }
      }

      return bestDesc != null
        ? (bestDesc.Id, bestDesc.DisplayName, bestConf)
        : (null, null, 0.0);
    } catch {
      return (null, null, 0.0);
    }
  }
}
