namespace Compression.Registry;

/// <summary>
/// Options for archive/stream creation, passed from the orchestration layer to format descriptors.
/// </summary>
public sealed class FormatCreateOptions {
  private string? _methodName;

  /// <summary>Creates options with an optional compression/codec method.</summary>
  public FormatCreateOptions(string? Method = null) => this._methodName = Method;

  /// <summary>Encryption password.</summary>
  public string? Password { get; init; }

  /// <summary>Compression method name (e.g. "deflate", "lzma", "aac", "opus").</summary>
  public string? MethodName {
    get => this._methodName;
    init => this._methodName = value;
  }

  /// <summary>
  /// Alias for <see cref="MethodName"/> used by codec/container creation paths.
  /// Both properties share the same backing value.
  /// </summary>
  public string? Method {
    get => this._methodName;
    init => this._methodName = value;
  }

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
  /// Format-specific tunable knobs collected from an <see cref="IFormatOptionsSchema"/>.
  /// The collection is initialized so callers can use collection/index initializers
  /// without allocating a dictionary explicitly.
  /// </summary>
  public IDictionary<string, string> FormatSpecific { get; init; }
    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

  /// <summary>True when the caller explicitly supplied a non-empty value for <paramref name="key"/>.</summary>
  public bool HasOption(string key)
    => this.FormatSpecific.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value);

  /// <summary>Reads a format-specific string option, returning <paramref name="fallback"/> if absent.</summary>
  public string GetOption(string key, string fallback)
    => this.FormatSpecific.TryGetValue(key, out var value) ? value : fallback;

  /// <summary>Reads a string option, returning <see langword="null"/> if absent.</summary>
  public string? GetString(string key)
    => this.FormatSpecific.TryGetValue(key, out var value) ? value : null;

  /// <summary>Reads a format-specific integer option. Returns <paramref name="fallback"/> if absent or unparsable.</summary>
  public int GetOptionInt(string key, int fallback)
    => this.TryGetInt(key, out var value) ? value : fallback;

  /// <summary>Attempts to read a format-specific invariant-culture integer.</summary>
  public bool TryGetInt(string key, out int value)
    => this.FormatSpecific.TryGetValue(key, out var text)
       && int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out value);

  /// <summary>Reads a format-specific boolean option. Accepts true/false/1/0 (case-insensitive).</summary>
  public bool GetOptionBool(string key, bool fallback) {
    if (!this.FormatSpecific.TryGetValue(key, out var value)) return fallback;
    return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" ? true
      : value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0" ? false
      : fallback;
  }
}
