using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Compression.Tests.Support;

internal static class ExecutablePackerToolCache {
  private static readonly string Root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "third-party-tools", "exe-packers");

  public sealed record PapawTools(string Papawify, string Stub);

  public static string? GetUpx() {
    var existing = FindExecutable("upx");
    if (existing != null) return existing;
    if (!DownloadsEnabled) return null;

    var version = Environment.GetEnvironmentVariable("CWB_UPX_VERSION");
    if (string.IsNullOrWhiteSpace(version)) version = "5.0.2";
    var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win64" :
      RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" : "amd64_linux";
    var ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".zip" : ".tar.xz";
    var archive = Path.Combine(Root, $"upx-{version}-{os}{ext}");
    var url = $"https://github.com/upx/upx/releases/download/v{version}/upx-{version}-{os}{ext}";
    Download(url, archive);
    var extractDir = Path.Combine(Root, $"upx-{version}-{os}");
    Directory.CreateDirectory(extractDir);
    if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
      ZipFile.ExtractToDirectory(archive, extractDir, overwriteFiles: true);
    else
      ExtractTarXz(archive, extractDir);
    return FindExecutable("upx", extractDir);
  }

  public static string? GetCrinkler() {
    var existing = FindExecutable("crinkler");
    if (existing != null) return existing;
    if (!DownloadsEnabled || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

    var version = Environment.GetEnvironmentVariable("CWB_CRINKLER_VERSION");
    if (string.IsNullOrWhiteSpace(version)) version = "3.0a";
    var assetName = $"crinkler{version.Replace(".", "", StringComparison.OrdinalIgnoreCase)}.zip";
    var url = Environment.GetEnvironmentVariable("CWB_CRINKLER_ZIP_URL");
    if (string.IsNullOrWhiteSpace(url))
      url = $"https://github.com/runestubbe/Crinkler/releases/download/v{version}/{assetName}";

    var archive = Path.Combine(Root, $"crinkler-{version}.zip");
    Download(url, archive);
    var extractDir = Path.Combine(Root, $"crinkler-{version}");
    Directory.CreateDirectory(extractDir);
    ZipFile.ExtractToDirectory(archive, extractDir, overwriteFiles: true);
    return FindExecutable("crinkler", extractDir);
  }

  public static PapawTools? GetPapaw() {
    var papawify = FindExecutable("papawify-xz");
    var stub = FindExecutable("papaw-xz-x86_64");
    if (papawify != null && stub != null)
      return new(papawify, stub);

    if (!DownloadsEnabled)
      return null;

    var dir = Path.Combine(Root, "papaw");
    Directory.CreateDirectory(dir);
    papawify = Path.Combine(dir, "papawify-xz");
    stub = Path.Combine(dir, "papaw-xz-x86_64");
    Download("https://github.com/dimkr/papaw/releases/latest/download/papawify-xz", papawify);
    Download("https://github.com/dimkr/papaw/releases/latest/download/papaw-xz-x86_64", stub);
    return File.Exists(papawify) && File.Exists(stub) ? new(papawify, stub) : null;
  }

  public static string? GetGoPackerSource() {
    if (!DownloadsEnabled)
      return null;

    var archive = Path.Combine(Root, "gopacker", "master.zip");
    Download("https://github.com/nirhaas/gopacker/archive/refs/heads/master.zip", archive);
    var extractDir = Path.Combine(Root, "gopacker", "source");
    Directory.CreateDirectory(extractDir);
    ZipFile.ExtractToDirectory(archive, extractDir, overwriteFiles: true);
    return Directory.EnumerateDirectories(extractDir, "gopacker-*", SearchOption.TopDirectoryOnly).FirstOrDefault();
  }

  public static string? GetOrigamiSource() {
    if (!DownloadsEnabled)
      return null;

    var archive = Path.Combine(Root, "origami", "master.zip");
    Download("https://github.com/dr4k0nia/Origami/archive/refs/heads/master.zip", archive);
    var extractDir = Path.Combine(Root, "origami", "source");
    Directory.CreateDirectory(extractDir);
    ZipFile.ExtractToDirectory(archive, extractDir, overwriteFiles: true);
    return Directory.EnumerateDirectories(extractDir, "Origami-*", SearchOption.TopDirectoryOnly).FirstOrDefault();
  }

  public static string? GetSilentPacker() {
    var existing = FindExecutable("Silent_Packer");
    if (existing != null)
      return existing;
    if (!DownloadsEnabled || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      return null;

    var dir = Path.Combine(Root, "silent_packer");
    Directory.CreateDirectory(dir);
    var tool = Path.Combine(dir, "Silent_Packer");
    Download("https://github.com/SilentVoid13/Silent_Packer/releases/download/v0.1/Silent_Packer", tool);
    try {
      File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    } catch {
      // Best-effort for platforms/filesystems that do not expose chmod.
    }
    return File.Exists(tool) ? tool : null;
  }

  public static string? GetPackerTool(string id, params string[] executableNames) {
    foreach (var name in executableNames) {
      var existing = FindExecutable(name);
      if (existing != null) return existing;
    }
    if (!DownloadsEnabled) return null;

    var envName = $"CWB_PACKER_{NormalizeEnvId(id)}_URL";
    var url = Environment.GetEnvironmentVariable(envName);
    if (string.IsNullOrWhiteSpace(url))
      return null;

    var archive = Path.Combine(Root, id, Path.GetFileName(new Uri(url).AbsolutePath));
    Download(url, archive);
    var extractDir = Path.Combine(Root, id, "tool");
    Directory.CreateDirectory(extractDir);
    ExtractArchive(archive, extractDir);
    foreach (var name in executableNames) {
      var extracted = FindExecutable(name, extractDir);
      if (extracted != null) return extracted;
    }
    return null;
  }

  public static string? GetPackerSample(string id) {
    var normalized = NormalizeEnvId(id);
    var path = Environment.GetEnvironmentVariable($"CWB_PACKER_{normalized}_SAMPLE");
    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
      return path;
    if (!DownloadsEnabled) return null;

    var url = Environment.GetEnvironmentVariable($"CWB_PACKER_{normalized}_SAMPLE_URL");
    if (string.IsNullOrWhiteSpace(url))
      return null;

    var target = Path.Combine(Root, id, "samples", Path.GetFileName(new Uri(url).AbsolutePath));
    Download(url, target);
    return File.Exists(target) ? target : null;
  }

  public static string? GetPackingBoxDatasetPackedRoot() {
    var configured = Environment.GetEnvironmentVariable("CWB_DATASET");
    if (!string.IsNullOrWhiteSpace(configured)) {
      var root = Directory.Exists(configured) && Path.GetFileName(configured).Equals("packed", StringComparison.OrdinalIgnoreCase)
        ? configured
        : Path.Combine(configured, "packed");
      if (Directory.Exists(root))
        return root;
    }

    if (!DownloadsEnabled)
      return null;

    var archive = Path.Combine(Root, "packing-box", "dataset-packed-pe-main.zip");
    Download("https://github.com/packing-box/dataset-packed-pe/archive/refs/heads/main.zip", archive);
    var extractDir = Path.Combine(Root, "packing-box", "dataset-packed-pe-main");
    Directory.CreateDirectory(extractDir);
    ZipFile.ExtractToDirectory(archive, extractDir, overwriteFiles: true);
    return Directory.EnumerateDirectories(extractDir, "dataset-packed-pe-main", SearchOption.TopDirectoryOnly)
      .Select(d => Path.Combine(d, "packed"))
      .FirstOrDefault(Directory.Exists);
  }

  public static string? GetHostTool(params string[] names) {
    foreach (var name in names) {
      var existing = FindExecutable(name);
      if (existing != null) return existing;
    }
    return null;
  }

  public static string Run(string exe, params string[] args) {
    var start = CreateStartInfo(exe, args);
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {exe}.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit(30_000);
    return stdout + stderr;
  }

  public static string RunWithEnvironment(string exe, IReadOnlyDictionary<string, string> environment, params string[] args) {
    var start = CreateStartInfo(exe, args);
    foreach (var (key, value) in environment)
      start.Environment[key] = value;
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {exe}.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit(30_000);
    return stdout + stderr;
  }

  public static string RunInDirectory(string exe, string workingDirectory, params string[] args) {
    var start = CreateStartInfo(exe, args);
    start.WorkingDirectory = workingDirectory;
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Failed to start {exe}.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit(60_000);
    return stdout + stderr;
  }

  private static ProcessStartInfo CreateStartInfo(string exe, params string[] args) {
    var executable = exe;
    var prependedArgs = Array.Empty<string>();
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
        Path.GetExtension(exe).Length == 0 &&
        File.Exists(exe)) {
      var bash = FindExecutable("bash");
      if (bash != null) {
        executable = bash;
        prependedArgs = [exe];
      }
    }

    var start = new ProcessStartInfo(executable) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    };
    foreach (var arg in prependedArgs) start.ArgumentList.Add(arg);
    foreach (var arg in args) start.ArgumentList.Add(arg);
    return start;
  }

  private static bool DownloadsEnabled =>
    string.Equals(Environment.GetEnvironmentVariable("CWB_DOWNLOAD_EXE_PACKER_TOOLS"), "1", StringComparison.Ordinal);

  private static void Download(string url, string target) {
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    if (File.Exists(target)) return;
    using var client = new HttpClient();
    using var response = client.GetAsync(url).GetAwaiter().GetResult();
    response.EnsureSuccessStatusCode();
    using var input = response.Content.ReadAsStream();
    using var output = File.Create(target);
    input.CopyTo(output);
  }

  private static void ExtractArchive(string archive, string extractDir) {
    if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
      ZipFile.ExtractToDirectory(archive, extractDir, overwriteFiles: true);
      return;
    }
    if (archive.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)) {
      ExtractTarXz(archive, extractDir);
      return;
    }
    if (archive.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
      TarFile.ExtractToDirectory(archive, extractDir, overwriteFiles: true);
  }

  private static void ExtractTarXz(string archive, string extractDir) {
    var tar = archive[..^3];
    if (!File.Exists(tar)) {
      var sevenZip = FindExecutable("7z") ?? FindExecutable("7za");
      if (sevenZip == null) return;
      _ = Run(sevenZip, "x", "-y", $"-o{Path.GetDirectoryName(archive)}", archive);
    }
    if (File.Exists(tar))
      TarFile.ExtractToDirectory(tar, extractDir, overwriteFiles: true);
  }

  private static string? FindExecutable(string name, string? root = null) {
    var names = CandidateExecutableNames(name);
    if (root != null)
      return names.SelectMany(n => Directory.EnumerateFiles(root, n, SearchOption.AllDirectories)).FirstOrDefault();

    var paths = ToolSearchPaths();
    foreach (var path in paths) {
      foreach (var candidateName in names) {
        var candidate = Path.Combine(path, candidateName);
        if (File.Exists(candidate) && IsUsableToolPath(name, candidate)) return candidate;
      }
    }
    return null;
  }

  private static bool IsUsableToolPath(string name, string candidate) {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;
    return !candidate.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
  }

  private static string[] CandidateExecutableNames(string name) {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || Path.HasExtension(name))
      return [name];
    return [$"{name}.exe", name];
  }

  private static IEnumerable<string> ToolSearchPaths() {
    foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
      if (!string.IsNullOrWhiteSpace(path))
        yield return path;

    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) yield break;

    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    if (!string.IsNullOrWhiteSpace(programFiles)) {
      yield return Path.Combine(programFiles, "Git", "bin");
      yield return Path.Combine(programFiles, "Git", "usr", "bin");
      yield return Path.Combine(programFiles, "Git", "mingw64", "bin");
    }

    var claudePortable = @"D:\Agents\ClaudePortable\app";
    if (Directory.Exists(claudePortable)) {
      yield return Path.Combine(claudePortable, "python");
      yield return Path.Combine(claudePortable, "bash", "bin");
      yield return Path.Combine(claudePortable, "bash", "usr", "bin");
      yield return Path.Combine(claudePortable, "bash", "mingw64", "bin");
    }
  }

  private static string NormalizeEnvId(string id) {
    var chars = id.ToUpperInvariant().ToCharArray();
    for (var i = 0; i < chars.Length; i++)
      if (!char.IsAsciiLetterOrDigit(chars[i]))
        chars[i] = '_';
    return new string(chars);
  }
}
