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

  private static string SettingsPath => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "CompressionWorkbench", "settings.json");

  public static UserSettings Load() {
    try {
      var path = SettingsPath;
      if (!File.Exists(path)) return new();
      var json = File.ReadAllText(path);
      return JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
    } catch {
      // Settings corruption shouldn't block app launch.
      return new();
    }
  }

  public void Save() {
    try {
      var path = SettingsPath;
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
