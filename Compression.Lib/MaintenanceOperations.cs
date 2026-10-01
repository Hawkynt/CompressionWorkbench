using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// File-level entry points for the maintenance operations split by effect — compress,
/// canonicalize, repack, sort directory entries — plus the query a shell or CLI uses to
/// decide which of them to offer for a file. Each one detects the format, asks
/// <see cref="MaintenanceCapabilities"/> whether the format offers the operation, and runs
/// it through <see cref="MaintenanceVerbs"/>, which verifies the result before it replaces
/// anything.
/// </summary>
/// <remarks>
/// Every operation is lossless or refuses: a format that does not offer it, or a result that
/// would not keep every name, byte, timestamp and container property the format's reader
/// reports, raises <see cref="NotSupportedException"/> and leaves the files on disk exactly
/// as they were. Staged results are committed with <see cref="AtomicFileWriter"/>, so the
/// output may be the input itself.
/// </remarks>
public static class MaintenanceOperations {

  /// <summary>Which maintenance operations, defragmentation modes and geometry keys the format of <paramref name="path"/> offers.</summary>
  /// <returns>The profile, or one with no operations when the format is not recognised.</returns>
  public static MaintenanceProfile Describe(string path) {
    var (id, descriptor) = Resolve(path);
    return descriptor is null
      ? new MaintenanceProfile(id, MaintenanceCapability.None, DefragFeature.None, [])
      : MaintenanceCapabilities.Describe(descriptor);
  }

  /// <summary>
  /// Re-encodes <paramref name="inputPath"/> with the format's best compression into
  /// <paramref name="outputPath"/> (which may be the same file). The original bytes are
  /// written instead when nothing smaller verifies.
  /// </summary>
  /// <exception cref="NotSupportedException">The format does not compress losslessly, or the result did not verify.</exception>
  public static MaintenanceResult Compress(string inputPath, string outputPath, string? password = null) {
    var target = Target(inputPath, MaintenanceCapability.Compress, "compress");
    return Staged(inputPath, outputPath, (input, output) =>
      MaintenanceVerbs.Compress(target, input, output, password, SchemaSearch(target)));
  }

  /// <summary>Writes the canonical representation of <paramref name="inputPath"/> to <paramref name="outputPath"/>.</summary>
  /// <exception cref="NotSupportedException">The format has no canonical form, or the result did not verify.</exception>
  public static MaintenanceResult Canonicalize(string inputPath, string outputPath) {
    var target = Target(inputPath, MaintenanceCapability.Canonicalize, "canonicalize");
    return Staged(inputPath, outputPath, (input, output) => MaintenanceVerbs.Canonicalize(target, input, output));
  }

  /// <summary>Repacks <paramref name="inputPath"/> from its own entries into <paramref name="outputPath"/>, dropping dead space.</summary>
  /// <exception cref="NotSupportedException">The format does not repack losslessly, or the result did not verify.</exception>
  public static MaintenanceResult Repack(string inputPath, string outputPath, string? password = null) {
    var target = Target(inputPath, MaintenanceCapability.Repack, "repack");
    return Staged(inputPath, outputPath, (input, output) => MaintenanceVerbs.Repack(target, input, output, password));
  }

  /// <summary>Sorts every directory of the image at <paramref name="path"/> in place; size and contents unchanged.</summary>
  /// <exception cref="NotSupportedException">The format does not sort directory entries, or the pass did not verify (the image is rolled back).</exception>
  public static void SortDirectoryEntries(string path) {
    var target = Target(path, MaintenanceCapability.SortDirectoryEntries, "sort directory entries");
    using var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    MaintenanceVerbs.SortDirectoryEntries(target, image);
  }

  private static (string Id, IFormatDescriptor? Descriptor) Resolve(string path) {
    ArgumentException.ThrowIfNullOrEmpty(path);
    if (!File.Exists(path)) throw new FileNotFoundException("File not found.", path);
    FormatRegistration.EnsureInitialized();
    var id = FormatDetector.Detect(path).ToString();
    return (id, FormatRegistry.GetById(id));
  }

  /// <summary>The object that carries <paramref name="capability"/> for the file's format: the descriptor or its operations object.</summary>
  private static object Target(string path, MaintenanceCapability capability, string verb) {
    var (id, descriptor) = Resolve(path);
    if (descriptor is null)
      throw new NotSupportedException($"'{Path.GetFileName(path)}' is not in a recognised format; nothing to {verb}.");
    foreach (var candidate in new object?[] { descriptor, FormatRegistry.GetArchiveOps(id), FormatRegistry.GetStreamOps(id) })
      if (candidate is not null && MaintenanceCapabilities.Of(candidate).HasFlag(capability))
        return candidate;
    throw new NotSupportedException(
      $"{descriptor.DisplayName} does not {verb} losslessly. Offered here: {Offered(MaintenanceCapabilities.Describe(descriptor))}.");
  }

  private static string Offered(MaintenanceProfile profile) {
    var names = Enum.GetValues<MaintenanceCapability>()
      .Where(c => c != MaintenanceCapability.None && profile.Supports(c))
      .Select(static c => c.ToString())
      .ToArray();
    return names.Length == 0 ? "nothing" : string.Join(", ", names);
  }

  /// <summary>
  /// For a single-stream format with a parameter schema and no re-encode of its own, the
  /// wider search over every schema combination (<see cref="CompressionOptimizer"/>) instead of
  /// the format's single best preset. Formats that carry their own header metadata (gzip)
  /// keep their own encoder, which is what carries that metadata across.
  /// </summary>
  private static Action<Stream, Stream>? SchemaSearch(object target) {
    if (target is not IStreamFormatOperations stream
        || target is not IFormatOptionsSchema { OptionsSchema.Count: > 0 } schema
        || !UsesDefaultReencode(target))
      return null;
    return (input, output) => {
      using var raw = new MemoryStream();
      input.Position = 0;
      stream.Decompress(input, raw);
      var best = CompressionOptimizer.OptimizeStream(raw.ToArray(), stream, schema);
      output.Write(best.Bytes);
    };
  }

  private static bool UsesDefaultReencode(object target) {
    var map = target.GetType().GetInterfaceMap(typeof(ICompressionOptimizable));
    for (var i = 0; i < map.InterfaceMethods.Length; ++i)
      if (map.InterfaceMethods[i].Name == nameof(ICompressionOptimizable.OptimizeCompression))
        return map.TargetMethods[i].DeclaringType == typeof(ICompressionOptimizable);
    return true;
  }

  private static MaintenanceResult Staged(string inputPath, string outputPath, Func<Stream, Stream, MaintenanceResult> run) {
    ArgumentException.ThrowIfNullOrEmpty(outputPath);
    MaintenanceResult? result = null;
    AtomicFileWriter.WriteAtomic(outputPath, output => {
      using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
      result = run(input, output);
    });
    return result!;
  }
}
