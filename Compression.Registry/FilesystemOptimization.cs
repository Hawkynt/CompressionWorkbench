namespace Compression.Registry;

/// <summary>
/// Optional space-saving transforms that a filesystem layout optimizer can expose.
/// The flags describe writer capabilities, not merely features of the on-disk format.
/// A filesystem may support symbolic links or compression in its specification while
/// still omitting the corresponding flag until this repository's writer can emit it.
/// </summary>
[Flags]
public enum FilesystemOptimizationFeatures {
  None = 0,
  SparseFiles = 1 << 0,
  HardLinkDeduplication = 1 << 1,
  SymbolicLinkDeduplication = 1 << 2,
  TransparentCompression = 1 << 3,
  CompressionParameterSearch = 1 << 4,
}

/// <summary>
/// Options shared by filesystem optimize and shrink operations.
/// All transforms are opt-in because hard/symbolic-link deduplication changes
/// write semantics even though the bytes observed through the original names stay
/// identical at the time of the rebuild.
/// </summary>
public sealed class FilesystemOptimizationOptions {
  /// <summary>Replace all-zero allocation units with filesystem holes.</summary>
  public bool MakeSparse { get; init; }

  /// <summary>Store identical regular files once and expose the other names as hard links.</summary>
  public bool DeduplicateWithHardLinks { get; init; }

  /// <summary>Replace duplicate regular files with symbolic links to one canonical copy.</summary>
  public bool DeduplicateWithSymbolicLinks { get; init; }

  /// <summary>Enable the filesystem writer's transparent file compression, when available.</summary>
  public bool UseTransparentCompression { get; init; }

  /// <summary>
  /// Probe the finite writer option schema and keep the smallest verified rebuild.
  /// This includes compression method/level knobs and other encoding/layout parameters
  /// whose values the descriptor explicitly declares as safe to try.
  /// </summary>
  public bool TryCompressionParameters { get; init; }

  /// <summary>Maximum number of parameter combinations attempted by one optimize/shrink pass.</summary>
  public int MaxCompressionProbes { get; init; } = 64;

  /// <summary>Explicit format-specific options. These seed every probe and win over auto-selected values.</summary>
  public IReadOnlyDictionary<string, string>? FormatSpecific { get; init; }

  /// <summary>Optional progress callback: completed probes, total planned probes.</summary>
  public Action<int, int>? OnProbeProgress { get; init; }

  internal bool RequestsTransform
    => MakeSparse || DeduplicateWithHardLinks || DeduplicateWithSymbolicLinks
       || UseTransparentCompression || TryCompressionParameters
       || FormatSpecific is { Count: > 0 };
}

/// <summary>
/// Optional capability for writers that can deliberately replace duplicate regular
/// files with symbolic links. It is separate from merely being able to read symlinks:
/// optimize must not claim this transform until the writer can create and round-trip it.
/// </summary>
public interface ISymbolicLinkDeduplicationLayout {
  void RebuildWithSymbolicLinkDeduplication(
    Stream source,
    Stream target,
    LayoutRebuildOptions options);
}

/// <summary>
/// Shared implementation behind filesystem optimize and option-aware shrink.
/// Candidates are always staged and only the smallest verified representation is
/// emitted; when no candidate improves on the input, the input is copied through.
/// </summary>
public static class FilesystemOptimization {
  private static readonly string[] CompressionKeyHints = [
    "Compression", "Compress", "Method", "Level", "Dictionary", "Window", "BlockSize"
  ];

  private static readonly string[] DisabledCompressionValues = [
    "off", "none", "false", "store", "stored", "uncompressed", "disabled"
  ];

  /// <summary>Returns only transforms the current repository writer can actually perform.</summary>
  public static FilesystemOptimizationFeatures GetSupportedFeatures(object descriptor) {
    ArgumentNullException.ThrowIfNull(descriptor);

    var result = FilesystemOptimizationFeatures.None;
    if (descriptor is ILayoutOptimizable layout) {
      if (layout.ReclaimSupport.HasFlag(LayoutReclaim.Sparse))
        result |= FilesystemOptimizationFeatures.SparseFiles;
      if (layout.ReclaimSupport.HasFlag(LayoutReclaim.HardLinks))
        result |= FilesystemOptimizationFeatures.HardLinkDeduplication;
    }

    if (descriptor is ISymbolicLinkDeduplicationLayout)
      result |= FilesystemOptimizationFeatures.SymbolicLinkDeduplication;

    if (descriptor is IFormatOptionsSchema schema) {
      var axes = SearchAxes(schema).ToArray();
      if (axes.Any(IsTransparentCompressionAxis))
        result |= FilesystemOptimizationFeatures.TransparentCompression;
      if (axes.Any(a => a.Values.Count > 1))
        result |= FilesystemOptimizationFeatures.CompressionParameterSearch;
    }

    return result;
  }

  /// <summary>
  /// Rebuilds a layout with the requested transforms and parameter search, emitting
  /// the input unchanged when no verified candidate is smaller.
  /// </summary>
  public static void Optimize(
    ILayoutOptimizable layout,
    Stream input,
    Stream output,
    FilesystemOptimizationOptions? options = null) {
    ArgumentNullException.ThrowIfNull(layout);
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    options ??= new FilesystemOptimizationOptions();

    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("Filesystem optimization requires a readable, seekable input stream.", nameof(input));
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("Filesystem optimization requires a writable, seekable output stream.", nameof(output));

    ValidateRequested(layout, options);

    var probes = BuildProbeParameters(layout, options).ToArray();
    if (probes.Length == 0)
      probes = [CopyParameters(options.FormatSpecific)];

    using var best = RebuildVerb.CreateScratchStream();
    var bestLength = input.Length;
    var haveBest = false;

    for (var index = 0; index < probes.Length; ++index) {
      options.OnProbeProgress?.Invoke(index, probes.Length);
      using var candidate = RebuildVerb.CreateScratchStream();
      try {
        input.Position = 0;
        var rebuild = new LayoutRebuildOptions {
          Parameters = probes[index],
          MakeSparse = options.MakeSparse,
          DeduplicateWithLinks = options.DeduplicateWithHardLinks,
        };

        if (options.DeduplicateWithSymbolicLinks) {
          ((ISymbolicLinkDeduplicationLayout)layout)
            .RebuildWithSymbolicLinkDeduplication(input, candidate, rebuild);
        } else {
          layout.RebuildStreaming(input, candidate, rebuild);
        }

        if (candidate.Length <= 0 || candidate.Length >= bestLength)
          continue;

        best.SetLength(0);
        candidate.Position = 0;
        candidate.CopyTo(best);
        best.Flush();
        bestLength = candidate.Length;
        haveBest = true;
      } catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or ArgumentException) {
        // One invalid writer/schema combination is a rejected probe. Other candidates
        // remain useful, and the untouched input is always the final fallback.
      }
    }

    options.OnProbeProgress?.Invoke(probes.Length, probes.Length);
    output.Position = 0;
    output.SetLength(0);
    if (haveBest) {
      best.Position = 0;
      best.CopyTo(output);
    } else {
      input.Position = 0;
      input.CopyTo(output);
    }
  }

  /// <summary>Throws before writing when a caller asks for a transform the writer does not advertise.</summary>
  public static void ValidateRequested(ILayoutOptimizable layout, FilesystemOptimizationOptions options) {
    ArgumentNullException.ThrowIfNull(layout);
    ArgumentNullException.ThrowIfNull(options);
    var supported = GetSupportedFeatures(layout);

    Require(options.MakeSparse, FilesystemOptimizationFeatures.SparseFiles, "sparse files");
    Require(options.DeduplicateWithHardLinks, FilesystemOptimizationFeatures.HardLinkDeduplication, "hard-link deduplication");
    Require(options.DeduplicateWithSymbolicLinks, FilesystemOptimizationFeatures.SymbolicLinkDeduplication, "symbolic-link deduplication");
    Require(options.UseTransparentCompression, FilesystemOptimizationFeatures.TransparentCompression, "transparent compression");
    Require(options.TryCompressionParameters, FilesystemOptimizationFeatures.CompressionParameterSearch, "compression parameter search");
    return;

    void Require(bool requested, FilesystemOptimizationFeatures feature, string name) {
      if (requested && !supported.HasFlag(feature))
        throw new NotSupportedException($"This filesystem writer does not support {name}.");
    }
  }

  private static IEnumerable<IReadOnlyDictionary<string, string>> BuildProbeParameters(
    ILayoutOptimizable layout,
    FilesystemOptimizationOptions options) {
    var seed = CopyParameters(options.FormatSpecific);
    if (layout is not IFormatOptionsSchema schema) {
      yield return seed;
      yield break;
    }

    var axes = SearchAxes(schema).ToArray();
    if (options.UseTransparentCompression)
      EnableTransparentCompression(seed, axes);

    if (!options.TryCompressionParameters || axes.Length == 0) {
      yield return seed;
      yield break;
    }

    var maxProbes = Math.Clamp(options.MaxCompressionProbes, 1, 4096);
    var variableAxes = axes
      .Where(a => a.Values.Count > 1 && !seed.ContainsKey(a.Key))
      .ToArray();
    if (variableAxes.Length == 0) {
      yield return seed;
      yield break;
    }

    // First probe the seeded/default configuration, then walk one axis at a time.
    // This bounded coordinate sweep avoids a combinatorial explosion on filesystems
    // exposing geometry plus compression choices, while still trying every declared
    // value when the budget permits.
    var current = new Dictionary<string, string>(seed, StringComparer.OrdinalIgnoreCase);
    foreach (var axis in variableAxes)
      current.TryAdd(axis.Key, axis.Default);
    yield return new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);

    var emitted = 1;
    foreach (var axis in variableAxes) {
      foreach (var value in axis.Values) {
        if (emitted >= maxProbes) yield break;
        if (string.Equals(current[axis.Key], value, StringComparison.OrdinalIgnoreCase)) continue;
        var trial = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase) {
          [axis.Key] = value,
        };
        ++emitted;
        yield return trial;
      }
    }
  }

  private static Dictionary<string, string> CopyParameters(IReadOnlyDictionary<string, string>? source) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (source != null)
      foreach (var pair in source)
        result[pair.Key] = pair.Value;
    return result;
  }

  private static void EnableTransparentCompression(Dictionary<string, string> parameters, IReadOnlyList<Axis> axes) {
    foreach (var axis in axes.Where(IsTransparentCompressionAxis)) {
      if (parameters.ContainsKey(axis.Key)) continue;
      var enabled = axis.Values.FirstOrDefault(v => !IsDisabledCompressionValue(v));
      if (enabled != null) parameters[axis.Key] = enabled;
    }
  }

  private static bool IsTransparentCompressionAxis(Axis axis) {
    if (!axis.Key.Contains("compress", StringComparison.OrdinalIgnoreCase)) return false;
    return axis.Values.Any(v => !IsDisabledCompressionValue(v))
      && axis.Values.Any(IsDisabledCompressionValue);
  }

  private static bool IsDisabledCompressionValue(string value)
    => DisabledCompressionValues.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

  private static IEnumerable<Axis> SearchAxes(IFormatOptionsSchema schema) {
    foreach (var option in schema.OptionsSchema) {
      if (!CompressionKeyHints.Any(h => option.Key.Contains(h, StringComparison.OrdinalIgnoreCase)))
        continue;

      IReadOnlyList<string>? values = option.Kind switch {
        FormatOptionKind.Enum or FormatOptionKind.Integer when option.AllowedValues is { Count: > 0 }
          => option.AllowedValues,
        FormatOptionKind.Boolean => ["false", "true"],
        _ => null,
      };
      if (values is { Count: > 0 })
        yield return new Axis(option.Key, values, option.Default);
    }
  }

  private sealed record Axis(string Key, IReadOnlyList<string> Values, string Default);
}
