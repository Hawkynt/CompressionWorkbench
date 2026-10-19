using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// The independent HFS+ implementations the tests judge our volumes by, each skipped cleanly where
/// it is not installed:
/// <list type="bullet">
/// <item><c>fsck.hfsplus</c> from hfsprogs (Apple's fsck_hfs ported to Linux), reached through
/// WSL on Windows — checks the volume header, every B-tree, the catalog hierarchy, hard link and
/// directory hard link chains, extended attributes and the allocation file.</item>
/// <item>libyal's libfshfs through <c>pyfshfs</c> (<c>pip install libfshfs-python</c>) — reads
/// files, decmpfs-compressed content and extended attributes.</item>
/// <item>HFSExplorer's <c>unhfs</c> (GPL, run as a program only) — extracts a volume and resolves
/// file and directory hard links. Found through HFSEXPLORER_HOME (its <c>lib/hfsx.jar</c>) or
/// D:\re-tools\hfsexplorer, with Java from JAVA_HOME, the PATH, or D:\re-tools\jdk.</item>
/// </list>
/// </summary>
internal static class HfsPlusOracles {

  // ── fsck.hfsplus ───────────────────────────────────────────────────

  internal static void RequireFsck() {
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not installed (needed for fsck.hfsplus).");
    if (!FsInteropToolbox.WslHasTool("fsck.hfsplus")) Assert.Ignore("fsck.hfsplus is not installed in WSL (`sudo apt install -y hfsprogs`).");
  }

  /// <summary>Runs <c>fsck.hfsplus -f -n</c> (forced, read-only) and returns its combined output and exit code.</summary>
  internal static (string Output, int ExitCode) Fsck(byte[] image, string tmp) {
    RequireFsck();
    var path = Path.Combine(tmp, $"fsck-{Guid.NewGuid():N}.hfs");
    File.WriteAllBytes(path, image);
    var (stdout, stderr, code) = FsInteropToolbox.RunWsl($"fsck.hfsplus -f -n {FsInteropToolbox.WinToWsl(path)}");
    File.Delete(path);
    return (stdout + "\n" + stderr, code);
  }

  internal static void AssertFsckClean(byte[] image, string tmp, string what) {
    var (output, code) = Fsck(image, tmp);
    Assert.That(output, Does.Contain("appears to be OK"), $"fsck.hfsplus did not accept the {what} volume (exit {code}):\n{output}");
    Assert.That(code, Is.Zero, $"fsck.hfsplus exit code for the {what} volume:\n{output}");
  }

  // ── libfshfs ───────────────────────────────────────────────────────

  private static readonly Lazy<string?> Python = new(() => {
    foreach (var candidate in new[] { "python3", "python" }) {
      try {
        if (Run(candidate, ["-c", "import pyfshfs"]).ExitCode == 0) return candidate;
      } catch (Win32Exception) {
        // not on the PATH
      }
    }
    return null;
  });

  /// <summary>One JSON object per entry: path, kind, size, sha256 of a regular file's bytes, and its extended attributes.</summary>
  private const string ListScript = """
    import hashlib, json, sys, pyfshfs
    volume = pyfshfs.volume()
    volume.open_file_object(open(sys.argv[1], 'rb'))
    def walk(entry, prefix):
        for index in range(entry.number_of_sub_file_entries):
            sub = entry.get_sub_file_entry(index)
            path = prefix + sub.name
            kind = sub.file_mode & 0o170000
            size = sub.size or 0
            digest = hashlib.sha256(sub.read_buffer(size) if kind == 0o100000 and size else b'').hexdigest()
            xattrs = {}
            for x in range(sub.number_of_extended_attributes):
                attribute = sub.get_extended_attribute(x)
                xattrs[attribute.name] = hashlib.sha256(attribute.read_buffer(attribute.size) if attribute.size else b'').hexdigest()
            print(json.dumps({'path': path, 'dir': kind == 0o040000, 'size': size, 'sha256': digest if kind == 0o100000 else None, 'xattrs': xattrs}))
            if kind == 0o040000:
                walk(sub, path + '/')
    walk(volume.get_root_directory(), '')
    """;

  internal sealed record LibfshfsEntry(string Path, bool Dir, long Size, string? Sha256, IReadOnlyDictionary<string, string> Xattrs);

  internal static List<LibfshfsEntry> Libfshfs(byte[] image, string tmp) {
    var python = Python.Value;
    if (python is null) Assert.Ignore("No Python with pyfshfs on the PATH (`pip install libfshfs-python`).");
    var path = Path.Combine(tmp, $"fshfs-{Guid.NewGuid():N}.hfs");
    File.WriteAllBytes(path, image);
    var (code, stdout, stderr) = Run(python!, ["-c", ListScript, path]);
    File.Delete(path);
    Assert.That(code, Is.EqualTo(0), $"libfshfs could not read the volume:\n{stderr}");
    return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(static line => JsonDocument.Parse(line).RootElement)
      .Select(static e => new LibfshfsEntry(e.GetProperty("path").GetString()!, e.GetProperty("dir").GetBoolean(),
        e.GetProperty("size").GetInt64(), e.GetProperty("sha256").GetString(),
        e.GetProperty("xattrs").EnumerateObject().ToDictionary(static p => p.Name, static p => p.Value.GetString()!)))
      .ToList();
  }

  internal static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

  // ── unhfs ──────────────────────────────────────────────────────────

  private static readonly Lazy<(string Java, string Jar)?> HfsExplorer = new(() => {
    var home = Environment.GetEnvironmentVariable("HFSEXPLORER_HOME") ?? @"D:\re-tools\hfsexplorer";
    var jar = Path.Combine(home, "lib", "hfsx.jar");
    if (!File.Exists(jar)) return null;
    var candidates = new List<string>();
    if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome) candidates.Add(Path.Combine(javaHome, "bin", "java"));
    candidates.Add("java");
    if (Directory.Exists(@"D:\re-tools\jdk"))
      candidates.AddRange(Directory.GetDirectories(@"D:\re-tools\jdk").Select(static d => Path.Combine(d, "bin", "java")));
    foreach (var java in candidates) {
      try {
        if (Run(java, ["-version"]).ExitCode == 0) return (java, jar);
      } catch (Win32Exception) {
        // not there
      }
    }
    return null;
  });

  /// <summary>Every regular file unhfs extracts from the volume, by POSIX path relative to the volume root.</summary>
  internal static Dictionary<string, byte[]> Unhfs(byte[] image, string tmp) {
    if (HfsExplorer.Value is not var (java, jar)) {
      Assert.Ignore("HFSExplorer's unhfs is not available (set HFSEXPLORER_HOME and install Java).");
      return [];
    }
    var path = Path.Combine(tmp, $"unhfs-{Guid.NewGuid():N}.hfs");
    var output = Path.Combine(tmp, $"unhfs-{Guid.NewGuid():N}");
    File.WriteAllBytes(path, image);
    Directory.CreateDirectory(output);
    // Its Windows file access needs the llio library that sits beside the jar.
    var (code, stdout, stderr) = Run(java, [$"-Djava.library.path={Path.GetDirectoryName(jar)}", "-cp", jar,
      "org.catacombae.hfsexplorer.tools.UnHFS", "-o", output, path]);
    Assert.That(code, Is.EqualTo(0), $"unhfs failed:\n{stdout}\n{stderr}");
    var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories)
      .ToDictionary(f => Path.GetRelativePath(output, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
    File.Delete(path);
    return files;
  }

  // ── Process ────────────────────────────────────────────────────────

  internal static (int ExitCode, string StdOut, string StdErr) Run(string file, IEnumerable<string> arguments) {
    var start = new ProcessStartInfo(file) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout.Result, stderr);
  }
}
