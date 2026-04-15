#pragma warning disable CS1591

using System.Runtime.InteropServices;

namespace Compression.Analysis.ExternalTools;

/// <summary>
/// Information about a discovered external tool.
/// </summary>
public sealed class DiscoveredTool {
  /// <summary>Short name of the tool (e.g., "7z", "gzip").</summary>
  public required string Name { get; init; }

  /// <summary>Full path to the executable.</summary>
  public required string Path { get; init; }

  /// <summary>Version string, if obtainable.</summary>
  public string? Version { get; init; }
}

/// <summary>
/// Auto-detects available external compression/analysis tools on PATH and in well-known locations.
/// Results are cached for the process lifetime.
/// </summary>
public static class ToolDiscovery {

  private static Dictionary<string, DiscoveredTool>? _cache;
  private static readonly object _lock = new();

  /// <summary>
  /// Names of tools to search for.
  /// </summary>
  private static readonly string[] _toolNames = [
    "7z", "7za", "unrar", "binwalk", "file", "trid",
    "gzip", "bzip2", "xz", "zstd", "lz4", "tar"
  ];

  /// <summary>
  /// Well-known installation paths on Windows.
  /// </summary>
  private static readonly string[] _windowsPaths = [
    @"C:\Program Files\7-Zip\7z.exe",
    @"C:\Program Files (x86)\7-Zip\7z.exe",
    @"C:\Program Files\7-Zip\7za.exe",
    @"C:\Program Files (x86)\7-Zip\7za.exe",
  ];

  /// <summary>
  /// Well-known patterns for Git for Windows installations (provides gzip, bzip2, xz, tar).
  /// </summary>
  private static readonly string[] _gitForWindowsSubPaths = [
    @"Git\usr\bin",
    @"Git\mingw64\bin",
  ];

  /// <summary>
  /// Discovers all available tools, returning a map of tool name to discovered tool info.
  /// Results are cached for the process lifetime. Thread-safe.
  /// </summary>
  public static Dictionary<string, DiscoveredTool> DiscoverTools() {
    if (_cache != null)
      return _cache;

    lock (_lock) {
      if (_cache != null)
        return _cache;

      var result = new Dictionary<string, DiscoveredTool>(StringComparer.OrdinalIgnoreCase);
      var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

      // Search PATH for each tool.
      foreach (var name in _toolNames) {
        var path = FindOnPath(name, isWindows);
        if (path != null)
          result.TryAdd(name, new DiscoveredTool { Name = name, Path = path });
      }

      // On Windows, check well-known installation paths.
      if (isWindows) {
        foreach (var knownPath in _windowsPaths) {
          if (!File.Exists(knownPath))
            continue;

          var name = System.IO.Path.GetFileNameWithoutExtension(knownPath).ToLowerInvariant();
          result.TryAdd(name, new DiscoveredTool { Name = name, Path = knownPath });
        }

        // Check Git for Windows paths.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        foreach (var subPath in _gitForWindowsSubPaths) {
          foreach (var root in new[] { programFiles, programFilesX86 }) {
            if (string.IsNullOrEmpty(root))
              continue;

            var dir = System.IO.Path.Combine(root, subPath);
            if (!Directory.Exists(dir))
              continue;

            foreach (var toolName in new[] { "gzip", "bzip2", "xz", "tar", "file" }) {
              var toolPath = System.IO.Path.Combine(dir, toolName + ".exe");
              if (File.Exists(toolPath))
                result.TryAdd(toolName, new DiscoveredTool { Name = toolName, Path = toolPath });
            }
          }
        }
      }

      _cache = result;
      return result;
    }
  }

  /// <summary>
  /// Gets the path to a specific tool, or null if not found.
  /// </summary>
  public static string? GetToolPath(string toolName) =>
    DiscoverTools().TryGetValue(toolName, out var tool) ? tool.Path : null;

  /// <summary>
  /// Returns true if the specified tool is available.
  /// </summary>
  public static bool IsAvailable(string toolName) =>
    DiscoverTools().ContainsKey(toolName);

  /// <summary>
  /// Clears the cached discovery results, forcing re-discovery on the next call.
  /// </summary>
  public static void InvalidateCache() {
    lock (_lock) {
      _cache = null;
    }
  }

  private static string? FindOnPath(string toolName, bool isWindows) {
    var pathEnv = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrEmpty(pathEnv))
      return null;

    var separator = isWindows ? ';' : ':';
    var extensions = isWindows ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
    var dirs = pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries);

    foreach (var dir in dirs) {
      foreach (var ext in extensions) {
        var candidate = System.IO.Path.Combine(dir, toolName + ext);
        try {
          if (File.Exists(candidate))
            return candidate;
        } catch {
          // Permission errors on some dirs — skip.
        }
      }
    }

    return null;
  }
}
