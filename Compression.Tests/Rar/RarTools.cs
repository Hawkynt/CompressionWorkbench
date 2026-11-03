using System.Diagnostics;
using System.Text;
using Compression.Analysis.ExternalTools;

namespace Compression.Tests.Rar;

/// <summary>
/// The RAR programs the tool-gated tests run, when present: RARLAB's <c>rar</c>/<c>unrar</c>
/// (from <c>CWB_TOOL_RAR</c>, <c>CWB_TOOL_RAR4</c>, <c>CWB_TOOL_UNRAR</c>, the PATH or a WinRAR
/// install) and 7-Zip (<c>CWB_TOOL_7Z</c> or the usual discovery).
/// </summary>
internal static class RarTools {
  private static readonly string[] WinRarDirs = [
    @"C:\Program Files\WinRAR", @"C:\Program Files (x86)\WinRAR",
  ];

  private static string? FromEnv(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } p && File.Exists(p) ? p : null;

  private static string? OnPath(string exe) {
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
      if (dir.Length == 0) continue;
      foreach (var name in OperatingSystem.IsWindows() ? new[] { exe + ".exe" } : [exe]) {
        var p = Path.Combine(dir, name);
        if (File.Exists(p)) return p;
      }
    }
    return null;
  }

  private static string? InWinRar(string exe) => OperatingSystem.IsWindows()
    ? WinRarDirs.Select(d => Path.Combine(d, exe + ".exe")).FirstOrDefault(File.Exists)
    : null;

  /// <summary>rar 5 or later (writes RAR 5).</summary>
  public static string? Rar { get; } = FromEnv("CWB_TOOL_RAR") ?? OnPath("rar") ?? InWinRar("Rar");

  /// <summary>A rar that still writes RAR 4 (<c>-ma4</c>, rar 5.x/6.x).</summary>
  public static string? Rar4 { get; } = FromEnv("CWB_TOOL_RAR4") ?? (Rar is { } r && MajorVersion(r) is >= 5 and <= 6 ? r : null);

  public static string? Unrar { get; } = FromEnv("CWB_TOOL_UNRAR") ?? ToolDiscovery.GetToolPath("unrar") ?? InWinRar("UnRAR");

  public static string? SevenZip { get; } = FromEnv("CWB_TOOL_7Z") ?? ToolDiscovery.GetToolPath("7z");

  private static int MajorVersion(string exe) {
    var (_, output) = Run(exe, ["-iver"], null);
    return int.TryParse(output.Trim().Split('.')[0], out var v) ? v : 0;
  }

  /// <summary>Runs <paramref name="exe"/>; returns its exit code and combined output.</summary>
  public static (int ExitCode, string Output) Run(string exe, IEnumerable<string> args, string? cwd, int timeoutMs = 600_000) {
    var psi = new ProcessStartInfo(exe) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
      StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
    };
    if (cwd != null) psi.WorkingDirectory = cwd;
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit(timeoutMs)) {
      try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
      return (-1, "timeout");
    }
    return (p.ExitCode, stdout.Result + stderr.Result);
  }

  public static string TempDir(string tag) {
    var d = Path.Combine(Path.GetTempPath(), $"cwb_{tag}_{Guid.NewGuid():N}");
    Directory.CreateDirectory(d);
    return d;
  }

  public static void TryDelete(string dir) {
    try {
      if (!Directory.Exists(dir)) return;
      foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
      Directory.Delete(dir, true);
    } catch (IOException) {
    } catch (UnauthorizedAccessException) {
    }
  }

  /// <summary>Every file under <paramref name="dir"/>, relative and slash-separated, with its bytes.</summary>
  public static Dictionary<string, byte[]> Files(string dir)
    => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
      .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
}
