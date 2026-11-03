using System.Globalization;
using System.Text.RegularExpressions;

namespace FileFormat.Rar;

/// <summary>
/// The file names of a RAR volume set. RAR 3.0 and later name volumes
/// <c>name.part1.rar</c>, <c>name.part2.rar</c>, … with as many digits as the set needs
/// (<c>part01</c>, <c>part001</c>); the older scheme, still written with <c>rar -vn</c>, is
/// <c>name.rar</c>, <c>name.r00</c>, <c>name.r01</c>, … <c>name.r99</c>, <c>name.s00</c>, ….
/// </summary>
public static partial class RarVolumeNames {
  [GeneratedRegex(@"^(?<base>.*)\.part(?<n>[0-9]+)\.(?<ext>[^.]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex NewStyle();

  [GeneratedRegex(@"^(?<base>.*)\.(?<l>[a-z])(?<n>[0-9]{2,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex OldStyleNumbered();

  /// <summary>
  /// The volume name sequence <paramref name="path"/> belongs to: index 0 is the first volume.
  /// Returns null when the name follows neither scheme.
  /// </summary>
  public static Func<int, string>? SequenceOf(string path) {
    var dir = Path.GetDirectoryName(path) ?? "";
    var file = Path.GetFileName(path);
    var m = NewStyle().Match(file);
    if (m.Success) {
      var baseName = m.Groups["base"].Value;
      var digits = m.Groups["n"].Value.Length;
      var ext = m.Groups["ext"].Value;
      return i => Path.Combine(dir, $"{baseName}.part{(i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')}.{ext}");
    }
    m = OldStyleNumbered().Match(file);
    if (m.Success && !file.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)) {
      var baseName = m.Groups["base"].Value;
      var upper = char.IsUpper(m.Groups["l"].Value[0]);
      var rarName = Path.Combine(dir, baseName + (upper ? ".RAR" : ".rar"));
      return i => i == 0 ? rarName : Path.Combine(dir, baseName + "." + OldSuffix(i - 1, upper));
    }
    if (file.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) {
      var baseName = file[..^4];
      var upper = file.EndsWith(".RAR", StringComparison.Ordinal);
      return i => i == 0 ? path : Path.Combine(dir, baseName + "." + OldSuffix(i - 1, upper));
    }
    return null;
  }

  /// <summary>
  /// The sequence for the volumes written after the first one at <paramref name="firstPath"/>:
  /// new-style names when the path ends in <c>.partN.rar</c> or when <paramref name="newStyle"/>
  /// asks for them, the old <c>.r00</c> scheme otherwise.
  /// </summary>
  public static Func<int, string> ForWriting(string firstPath, bool newStyle, int digits = 0) {
    var seq = SequenceOf(firstPath);
    if (seq != null && NewStyle().IsMatch(Path.GetFileName(firstPath)))
      return seq;
    if (!newStyle)
      return seq ?? (i => i == 0 ? firstPath : firstPath + "." + OldSuffix(i - 1, false));
    var dir = Path.GetDirectoryName(firstPath) ?? "";
    var file = Path.GetFileName(firstPath);
    var baseName = file.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
    var width = Math.Max(1, digits);
    return i => i == 0 ? firstPath : Path.Combine(dir, $"{baseName}.part{(i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}.rar");
  }

  /// <summary>
  /// The extension of old-style volume <paramref name="index"/> + 2 (index 0 is <c>r00</c>,
  /// the second volume).
  /// </summary>
  /// <remarks>Past <c>r99</c> the letter advances (<c>s00</c>, <c>t00</c>, …), as rar 6.24
  /// <c>-vn</c> names them; beyond <c>z99</c> this is a hypothesis.</remarks>
  public static string OldSuffix(int index, bool upper) {
    var letter = (char)('r' + index / 100);
    var s = index < 2600 && letter <= 'z'
      ? $"{letter}{(index % 100).ToString("00", CultureInfo.InvariantCulture)}"
      : $"r{index.ToString("000", CultureInfo.InvariantCulture)}";
    return upper ? s.ToUpperInvariant() : s;
  }

  /// <summary>Whether <paramref name="path"/> names a recovery volume (<c>.rev</c>).</summary>
  public static bool IsRecoveryVolume(string path) => path.EndsWith(".rev", StringComparison.OrdinalIgnoreCase);
}
