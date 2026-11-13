using Compression.Core.FileSystems.Cvf;
using Compression.Registry;

namespace FileSystem.DoubleSpace;

/// <summary>
/// The per-cluster codec a CVF writer is asked for — by the schema's <c>Method</c>/<c>Level</c>
/// options or by a method name such as <c>jm++</c> — shared by the DoubleSpace, DriveSpace,
/// DriveSpace 3 and Stacker descriptors. Every published method names a codec the real driver
/// reads; the <c>+</c> run raises the encoder's search effort.
/// </summary>
internal static class CvfCodecChoice {
  private const int DefaultLevel = 2;
  private const int MaxLevel = 4;

  /// <summary><c>stored</c> plus each codec in three effort tiers (<c>x</c>, <c>x+</c>, <c>x++</c>).</summary>
  public static IReadOnlyList<FormatMethodInfo> Methods(params (string Id, string Name)[] codecs) {
    List<FormatMethodInfo> methods = [new("stored", "Stored (no compression)")];
    foreach (var (id, name) in codecs) {
      methods.Add(new(id, name));
      methods.Add(new(id + "+", $"{name} (deeper match search)"));
      methods.Add(new(id + "++", $"{name} (exhaustive match search)"));
    }
    return methods;
  }

  /// <summary>
  /// The codec and effort level for <paramref name="options"/>: the <c>Method</c> option (with
  /// <c>Level</c>) when present, else the method name (its <c>+</c> run raising the level), else
  /// <paramref name="defaultCodec"/>.
  /// </summary>
  /// <exception cref="ArgumentException">A codec this volume type cannot record.</exception>
  public static (CvfLzMethod Method, int Level) Resolve(FormatCreateOptions options, string defaultCodec, IReadOnlyDictionary<string, CvfLzMethod> codecs) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.HasOption("Method"))
      return (Lookup(options.GetOption("Method", defaultCodec), codecs), Math.Clamp(options.GetOptionInt("Level", DefaultLevel), 1, MaxLevel));

    var named = FormatHelpers.MethodWithEffort(options);
    if (string.IsNullOrWhiteSpace(named))
      return (Lookup(defaultCodec, codecs), Math.Clamp(options.GetOptionInt("Level", DefaultLevel), 1, MaxLevel));

    var (codec, plus) = MethodNameParser.Parse(named);
    return (Lookup(codec, codecs), Math.Min(DefaultLevel + plus, MaxLevel));
  }

  private static CvfLzMethod Lookup(string name, IReadOnlyDictionary<string, CvfLzMethod> codecs)
    => name.Equals("stored", StringComparison.OrdinalIgnoreCase) ? CvfLzMethod.Stored
      : codecs.TryGetValue(name.ToLowerInvariant(), out var method) ? method
      : throw new ArgumentException($"Unknown cluster codec '{name}'; this volume type records: stored, {string.Join(", ", codecs.Keys)}.", nameof(name));
}
