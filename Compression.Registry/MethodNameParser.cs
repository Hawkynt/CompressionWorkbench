namespace Compression.Registry;

/// <summary>
/// Parses a format method name into (base method, plus level).
/// <para>
/// Convention (Zopfli-inspired): a trailing <c>+</c> (or repeated <c>++</c>,
/// <c>+++</c>) on a method id means "spend extra CPU for a better
/// compression ratio". The base method is the longest prefix that does not
/// end with <c>+</c>; the plus level is the number of trailing <c>+</c>
/// characters that were stripped.
/// </para>
/// <para>
/// Examples:
/// </para>
/// <list type="bullet">
///   <item><c>"deflate"</c>   → <c>("deflate", 0)</c></item>
///   <item><c>"deflate+"</c>  → <c>("deflate", 1)</c> — ~10× effort (e.g. enable lazy matching, deeper search)</item>
///   <item><c>"deflate++"</c> → <c>("deflate", 2)</c> — ~100× effort (e.g. full Zopfli)</item>
///   <item><c>"stored"</c>    → <c>("stored",  0)</c></item>
/// </list>
/// <para>
/// Writers consult <see cref="ValueTuple{T1, T2}.Item2"/> (the plus level)
/// to pick: 0 = default fast, 1 = "+", 2+ = "++" and slower variants.
/// Surrounding whitespace is trimmed before parsing.
/// </para>
/// </summary>
public static class MethodNameParser {

  /// <summary>
  /// Parses <paramref name="method"/> into <c>(BaseMethod, PlusLevel)</c>.
  /// Whitespace is trimmed first; a null or whitespace-only input returns
  /// <c>("", 0)</c>. A string that is only <c>+</c> characters returns
  /// <c>("", n)</c>.
  /// </summary>
  public static (string BaseMethod, int PlusLevel) Parse(string? method) {
    if (string.IsNullOrEmpty(method))
      return ("", 0);

    var trimmed = method.Trim();
    if (trimmed.Length == 0)
      return ("", 0);

    var plus = 0;
    while (trimmed.Length > 0 && trimmed[^1] == '+') {
      ++plus;
      trimmed = trimmed[..^1];
    }

    return (trimmed, plus);
  }
}
