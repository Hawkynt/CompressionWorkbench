using Compression.Registry;

namespace Compression.Core.Deflate;

/// <summary>
/// Resolves a <see cref="DeflateCompressionLevel"/> from a format's
/// <see cref="FormatCreateOptions"/>. Shared by the Deflate-based stream formats
/// (GZIP, Zlib) so their <c>Level</c> option is parsed identically.
/// </summary>
public static class DeflateLevelOption {

  /// <summary>
  /// Resolves the requested level: the named <c>Level</c> string in
  /// <see cref="FormatCreateOptions.FormatSpecific"/> wins (matched
  /// case-insensitively against the enum names); otherwise a numeric
  /// <see cref="FormatCreateOptions.Level"/> (0–11) is mapped onto the nearest
  /// tier; failing both, <see cref="DeflateCompressionLevel.Default"/>.
  /// </summary>
  public static DeflateCompressionLevel Parse(FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    var raw = options.GetString("Level");
    if (raw is { Length: > 0 } && Enum.TryParse<DeflateCompressionLevel>(raw, ignoreCase: true, out var named))
      return named;

    return options.Level is { } numeric ? FromNumeric(numeric) : DeflateCompressionLevel.Default;
  }

  /// <summary>Maps a 0–11 numeric level onto the nearest named Deflate tier.</summary>
  private static DeflateCompressionLevel FromNumeric(int level) => level switch {
    <= 0 => DeflateCompressionLevel.None,
    <= 3 => DeflateCompressionLevel.Fast,
    <= 6 => DeflateCompressionLevel.Default,
    <= 9 => DeflateCompressionLevel.Best,
    _ => DeflateCompressionLevel.Maximum,
  };
}
