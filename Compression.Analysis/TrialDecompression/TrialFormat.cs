using System.Text;
using Compression.Analysis.Statistics;
using Compression.Registry;

namespace Compression.Analysis.TrialDecompression;

/// <summary>
/// Attempts format-level decompression using the Compression.Lib format libraries.
/// Supports stream formats, archive formats, and magic-byte pre-detection.
/// </summary>
public sealed class TrialFormat : ITrialStrategy {
  /// <inheritdoc />
  public string Algorithm { get; }

  /// <inheritdoc />
  public TrialCategory Category { get; }

  private readonly Func<byte[], DecompressionAttempt> _handler;

  private TrialFormat(string algorithm, TrialCategory category, Func<byte[], DecompressionAttempt> handler) {
    Algorithm = algorithm;
    Category = category;
    _handler = handler;
  }

  /// <inheritdoc />
  public DecompressionAttempt TryDecompress(ReadOnlySpan<byte> data, int maxOutput, CancellationToken ct) {
    try {
      return _handler(data.ToArray());
    }
    catch (Exception ex) {
      return Fail(ex.Message);
    }
  }

  private DecompressionAttempt Fail(string error)
    => new(Algorithm, 0, -1, -1, false, error, null);

  /// <summary>Creates trial strategies for all registered stream formats that support decompression.</summary>
  public static IEnumerable<TrialFormat> CreateAll() {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    foreach (var desc in FormatRegistry.All) {
      if (desc.Category is not (FormatCategory.Stream or FormatCategory.CompoundTar))
        continue;
      if (!desc.Capabilities.HasFlag(FormatCapabilities.CanExtract))
        continue;

      var ops = FormatRegistry.GetStreamOps(desc.Id);
      if (ops == null)
        continue;

      var capturedOps = ops;
      var displayName = desc.DisplayName;
      yield return new(displayName, TrialCategory.Stream, dataArray => {
        using var input = new MemoryStream(dataArray, 0, dataArray.Length);
        using var output = new MemoryStream();
        capturedOps.Decompress(input, output);
        var result = output.ToArray();
        if (result.Length == 0)
          return new(displayName, 0, -1, -1, false, "Output empty", null);

        var entropy = BinaryStatistics.ComputeEntropy(result);
        return new(displayName, 0, result.Length, entropy, true, null, result);
      });
    }
  }

  /// <summary>Creates trial strategies for all registered archive formats that support listing and extraction.</summary>
  public static IEnumerable<TrialFormat> CreateArchiveTrials() {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    foreach (var desc in FormatRegistry.All) {
      if (desc.Category is not FormatCategory.Archive)
        continue;
      if (!desc.Capabilities.HasFlag(FormatCapabilities.CanList))
        continue;

      var ops = FormatRegistry.GetArchiveOps(desc.Id);
      if (ops == null)
        continue;

      var capturedOps = ops;
      var displayName = desc.DisplayName;
      var canExtract = desc.Capabilities.HasFlag(FormatCapabilities.CanExtract);

      var algorithmName = $"{displayName} (archive)";
      yield return new(algorithmName, TrialCategory.Archive, dataArray => {
        using var input = new MemoryStream(dataArray, 0, dataArray.Length);
        var entries = capturedOps.List(input, null);
        if (entries is not { Count: > 0 })
          return new(algorithmName, 0, -1, -1, false, "No entries found", null);

        // Try to extract first file via temp file (most reliable — handles all format quirks).
        byte[]? extractedData = null;
        string? extractedName = null;
        if (canExtract) {
          var firstFile = entries.FirstOrDefault(e => !e.IsDirectory && e.OriginalSize > 0);
          if (firstFile != null) {
            var tempArchive = Path.Combine(Path.GetTempPath(), "cwb_trial_" + Guid.NewGuid().ToString("N")[..8] + desc.DefaultExtension);
            var tempDir = tempArchive + "_out";
            try {
              File.WriteAllBytes(tempArchive, dataArray);
              Directory.CreateDirectory(tempDir);
              try {
                Compression.Lib.ArchiveOperations.Extract(tempArchive, tempDir, null, [firstFile.Name]);
              } catch {
                // Selective extract failed — try extracting all.
                try { Compression.Lib.ArchiveOperations.Extract(tempArchive, tempDir, null, null); } catch { }
              }
              // Find extracted file (may be nested in subdirectories).
              var candidates = Directory.Exists(tempDir)
                ? Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)
                : [];
              var match = candidates.FirstOrDefault(f => Path.GetFileName(f) == Path.GetFileName(firstFile.Name))
                          ?? candidates.FirstOrDefault();
              if (match != null) {
                extractedData = File.ReadAllBytes(match);
                extractedName = firstFile.Name;
              }
            } catch { /* extraction failed */ }
            finally {
              try { File.Delete(tempArchive); } catch { }
              try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
          }
        }

        // Build output: extracted file data (if available) OR formatted entry listing.
        if (extractedData is { Length: > 0 }) {
          var entropy = BinaryStatistics.ComputeEntropy(extractedData);
          return new(algorithmName, 0, extractedData.Length, entropy, true, null, extractedData);
        }

        // Show formatted entry listing with sizes and methods.
        var sb = new StringBuilder();
        var totalFiles = entries.Count(e => !e.IsDirectory);
        var totalDirs = entries.Count(e => e.IsDirectory);
        sb.AppendLine($"{displayName} archive: {totalFiles} files, {totalDirs} directories");
        sb.AppendLine();
        sb.AppendLine($"{"Name",-40} {"Size",12} {"Packed",12} {"Method",-10}");
        sb.AppendLine(new string('-', 78));
        long totalOrig = 0, totalComp = 0;
        foreach (var entry in entries) {
          if (entry.IsDirectory) continue;
          var orig = entry.OriginalSize >= 0 ? $"{entry.OriginalSize,12:N0}" : "           ?";
          var comp = entry.CompressedSize >= 0 ? $"{entry.CompressedSize,12:N0}" : "           ?";
          sb.AppendLine($"{entry.Name,-40} {orig} {comp} {entry.Method,-10}");
          if (entry.OriginalSize > 0) totalOrig += entry.OriginalSize;
          if (entry.CompressedSize > 0) totalComp += entry.CompressedSize;
        }
        sb.AppendLine(new string('-', 78));
        sb.AppendLine($"{"Total",-40} {totalOrig,12:N0} {totalComp,12:N0}");
        var textBytes = Encoding.UTF8.GetBytes(sb.ToString());
        return new(algorithmName, 0, (int)Math.Min(totalOrig, int.MaxValue), 0.5, true, null, textBytes);
      });
    }
  }

  /// <summary>
  /// Creates fast magic-byte detection strategies that check signatures without decompression.
  /// These run first and report high-confidence matches immediately.
  /// </summary>
  public static IEnumerable<TrialFormat> CreateMagicDetections() {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    foreach (var desc in FormatRegistry.All) {
      if (desc.MagicSignatures.Count == 0)
        continue;

      var signatures = desc.MagicSignatures;
      var displayName = desc.DisplayName;
      var category = desc.Category;
      var algorithmName = $"Detected: {displayName} (magic match)";

      yield return new(algorithmName, TrialCategory.Magic, dataArray => {
        foreach (var sig in signatures) {
          if (dataArray.Length < sig.Offset + sig.Bytes.Length)
            continue;

          var matched = true;
          for (var i = 0; i < sig.Bytes.Length; i++) {
            var dataByte = dataArray[sig.Offset + i];
            var expected = sig.Bytes[i];

            if (sig.Mask != null)
              dataByte = (byte)(dataByte & sig.Mask[i]);

            if (dataByte != expected) {
              matched = false;
              break;
            }
          }

          if (matched) {
            var info = $"Magic match: {displayName} ({category}) at offset {sig.Offset}, confidence {sig.Confidence:P0}";
            var outputBytes = Encoding.UTF8.GetBytes(info);
            // Report size=0, entropy=0 for detection-only results — very high confidence
            return new(algorithmName, 0, 0, 0.0, true, null, outputBytes);
          }
        }

        return new(algorithmName, 0, -1, -1, false, "No magic match", null);
      });
    }
  }
}
