namespace Compression.Registry;

/// <summary>
/// Options for archive/stream creation, passed from the orchestration layer to format descriptors.
/// </summary>
public sealed class FormatCreateOptions {
  /// <summary>Encryption password.</summary>
  public string? Password { get; init; }

  /// <summary>Compression method name (e.g. "deflate", "lzma").</summary>
  public string? MethodName { get; init; }

  /// <summary>Whether "+" optimization was requested.</summary>
  public bool Optimize { get; init; }

  /// <summary>Compression level (0-9), or null for format default.</summary>
  public int? Level { get; init; }

  /// <summary>Dictionary size in bytes, or 0 for format default.</summary>
  public long DictSize { get; init; }

  /// <summary>Word size / fast bytes, or null for format default.</summary>
  public int? WordSize { get; init; }

  /// <summary>Number of parallel threads.</summary>
  public int Threads { get; init; } = 1;

  /// <summary>Maximum solid block size in bytes.</summary>
  public long SolidSize { get; init; }

  /// <summary>Whether to compress all files regardless of entropy detection.</summary>
  public bool ForceCompress { get; init; }

  /// <summary>When true, encrypt file names/headers.</summary>
  public bool EncryptFilenames { get; init; }

  /// <summary>Encryption method override (e.g. "aes256", "zipcrypto").</summary>
  public string? EncryptionMethod { get; init; }

  /// <summary>Set of file paths detected as incompressible (null = not computed).</summary>
  public HashSet<string>? IncompressiblePaths { get; init; }

  /// <summary>
  /// Format-specific tunable knobs collected from a
  /// <see cref="IFormatOptionsSchema"/>. Keys come from
  /// <see cref="FormatOptionDescriptor.Key"/>; values are in canonical string
  /// form (the format's writer parses them per its schema). Writers should
  /// call <see cref="GetOption(string, string)"/> or
  /// <see cref="GetOptionInt(string, int)"/> rather than reading the dict
  /// directly, so a missing entry falls back to the schema default.
  /// </summary>
  public IReadOnlyDictionary<string, string>? FormatSpecific { get; init; }

  /// <summary>
  /// True when the caller explicitly supplied <paramref name="key"/> (with a
  /// non-empty value). Writers use this to distinguish "caller pinned a size"
  /// from "use the format default / auto-optimise" — an unset size must leave the
  /// auto-selection path free, while a pinned size must be honoured byte-for-byte.
  /// </summary>
  public bool HasOption(string key)
    => this.FormatSpecific != null
       && this.FormatSpecific.TryGetValue(key, out var v)
       && !string.IsNullOrEmpty(v);

  /// <summary>Reads a format-specific string option, returning <paramref name="fallback"/> if absent.</summary>
  public string GetOption(string key, string fallback) {
    if (this.FormatSpecific == null) return fallback;
    return this.FormatSpecific.TryGetValue(key, out var v) ? v : fallback;
  }

  /// <summary>Reads a format-specific integer option. Returns <paramref name="fallback"/> if absent or unparsable.</summary>
  public int GetOptionInt(string key, int fallback) {
    if (this.FormatSpecific == null) return fallback;
    if (!this.FormatSpecific.TryGetValue(key, out var v)) return fallback;
    return int.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;
  }

  /// <summary>Reads a format-specific boolean option. Accepts "true"/"false"/"1"/"0" (case-insensitive).</summary>
  public bool GetOptionBool(string key, bool fallback) {
    if (this.FormatSpecific == null) return fallback;
    if (!this.FormatSpecific.TryGetValue(key, out var v)) return fallback;
    return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ? true
      : v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0" ? false
      : fallback;
  }
}
