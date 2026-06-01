using System.Globalization;

namespace Compression.Registry.Layout;

/// <summary>
/// A byte-range expression for a <see cref="LayoutZone"/>. Either both
/// <see cref="StartFraction"/>/<see cref="EndFraction"/> are set (percent
/// form: <c>0%-5%</c>) or both <see cref="StartBytes"/>/<see cref="EndBytes"/>
/// are set (absolute form: <c>10MB-50MB</c>). Open-ended ranges
/// (<c>5%-</c>, <c>-50%</c>, <c>1024-+</c>) resolve to the image bounds.
/// </summary>
/// <param name="StartFraction">Start as a 0..1 fraction of the image, or null if absolute.</param>
/// <param name="EndFraction">End (exclusive) as a 0..1 fraction of the image, or null if absolute.</param>
/// <param name="StartBytes">Start in bytes, or null if percent.</param>
/// <param name="EndBytes">End (exclusive) in bytes, or null if percent.</param>
public sealed record RangeSpec(
  double? StartFraction,
  double? EndFraction,
  long? StartBytes,
  long? EndBytes) {

  /// <summary>
  /// Resolves the spec into concrete byte bounds against an image of size
  /// <paramref name="imageSize"/>. End is clamped to <paramref name="imageSize"/>.
  /// Returns a half-open interval [start, end).
  /// </summary>
  public (long Start, long End) Resolve(long imageSize) {
    if (imageSize < 0) throw new ArgumentOutOfRangeException(nameof(imageSize));

    var start = this.StartBytes
      ?? (this.StartFraction is { } sf ? (long)(sf * imageSize) : 0);
    var end = this.EndBytes
      ?? (this.EndFraction is { } ef ? (long)(ef * imageSize) : imageSize);

    if (start < 0) start = 0;
    if (end > imageSize) end = imageSize;
    if (end < start) end = start;
    return (start, end);
  }

  /// <summary>
  /// Parses a textual range. Accepted forms (case-insensitive, whitespace-insensitive):
  /// <list type="bullet">
  ///   <item><c>0%-5%</c> — percent form, end exclusive.</item>
  ///   <item><c>10MB-50MB</c> — absolute form with KB/MB/GB/TB suffix.</item>
  ///   <item><c>[1024, 2048)</c> — bracket form, supports half-open semantics.</item>
  ///   <item><c>[1024, 2048]</c> — closed form treated as half-open at end+1.</item>
  ///   <item><c>5%-</c> / <c>10MB-</c> — open-ended (to image end).</item>
  ///   <item><c>-50%</c> / <c>-1MB</c> — open-started (from image origin).</item>
  ///   <item><c>1024-+</c> — synonymous with <c>1024-</c>.</item>
  /// </list>
  /// </summary>
  public static RangeSpec Parse(string s) {
    ArgumentNullException.ThrowIfNull(s);
    var trimmed = s.Trim();
    if (trimmed.Length == 0) throw new FormatException("Empty range spec.");

    // Bracket form: [start, end) or [start, end]
    if (trimmed[0] is '[' or '(' && (trimmed[^1] is ')' or ']'))
      return ParseBracket(trimmed);

    // Dash form: start-end (either side may be open / "+")
    return ParseDash(trimmed);
  }

  private static RangeSpec ParseBracket(string s) {
    var inclusiveStart = s[0] == '[';
    var inclusiveEnd = s[^1] == ']';
    var inner = s[1..^1].Trim();
    var comma = inner.IndexOf(',');
    if (comma < 0)
      throw new FormatException($"Bracket range '{s}' must contain a comma.");

    var startTok = inner[..comma].Trim();
    var endTok = inner[(comma + 1)..].Trim();

    var (startBytes, startFraction) = ParseEndpoint(startTok, isStart: true);
    var (endBytes, endFraction) = ParseEndpoint(endTok, isStart: false);

    // ( endpoint and ] endpoint adjustments aren't pixel-perfect in mixed
    // unit cases, but we apply best-effort: ] means "include this byte" so
    // the half-open end is endBytes+1.
    if (!inclusiveStart && startBytes is { } sb) startBytes = sb + 1;
    if (inclusiveEnd && endBytes is { } eb) endBytes = eb + 1;

    return new RangeSpec(startFraction, endFraction, startBytes, endBytes);
  }

  private static RangeSpec ParseDash(string s) {
    // Find the dash that splits start from end. Don't confuse with a leading
    // minus on the start endpoint — but our grammar uses leading dash to
    // signal an open start, so we treat "-50%" as start-omitted, end=50%.
    if (s == "-" || s == "+") throw new FormatException($"Invalid range '{s}'.");

    // Strip a trailing '+' (e.g. "1024-+").
    if (s.EndsWith('+')) s = s[..^1];

    string startTok, endTok;
    if (s.StartsWith('-')) {
      // open start
      startTok = "";
      endTok = s[1..];
    } else {
      var dashIdx = s.IndexOf('-', 1);
      if (dashIdx < 0) throw new FormatException($"Range '{s}' missing '-' separator.");
      startTok = s[..dashIdx];
      endTok = s[(dashIdx + 1)..];
    }

    var (startBytes, startFraction) = ParseEndpoint(startTok, isStart: true);
    var (endBytes, endFraction) = ParseEndpoint(endTok, isStart: false);
    return new RangeSpec(startFraction, endFraction, startBytes, endBytes);
  }

  private static (long? Bytes, double? Fraction) ParseEndpoint(string tok, bool isStart) {
    tok = tok.Trim();
    if (tok.Length == 0) return (null, null); // open

    if (tok.EndsWith('%')) {
      var pctText = tok[..^1].Trim();
      if (!double.TryParse(pctText, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        throw new FormatException($"Invalid percent '{tok}'.");
      if (pct < 0 || pct > 100)
        throw new FormatException($"Percent '{tok}' must be 0..100.");
      return (null, pct / 100.0);
    }

    // Byte form with optional unit suffix.
    var bytes = ParseByteSize(tok);
    return (bytes, null);
  }

  private static long ParseByteSize(string s) {
    var trimmed = s.Trim();
    // Split numeric prefix from unit suffix.
    var splitAt = trimmed.Length;
    for (var i = 0; i < trimmed.Length; i++) {
      var c = trimmed[i];
      if (!(char.IsDigit(c) || c == '.' || c == '+' || c == '-' || c == 'e' || c == 'E')) {
        splitAt = i;
        break;
      }
    }
    var numText = trimmed[..splitAt].Trim();
    var unitText = trimmed[splitAt..].Trim().ToUpperInvariant();

    if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
      throw new FormatException($"Invalid number '{numText}' in byte size '{s}'.");

    long multiplier = unitText switch {
      "" or "B" => 1L,
      "K" or "KB" or "KIB" => 1024L,
      "M" or "MB" or "MIB" => 1024L * 1024L,
      "G" or "GB" or "GIB" => 1024L * 1024L * 1024L,
      "T" or "TB" or "TIB" => 1024L * 1024L * 1024L * 1024L,
      _ => throw new FormatException($"Unknown byte unit '{unitText}' in '{s}'."),
    };

    return (long)(num * multiplier);
  }

  /// <inheritdoc/>
  public override string ToString() {
    var start = this.StartFraction is { } sf
      ? FormatPercent(sf)
      : this.StartBytes is { } sb ? FormatBytes(sb) : "";
    var end = this.EndFraction is { } ef
      ? FormatPercent(ef)
      : this.EndBytes is { } eb ? FormatBytes(eb) : "";
    return $"{start}-{end}";
  }

  private static string FormatPercent(double fraction)
    => (fraction * 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "%";

  private static string FormatBytes(long bytes) {
    if (bytes % (1024L * 1024L * 1024L * 1024L) == 0 && bytes != 0)
      return (bytes / (1024L * 1024L * 1024L * 1024L)).ToString(CultureInfo.InvariantCulture) + "TB";
    if (bytes % (1024L * 1024L * 1024L) == 0 && bytes != 0)
      return (bytes / (1024L * 1024L * 1024L)).ToString(CultureInfo.InvariantCulture) + "GB";
    if (bytes % (1024L * 1024L) == 0 && bytes != 0)
      return (bytes / (1024L * 1024L)).ToString(CultureInfo.InvariantCulture) + "MB";
    if (bytes % 1024L == 0 && bytes != 0)
      return (bytes / 1024L).ToString(CultureInfo.InvariantCulture) + "KB";
    return bytes.ToString(CultureInfo.InvariantCulture);
  }
}
