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

  /// <summary>
  /// Store identical regular files once. Depending on the writer this is either a
  /// native hard link or an explicitly read-only shared-data alias; query
  /// <see cref="FilesystemOptimization.GetHardLinkDeduplicationSemantics"/> when the
  /// distinction matters to a UI or caller.
  /// </summary>
  public bool DeduplicateWithHardLinks { get; init; }

  /// <summary>Replace duplicate regular files with symbolic links to one canonical copy.</summary>
  public bool DeduplicateWithSymbolicLinks { get; init; }

  /// <summary>Enable the filesystem writer's transparent file compression, when available.</summary>
  public bool UseTransparentCompression { get; init; }

  /// <summary>
  /// Probe explicitly registered writer compression parameters and keep the smallest
  /// verified rebuild. No option is inferred merely from its name.
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
/// Writer-specific implementations may alternatively register through
/// <see cref="FilesystemOptimizationAdapters"/> when the format assembly owns the
/// necessary entry semantics.
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
  private static readonly string[] DisabledCompressionValues = [
    "off", "none", "false", "store", "stored", "uncompressed", "disabled"
  ];

  /// <summary>
  /// Returns the exact hard-link-style semantics implemented by this writer.
  /// Native <see cref="LayoutReclaim.HardLinks"/> wins when present; otherwise a
  /// registered read-only shared-data backend can expose the same user option without
  /// pretending that the underlying filesystem has native hard links.
  /// </summary>
  public static HardLinkDeduplicationSemantics GetHardLinkDeduplicationSemantics(object descriptor) {
    ArgumentNullException.ThrowIfNull(descriptor);
    if (descriptor is not ILayoutOptimizable layout)
      return HardLinkDeduplicationSemantics.None;
    if (layout.ReclaimSupport.HasFlag(LayoutReclaim.HardLinks))
      return HardLinkDeduplicationSemantics.Native;
    return FilesystemOptimizationAdapters.TryGetHardLinkDeduplicator(layout, out var semantics, out _)
      ? semantics
      : HardLinkDeduplicationSemantics.None;
  }

  /// <summary>Returns only transforms the current repository writer can actually perform.</summary>
  public static FilesystemOptimizationFeatures GetSupportedFeatures(object descriptor) {
    ArgumentNullException.ThrowIfNull(descriptor);

    var result = FilesystemOptimizationFeatures.None;
    if (descriptor is not ILayoutOptimizable layout)
      return result;

    if (layout.ReclaimSupport.HasFlag(LayoutReclaim.Sparse))
      result |= FilesystemOptimizationFeatures.SparseFiles;
    if (GetHardLinkDeduplicationSemantics(layout) != HardLinkDeduplicationSemantics.None)
      result |= FilesystemOptimizationFeatures.HardLinkDeduplication;
    if (layout is ISymbolicLinkDeduplicationLayout
        || FilesystemOptimizationAdapters.TryGetSymbolicLinkDeduplicator(layout, out _))
      result |= FilesystemOptimizationFeatures.SymbolicLinkDeduplication;

    if (FilesystemOptimizationAdapters.TryGetCompressionProfile(layout, out var profile)) {
      if (profile.TransparentCompression)
        result |= FilesystemOptimizationFeatures.TransparentCompression;
      if (BuildCompressionAxes(layout, profile).Any(a => a.Values.Count > 1))
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

    var linkSemantics = options.DeduplicateWithHardLinks
      ? GetHardLinkDeduplicationSemantics(layout)
      : HardLinkDeduplicationSemantics.None;
    FilesystemOptimizationAdapters.HardLinkDeduplicator? hardLinkRebuild = null;
    var hasRegisteredHardLinkBackend = options.DeduplicateWithHardLinks
      && FilesystemOptimizationAdapters.TryGetHardLinkDeduplicator(layout, out _, out hardLinkRebuild);

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
          DeduplicateWithLinks = options.DeduplicateWithHardLinks
            && linkSemantics == HardLinkDeduplicationSemantics.Native,
        };

        if (options.DeduplicateWithSymbolicLinks) {
          if (layout is ISymbolicLinkDeduplicationLayout direct)
            direct.RebuildWithSymbolicLinkDeduplication(input, candidate, rebuild);
          else if (FilesystemOptimizationAdapters.TryGetSymbolicLinkDeduplicator(layout, out var registered))
            registered(layout, input, candidate, rebuild);
          else
            throw new NotSupportedException("This filesystem writer does not support symbolic-link deduplication.");
        } else if (hasRegisteredHardLinkBackend) {
          hardLinkRebuild!(layout, input, candidate, rebuild);
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
        // One invalid writer/parameter combination is a rejected probe. Other
        // candidates remain useful, and the untouched input is the final fallback.
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
    if (options.DeduplicateWithHardLinks && options.DeduplicateWithSymbolicLinks)
      throw new ArgumentException("Hard-link and symbolic-link deduplication are alternative transforms; request only one.", nameof(options));

    var supported = GetSupportedFeatures(layout);
    Require(options.MakeSparse, FilesystemOptimizationFeatures.SparseFiles, "sparse files");
    Require(options.DeduplicateWithHardLinks, FilesystemOptimizationFeatures.HardLinkDeduplication, "hard-link/shared-data deduplication");
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
    if (!FilesystemOptimizationAdapters.TryGetCompressionProfile(layout, out var profile)) {
      yield return seed;
      yield break;
    }

    var axes = BuildCompressionAxes(layout, profile).ToArray();
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
    foreach (var axis in axes) {
      if (parameters.ContainsKey(axis.Key)) continue;
      if (!axis.Values.Any(IsDisabledCompressionValue)) continue;
      var enabled = axis.Values.FirstOrDefault(v => !IsDisabledCompressionValue(v));
      if (enabled != null) parameters[axis.Key] = enabled;
    }
  }

  private static bool IsDisabledCompressionValue(string value)
    => DisabledCompressionValues.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

  private static IEnumerable<Axis> BuildCompressionAxes(
    ILayoutOptimizable layout,
    FilesystemCompressionProfile profile) {
    var schema = layout as IFormatOptionsSchema;
    foreach (var parameter in profile.Parameters) {
      var descriptor = schema?.OptionsSchema.FirstOrDefault(o =>
        string.Equals(o.Key, parameter.Key, StringComparison.OrdinalIgnoreCase));
      IReadOnlyList<string>? values = parameter.Values is { Count: > 0 }
        ? parameter.Values
        : descriptor?.Kind switch {
          FormatOptionKind.Enum or FormatOptionKind.Integer when descriptor.AllowedValues is { Count: > 0 }
            => descriptor.AllowedValues,
          FormatOptionKind.Boolean => ["false", "true"],
          _ => null,
        };
      if (values is not { Count: > 0 }) continue;
      var defaultValue = descriptor?.Default;
      if (string.IsNullOrWhiteSpace(defaultValue) || !values.Contains(defaultValue, StringComparer.OrdinalIgnoreCase))
        defaultValue = values[0];
      yield return new Axis(parameter.Key, values, defaultValue);
    }
  }

  private sealed record Axis(string Key, IReadOnlyList<string> Values, string Default);
}
