using Compression.Registry;
namespace Compression.Lib;

/// <summary>
/// Composite maintenance verb: defragment, compress and shrink. Every stage is
/// optional and selected from the descriptor's real capabilities.
/// </summary>
public static class CompactOperation {
  public sealed record CompactResult(long OriginalSize, long NewSize, IReadOnlyList<string> StepsRun);

  public sealed class CompactOptions {
    public string? Password { get; init; }
    public Action<string>? Log { get; init; }
  }

  public static CompactResult Compact(string path, CompactOptions? options = null) {
    ArgumentException.ThrowIfNullOrEmpty(path);
    if (!File.Exists(path)) throw new FileNotFoundException("Container not found.", path);
    options ??= new CompactOptions();
    var log = options.Log ?? (_ => { });

    FormatRegistration.EnsureInitialized();
    var originalSize = new FileInfo(path).Length;
    var format = FormatDetector.Detect(path);
    var formatId = format.ToString();
    var descriptor = FormatRegistry.GetById(formatId);
    var ops = FormatRegistry.GetArchiveOps(formatId);
    var steps = new List<string>();

    if (OptimizationCapabilities.CanDefragmentExtents(descriptor)
        && ops is IArchiveDefragmentable defragmentable) {
      try {
        using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        defragmentable.Defragment(stream, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
        steps.Add("defragment");
        log("defragment: consolidated live data at the start.");
      } catch (Exception ex) {
        log($"defragment: skipped ({ex.GetType().Name}: {ex.Message}).");
      }
    }

    if (formatId is "DoubleSpace" or "DriveSpace" or "DriveSpace3") {
      if (descriptor != null) {
        try {
          var r = CvfOptimizer.Optimize(path, descriptor);
          steps.Add("compress");
          log($"compress: re-encoded via {r.MethodUsed}.");
        } catch (Exception ex) {
          log($"compress: skipped ({ex.GetType().Name}: {ex.Message}).");
        }
      }
    } else if (OptimizationCapabilities.CanCompress(descriptor)) {
      var tempOut = path + ".compact-compress.tmp";
      try {
        var r = ArchiveOperations.Compress(path, tempOut, options.Password);
        File.Move(tempOut, path, overwrite: true);
        steps.Add("compress");
        log($"compress: re-encoded {r.EntriesOptimized} entr(ies).");
      } catch (Exception ex) {
        log($"compress: skipped ({ex.GetType().Name}: {ex.Message}).");
      } finally {
        if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { }
      }
    }

    if (ops is IArchiveShrinkable shrinkable) {
      var tempOut = path + ".compact-shrink.tmp";
      try {
        long shrunkLen;
        using (var input = File.OpenRead(path))
        using (var output = File.Create(tempOut))
          shrinkable.Shrink(input, output);
        shrunkLen = new FileInfo(tempOut).Length;
        if (shrunkLen > 0 && shrunkLen < new FileInfo(path).Length) {
          File.Move(tempOut, path, overwrite: true);
          steps.Add("shrink");
          log($"shrink: reduced to {shrunkLen:N0} bytes.");
        } else {
          log("shrink: already compact (no reduction).");
        }
      } catch (Exception ex) {
        log($"shrink: skipped ({ex.GetType().Name}: {ex.Message}).");
      } finally {
        if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { }
      }
    }

    return new CompactResult(originalSize, new FileInfo(path).Length, steps);
  }

}
