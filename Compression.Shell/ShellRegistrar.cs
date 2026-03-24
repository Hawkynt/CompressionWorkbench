using Microsoft.Win32;

namespace Compression.Shell;

/// <summary>
/// Registers and unregisters Explorer context menu entries for CompressionWorkbench.
/// Uses the registry-based "shell extension" approach via ContextMenuHandlers
/// and the simpler "static verb" approach for broad compatibility.
/// </summary>
public static class ShellRegistrar {

  private const string AppName = "CompressionWorkbench";

  /// <summary>
  /// Archive file extensions that get context menu entries, derived from the format registry.
  /// </summary>
  private static string[]? _archiveExtensions;
  private static string[] ArchiveExtensions => _archiveExtensions ??= BuildArchiveExtensions();

  private static string[] BuildArchiveExtensions() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var desc in Compression.Registry.FormatRegistry.All) {
      foreach (var ext in desc.Extensions) exts.Add(ext);
      foreach (var ext in desc.CompoundExtensions) exts.Add(ext);
    }
    return [.. exts.OrderBy(e => e, StringComparer.OrdinalIgnoreCase)];
  }

  /// <summary>
  /// Registers context menu entries for the current user.
  /// </summary>
  /// <param name="cwbExePath">Full path to cwb.exe (CLI tool).</param>
  /// <param name="uiExePath">Full path to the UI executable.</param>
  public static void Register(string cwbExePath, string uiExePath) {
    // Register "Open with CompressionWorkbench" for all archive types
    foreach (var ext in ArchiveExtensions) {
      using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\shell\{AppName}");
      if (key == null) continue;
      key.SetValue("", "Open with CompressionWorkbench");
      key.SetValue("Icon", $"\"{uiExePath}\",0");

      using var cmdKey = key.CreateSubKey("command");
      cmdKey?.SetValue("", $"\"{uiExePath}\" \"%1\"");
    }

    // Register "Extract here" context menu
    foreach (var ext in ArchiveExtensions) {
      using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\shell\{AppName}.ExtractHere");
      if (key == null) continue;
      key.SetValue("", "Extract here");
      key.SetValue("Icon", $"\"{uiExePath}\",0");

      using var cmdKey = key.CreateSubKey("command");
      cmdKey?.SetValue("", $"\"{cwbExePath}\" extract \"%1\" --output \"%V\"");
    }

    // Register "Extract to folder" context menu
    foreach (var ext in ArchiveExtensions) {
      using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\shell\{AppName}.ExtractToFolder");
      if (key == null) continue;
      key.SetValue("", "Extract to folder...");
      key.SetValue("Icon", $"\"{uiExePath}\",0");
      key.SetValue("Extended", ""); // Only show in Shift+Right-click

      using var cmdKey = key.CreateSubKey("command");
      // The UI will handle the folder picker
      cmdKey?.SetValue("", $"\"{uiExePath}\" --extract \"%1\"");
    }

    // Register "Add to archive" on directories and * (all files)
    RegisterAddToArchive(cwbExePath, uiExePath, "Directory");
    RegisterAddToArchive(cwbExePath, uiExePath, "*");
  }

  private static void RegisterAddToArchive(string cwbExePath, string uiExePath, string fileClass) {
    // Add to .zip
    using var zipKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{fileClass}\shell\{AppName}.AddToZip");
    if (zipKey != null) {
      zipKey.SetValue("", "Add to ZIP archive");
      zipKey.SetValue("Icon", $"\"{uiExePath}\",0");
      using var cmd = zipKey.CreateSubKey("command");
      cmd?.SetValue("", $"\"{cwbExePath}\" create \"%1.zip\" \"%1\"");
    }

    // Add to .7z
    using var szKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{fileClass}\shell\{AppName}.AddTo7z");
    if (szKey != null) {
      szKey.SetValue("", "Add to 7z archive");
      szKey.SetValue("Icon", $"\"{uiExePath}\",0");
      using var cmd = szKey.CreateSubKey("command");
      cmd?.SetValue("", $"\"{cwbExePath}\" create \"%1.7z\" \"%1\"");
    }
  }

  /// <summary>
  /// Removes all CompressionWorkbench context menu entries.
  /// </summary>
  public static void Unregister() {
    foreach (var ext in ArchiveExtensions) {
      TryDeleteKey($@"Software\Classes\{ext}\shell\{AppName}");
      TryDeleteKey($@"Software\Classes\{ext}\shell\{AppName}.ExtractHere");
      TryDeleteKey($@"Software\Classes\{ext}\shell\{AppName}.ExtractToFolder");
    }
    TryDeleteKey($@"Software\Classes\Directory\shell\{AppName}.AddToZip");
    TryDeleteKey($@"Software\Classes\Directory\shell\{AppName}.AddTo7z");
    TryDeleteKey($@"Software\Classes\*\shell\{AppName}.AddToZip");
    TryDeleteKey($@"Software\Classes\*\shell\{AppName}.AddTo7z");
  }

  private static void TryDeleteKey(string path) {
    try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    catch { /* ignore */ }
  }
}
