namespace Compression.Registry;

/// <summary>Outcome of a staged maintenance operation.</summary>
/// <param name="OriginalSize">Length of the input, in bytes.</param>
/// <param name="NewSize">Length of what was written to the output, in bytes.</param>
/// <param name="Changed">False when the output is a copy of the input (nothing to gain, or already canonical).</param>
public sealed record MaintenanceResult(long OriginalSize, long NewSize, bool Changed);

/// <summary>
/// Runs the staged maintenance operations — compress, canonicalize, repack — and the
/// in-place directory sort through their capability interfaces, and holds every result
/// to the lossless-or-refuse rule before it is handed back.
/// </summary>
/// <remarks>
/// <para>A staged operation writes its candidate to scratch space, never to the output
/// directly. The candidate is then verified — the same <see cref="ArchiveSemanticManifest"/>
/// for a container, the same decoded payload for a single stream — and only then copied to
/// the output. A candidate that does not verify raises <see cref="NotSupportedException"/>;
/// one that verifies but gains nothing is discarded and the input is copied through, so
/// the output is always a valid file and never worse than the input.</para>
/// <para>Asking for an operation the target does not advertise is a
/// <see cref="NotSupportedException"/> too, raised before anything is read.</para>
/// </remarks>
public static class MaintenanceVerbs {

  /// <summary>Re-encodes with the best compression (<see cref="ICompressionOptimizable"/>); keeps the input when that is not smaller.</summary>
  /// <param name="target">The descriptor; must advertise <see cref="MaintenanceCapability.Compress"/>.</param>
  /// <param name="input">The source, readable and seekable.</param>
  /// <param name="output">Receives the smaller verified candidate, or a copy of the input.</param>
  /// <param name="password">Password for an encrypted source.</param>
  /// <param name="encoder">An encoder to use instead of the target's own
  /// <see cref="ICompressionOptimizable.OptimizeCompression"/> — a caller with a wider parameter
  /// search, say. Its candidate is verified exactly like the target's own.</param>
  public static MaintenanceResult Compress(object target, Stream input, Stream output, string? password = null,
      Action<Stream, Stream>? encoder = null) {
    var compressor = Require<ICompressionOptimizable>(target, "compress");
    if (!compressor.CanOptimizeCompression)
      throw new NotSupportedException($"{Describe(target)} cannot re-encode losslessly.");
    return Staged(target, input, output, password, "Compress", keep: static (candidate, original) => candidate < original,
      encoder ?? ((i, o) => compressor.OptimizeCompression(i, o, password)));
  }

  /// <summary>Rewrites into the canonical form (<see cref="IArchiveCanonicalizable"/>); the result is kept whenever it differs.</summary>
  public static MaintenanceResult Canonicalize(object target, Stream input, Stream output) {
    var canonicalizer = Require<IArchiveCanonicalizable>(target, "canonicalize");
    return Staged(target, input, output, null, "Canonicalize", keep: static (_, _) => true, canonicalizer.Canonicalize);
  }

  /// <summary>Repacks from the same entries (<see cref="IArchiveRepackable"/>); keeps the input when the repack is larger.</summary>
  public static MaintenanceResult Repack(object target, Stream input, Stream output, string? password = null) {
    var repacker = Require<IArchiveRepackable>(target, "repack");
    return Staged(target, input, output, password, "Repack", keep: static (candidate, original) => candidate <= original,
      (i, o) => repacker.Repack(i, o, password));
  }

  /// <summary>
  /// Lays the volume out again at the geometry in <paramref name="geometry"/>
  /// (<see cref="MaintenanceCapability.ChangeGeometry"/>). Only keys the format tags
  /// <see cref="FormatOptionDescriptor.IsAllocationGeometry"/> are accepted, and the result is
  /// kept only when its manifest matches the input's; the size may change, the contents may not.
  /// </summary>
  /// <exception cref="NotSupportedException">The format does not change geometry losslessly, a
  /// key is not a geometry key, or the relayout did not keep everything.</exception>
  public static MaintenanceResult ChangeGeometry(object target, Stream input, Stream output,
      IReadOnlyDictionary<string, string> geometry, string? password = null) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(geometry);
    var allowed = MaintenanceCapabilities.GeometryOptionsOf(target);
    if (allowed.Count == 0)
      throw new NotSupportedException(
        $"{Describe(target)} cannot change its geometry losslessly: laying it out again would keep names and bytes but not the rest of what it carries.");
    var keys = allowed.Select(static o => o.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var foreign = geometry.Keys.Where(k => !keys.Contains(k)).ToArray();
    if (foreign.Length > 0)
      throw new NotSupportedException(
        $"{string.Join(", ", foreign)} {(foreign.Length == 1 ? "is" : "are")} not allocation geometry for {Describe(target)}; "
        + $"a geometry change may set {string.Join(", ", keys.Order(StringComparer.OrdinalIgnoreCase))}.");
    var layout = (ILayoutOptimizable)target;
    return Staged(target, input, output, password, "Change geometry", keep: static (_, _) => true,
      (i, o) => layout.RebuildStreaming(i, o, new LayoutRebuildOptions { Parameters = geometry }));
  }

  /// <summary>Sorts every directory in place (<see cref="IFilesystemDirectoryOrderer"/>).</summary>
  public static void SortDirectoryEntries(object target, Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    Require<IFilesystemDirectoryOrderer>(target, "sort directory entries").SortDirectoryEntries(image);
  }

  private static T Require<T>(object target, string verb) where T : class {
    ArgumentNullException.ThrowIfNull(target);
    return target as T ?? throw new NotSupportedException(
      $"{Describe(target)} does not {verb}: it does not implement {typeof(T).Name}.");
  }

  private static string Describe(object target) => target is IFormatDescriptor d ? d.DisplayName : target.GetType().Name;

  private static MaintenanceResult Staged(
      object target, Stream input, Stream output, string? password, string operation,
      Func<long, long, bool> keep, Action<Stream, Stream> run) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException($"{operation} needs a readable, seekable input.", nameof(input));
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException($"{operation} needs a writable, seekable output.", nameof(output));

    var originalLength = input.Length;
    using var candidate = RebuildVerb.CreateScratchStream();
    input.Position = 0;
    run(input, candidate);
    candidate.Flush();
    Verify(target, input, candidate, password, operation);

    var changed = keep(candidate.Length, originalLength) && !SameBytes(input, candidate);
    var source = changed ? (Stream)candidate : input;
    source.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    source.CopyTo(output);
    output.Flush();
    output.Position = 0;
    input.Position = 0;
    return new MaintenanceResult(originalLength, output.Length, changed);
  }

  /// <summary>
  /// Holds a candidate to the input: the container's manifest where the format lists
  /// entries, the decoded payload where it is a single stream, both where it is both.
  /// </summary>
  private static void Verify(object target, Stream input, Stream candidate, string? password, string operation) {
    var verified = false;
    if (target is IArchiveFormatOperations ops) {
      ArchiveSemanticManifest before, after;
      try {
        before = ArchiveSemanticManifest.Capture(input, ops, password);
      } catch (Exception ex) when (ex is not NotSupportedException) {
        throw new NotSupportedException($"{operation}: the original cannot be read to verify the result ({ex.Message}).", ex);
      }
      try {
        after = ArchiveSemanticManifest.Capture(candidate, ops, password);
      } catch (Exception ex) when (ex is not NotSupportedException) {
        throw new NotSupportedException($"{operation} produced a file its own reader rejects ({ex.Message}); refused, nothing was changed.", ex);
      }
      before.RequireSameAs(after, operation);
      verified = true;
    }
    if (target is IStreamFormatOperations stream) {
      ContentPreservation.RequireSamePayload(stream, input, candidate);
      verified = true;
    }
    if (!verified)
      throw new NotSupportedException(
        $"{operation}: {Describe(target)} has no reader to verify the result against; refused rather than trusted.");
    input.Position = 0;
    candidate.Position = 0;
  }

  private static bool SameBytes(Stream a, Stream b) {
    if (a.Length != b.Length) return false;
    a.Position = 0;
    b.Position = 0;
    var x = new byte[64 * 1024];
    var y = new byte[64 * 1024];
    for (var left = a.Length; left > 0;) {
      var n = (int)Math.Min(left, x.Length);
      a.ReadExactly(x, 0, n);
      b.ReadExactly(y, 0, n);
      if (!x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n))) return false;
      left -= n;
    }
    return true;
  }
}
