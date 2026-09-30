using System.Text.Json;

namespace Compression.NativeUI;

/// <summary>
/// Lightweight per-user settings persisted as JSON under the platform's local application data
/// directory (<c>%LOCALAPPDATA%\CompressionWorkbench\settings.json</c> on Windows,
/// <c>~/.local/share/CompressionWorkbench/settings.json</c> on Linux). Currently stores only the
/// last-used folder so the next launch can restore the browser at that location (with a parent-walk
/// fallback when the folder has been deleted between sessions).
/// </summary>
internal sealed class UserSettings {
  public string? LastFolder { get; set; }

  /// <summary>
  /// Redirects the settings file. For tests: opening an archive records its folder, and without
  /// this a test run overwrites the developer's own last-used folder.
  /// </summary>
  internal static string? PathOverride { get; set; }

  private static string? SettingsPath => PathOverride ?? SettingsPathUnder(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create));

  /// <summary>
  /// The settings file inside <paramref name="dataFolder"/>, or null when that is not an absolute
  /// path. .NET reports an empty path for a data folder the platform does not have - on Linux,
  /// <c>~/.local/share</c> on a fresh profile - and combining it gave a relative path, which wrote
  /// the settings into whatever folder the shell had been started from.
  /// </summary>
  internal static string? SettingsPathUnder(string dataFolder)
    => Path.IsPathRooted(dataFolder) ? Path.Combine(dataFolder, "CompressionWorkbench", "settings.json") : null;

  public static UserSettings Load() {
    try {
      var path = SettingsPath;
      if (path is null || !File.Exists(path)) return new();
      var json = File.ReadAllText(path);
      return JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
    } catch {
      // Settings corruption shouldn't block app launch.
      return new();
    }
  }

  public void Save() {
    try {
      if (SettingsPath is not { } path) return;
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
      File.WriteAllText(path, json);
    } catch {
      // Best-effort persistence — settings save failure must not crash the app.
    }
  }

  /// <summary>
  /// Returns the deepest existing ancestor of <paramref name="path"/>, walking up through parent
  /// directories until one exists. Falls back to
  /// <see cref="Environment.SpecialFolder.UserProfile"/> when nothing on the path is reachable
  /// (e.g. removable drive ejected since last session).
  /// </summary>
  public static string ResolveExistingAncestor(string? path) {
    if (string.IsNullOrEmpty(path))
      return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    var current = path;
    while (!string.IsNullOrEmpty(current)) {
      if (Directory.Exists(current)) return current;
      var parent = Directory.GetParent(current)?.FullName;
      if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
        break;
      current = parent;
    }

    return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
  }
}
