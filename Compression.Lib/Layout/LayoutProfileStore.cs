using Compression.Registry.Layout;

namespace Compression.Lib.Layout;

/// <summary>
/// Where a discovered profile lives. Built-in profiles ship with the repo
/// under <c>templates/</c> and must be treated as read-only by the UI;
/// user profiles live under <c>%APPDATA%/CompressionWorkbench/profiles/</c>
/// and are read-write.
/// </summary>
public enum ProfileOrigin {
  /// <summary>Read-only template shipped with the repo under <c>templates/</c>.</summary>
  Builtin,
  /// <summary>User-saved template under %APPDATA%/CompressionWorkbench/profiles/.</summary>
  User,
}

/// <summary>
/// One discovered layout profile. The UI shows <see cref="Name"/> + a badge
/// derived from <see cref="Origin"/>; <see cref="FilePath"/> is the canonical
/// disk location used by <see cref="LayoutProfileStore.Load"/> /
/// <see cref="LayoutProfileStore.Delete"/>.
/// </summary>
public sealed record LayoutProfileEntry(
  string Name,
  string FilePath,
  ProfileOrigin Origin);

/// <summary>
/// Filesystem-backed CRUD for layout profiles. Combines built-in templates
/// (read-only, shipped under <c>templates/</c>) with user-saved templates
/// (read-write, under <c>%APPDATA%/CompressionWorkbench/profiles/</c>).
///
/// <para>The store is process-local with no caching — each <see cref="List"/>
/// call re-scans both directories so changes from another instance (or the
/// editor saving a new file) are picked up immediately.</para>
///
/// <para>The built-in directory is resolved via two strategies, in order:</para>
/// <list type="number">
///   <item>The path overridden by <see cref="BuiltinDirectoryOverride"/>.
///   Tests use this to point at a temp dir.</item>
///   <item>A <c>templates/</c> directory found by walking up from
///   <c>AppContext.BaseDirectory</c> — handles both running from the build
///   output and running from a published single-file binary alongside a
///   <c>templates/</c> sibling.</item>
/// </list>
/// </summary>
public static class LayoutProfileStore {

  /// <summary>
  /// Optional override for <see cref="BuiltinDirectory"/>. Tests set this to
  /// a temp directory and reset to <c>null</c> in their teardown.
  /// </summary>
  public static string? BuiltinDirectoryOverride { get; set; }

  /// <summary>
  /// Optional override for <see cref="UserDirectory"/>. Tests set this to
  /// a temp directory and reset to <c>null</c> in their teardown.
  /// </summary>
  public static string? UserDirectoryOverride { get; set; }

  /// <summary>
  /// Directory holding built-in templates (read-only from the UI's perspective).
  /// Resolved at call time so test overrides take effect.
  /// </summary>
  public static string BuiltinDirectory => BuiltinDirectoryOverride ?? ResolveDefaultBuiltinDirectory();

  /// <summary>
  /// Directory holding user-saved templates (read-write).
  /// Resolved at call time so test overrides take effect.
  /// </summary>
  public static string UserDirectory => UserDirectoryOverride ?? ResolveDefaultUserDirectory();

  /// <summary>
  /// Enumerates all discoverable profiles. Returns built-ins first, then
  /// user profiles, each sorted alphabetically by name. Profiles that fail
  /// to parse are skipped silently — the editor surfaces parse failures
  /// when the user explicitly clicks one.
  /// </summary>
  public static IReadOnlyList<LayoutProfileEntry> List() {
    var result = new List<LayoutProfileEntry>();
    AppendFrom(result, BuiltinDirectory, ProfileOrigin.Builtin);
    AppendFrom(result, UserDirectory, ProfileOrigin.User);
    return result;
  }

  /// <summary>Loads the JSON file referenced by <paramref name="entry"/>.</summary>
  public static LayoutTemplate Load(LayoutProfileEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return LayoutTemplate.Load(entry.FilePath);
  }

  /// <summary>
  /// Saves <paramref name="template"/> as <paramref name="fileName"/> under
  /// the user directory. Returns the freshly-created entry. The user
  /// directory is created on demand if it does not exist.
  /// </summary>
  /// <exception cref="ArgumentException">
  /// Thrown when <paramref name="fileName"/> is null, blank, or contains
  /// path separators / invalid characters.
  /// </exception>
  public static LayoutProfileEntry Save(LayoutTemplate template, string fileName) {
    ArgumentNullException.ThrowIfNull(template);
    ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

    if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0)
      throw new ArgumentException($"Invalid filename '{fileName}'.", nameof(fileName));

    var normalised = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
      ? fileName : fileName + ".json";

    var userDir = UserDirectory;
    Directory.CreateDirectory(userDir);
    var fullPath = Path.Combine(userDir, normalised);
    template.Save(fullPath);
    return new LayoutProfileEntry(template.Name, fullPath, ProfileOrigin.User);
  }

  /// <summary>
  /// Deletes a user profile. Throws <see cref="InvalidOperationException"/>
  /// when the entry refers to a built-in template (those are read-only).
  /// </summary>
  public static void Delete(LayoutProfileEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Origin == ProfileOrigin.Builtin)
      throw new InvalidOperationException("Built-in profiles are read-only.");
    if (File.Exists(entry.FilePath))
      File.Delete(entry.FilePath);
  }

  /// <summary>
  /// Generates a unique filename for a new profile based on its template name.
  /// Strips invalid characters and adds a numeric suffix on collision.
  /// </summary>
  public static string SuggestFileName(string templateName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
    var sanitized = SanitizeForFileName(templateName);
    if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "profile";
    var userDir = UserDirectory;
    if (!Directory.Exists(userDir)) return sanitized + ".json";
    var candidate = sanitized + ".json";
    var i = 2;
    while (File.Exists(Path.Combine(userDir, candidate)))
      candidate = $"{sanitized}-{i++}.json";
    return candidate;
  }

  private static string SanitizeForFileName(string s) {
    var invalid = Path.GetInvalidFileNameChars();
    var chars = new char[s.Length];
    var idx = 0;
    foreach (var c in s) {
      if (Array.IndexOf(invalid, c) >= 0 || c is ':' or '/' or '\\') {
        chars[idx++] = '-';
        continue;
      }
      chars[idx++] = c is ' ' ? '-' : char.ToLowerInvariant(c);
    }
    return new string(chars, 0, idx).Trim('-');
  }

  private static void AppendFrom(List<LayoutProfileEntry> result, string directory, ProfileOrigin origin) {
    if (!Directory.Exists(directory)) return;
    var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).ToList();
    files.Sort(StringComparer.OrdinalIgnoreCase);
    foreach (var file in files) {
      string name;
      try {
        // Parse just the name field for the listing; full parse on Load.
        var tpl = LayoutTemplate.Load(file);
        name = tpl.Name;
      } catch {
        // Skip files that don't parse — they'd error out when clicked, no
        // point cluttering the list with broken entries.
        continue;
      }
      result.Add(new LayoutProfileEntry(name, file, origin));
    }
  }

  /// <summary>
  /// Walks up from <c>AppContext.BaseDirectory</c> looking for a sibling
  /// <c>templates/</c> directory. Handles the dev case (running from
  /// <c>Compression.UI/bin/Debug/...</c> with <c>templates/</c> at the repo
  /// root) and a deployed case where the directory is co-located.
  /// </summary>
  private static string ResolveDefaultBuiltinDirectory() {
    var current = AppContext.BaseDirectory;
    for (var i = 0; i < 8 && !string.IsNullOrEmpty(current); i++) {
      var candidate = Path.Combine(current, "templates");
      if (Directory.Exists(candidate)) return candidate;
      var parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
      if (string.IsNullOrEmpty(parent) || parent == current) break;
      current = parent;
    }
    // Fall back to the conventional location even if it doesn't exist — the
    // List() method tolerates a missing directory.
    return Path.Combine(AppContext.BaseDirectory, "templates");
  }

  private static string ResolveDefaultUserDirectory() {
    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    if (string.IsNullOrEmpty(appData)) {
      // Non-Windows fallback: ~/.config/CompressionWorkbench/profiles
      appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }
    return Path.Combine(appData, "CompressionWorkbench", "profiles");
  }
}
