namespace Compression.NativeUI.Editing;

/// <summary>
/// What a user may type as the new name of a file, folder or archive entry. The same rules apply
/// inside an archive as on disk: an entry that cannot be extracted under its own name on this
/// machine is a trap, and the rebuild that renames it extracts it first anyway.
/// </summary>
internal static class EntryName {
  /// <summary>The longest single name the common filesystems accept.</summary>
  public const int MaxLength = 255;

  private static readonly char[] WindowsForbidden = ['<', '>', ':', '"', '|', '?', '*'];

  private static readonly HashSet<string> WindowsDevices = new(StringComparer.OrdinalIgnoreCase) {
    "CON", "PRN", "AUX", "NUL",
    "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
    "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
  };

  /// <summary>
  /// Checks <paramref name="typed"/> and returns the name to use — surrounding blanks removed — or
  /// the reason it cannot be used.
  /// </summary>
  public static (string? Name, string? Error) Validate(string? typed)
    => Validate(typed, OperatingSystem.IsWindows());

  /// <inheritdoc cref="Validate(string?)"/>
  /// <param name="windows">Apply Windows' additional rules, whatever this process runs on.</param>
  public static (string? Name, string? Error) Validate(string? typed, bool windows) {
    var name = (typed ?? "").Trim();

    if (name.Length == 0) return (null, "A name cannot be empty.");
    if (name is "." or "..") return (null, $"\"{name}\" is reserved.");
    if (name.Length > MaxLength) return (null, $"A name can be at most {MaxLength} characters long.");
    if (name.Contains('/') || name.Contains('\\')) return (null, "A name cannot contain / or \\.");
    if (name.Any(char.IsControl)) return (null, "A name cannot contain control characters.");

    if (windows) {
      if (name.IndexOfAny(WindowsForbidden) >= 0)
        return (null, "A name cannot contain any of < > : \" | ? *");
      if (name.EndsWith('.'))
        return (null, "A name cannot end with a dot.");

      var stem = name.Split('.')[0].TrimEnd();
      if (WindowsDevices.Contains(stem))
        return (null, $"\"{stem}\" is reserved by Windows.");
    }

    return (name, null);
  }
}
