using System.Runtime.InteropServices;
using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Creates self-extracting archives by concatenating a stub executable with archive data.
/// Layout is defined once by <see cref="SfxTrailer"/>, which the stubs also read.
/// </summary>
public static class SfxBuilder {

  public enum StubType { Cli, Ui }

  /// <summary>
  /// Known runtime identifiers for cross-platform stub publishing.
  /// </summary>
  public static readonly string[] SupportedTargets = [
    "win-x64", "win-x86", "win-arm64",
    "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64",
    "osx-x64", "osx-arm64",
  ];

  /// <summary>
  /// Creates a self-extracting archive by combining a stub with an archive file.
  /// </summary>
  public static void Create(string archivePath, string outputExePath, StubType stubType, string? targetRid = null) {
    var stubPath = FindStub(stubType, targetRid, DetectFormatId(archivePath));
    using var output = File.Create(outputExePath);

    // 1. Copy stub
    using (var stub = File.OpenRead(stubPath))
      stub.CopyTo(output);

    var archiveOffset = output.Position;

    // 2. Copy archive data
    using (var archive = File.OpenRead(archivePath))
      archive.CopyTo(output);

    // 3. Write trailer: [8-byte offset][4-byte magic]
    SfxTrailer.Write(output, archiveOffset);
  }

  /// <summary>
  /// The format id of an archive, for choosing a carved stub. Null when it cannot be identified,
  /// which simply means the universal stub gets used.
  /// </summary>
  private static string? DetectFormatId(string archivePath) {
    try {
      var format = FormatDetector.Detect(archivePath);
      return format == FormatDetector.Format.Unknown ? null : format.ToString();
    } catch {
      // Detection is an optimisation here, never a requirement.
      return null;
    }
  }

  /// <summary>
  /// Creates one self-extracting archive that runs on several operating systems: a Windows
  /// executable and a POSIX shell script at the same time.
  /// </summary>
  /// <param name="archivePath">The archive to embed.</param>
  /// <param name="outputExePath">File to create.</param>
  /// <param name="stubType">Console or GUI stub.</param>
  /// <param name="targetRids">
  /// Runtimes to bundle. Exactly one Windows runtime is required, because the file's outward shape
  /// is a PE; the rest are selected at run time by <c>uname</c>.
  /// </param>
  /// <exception cref="ArgumentException">No Windows runtime was listed.</exception>
  public static void CreateUniversal(
    string archivePath, string outputExePath, StubType stubType, IReadOnlyList<string> targetRids) {
    ArgumentNullException.ThrowIfNull(targetRids);

    var windowsRid = targetRids.FirstOrDefault(r => r.StartsWith("win", StringComparison.Ordinal))
      ?? throw new ArgumentException(
        "A multi-OS self-extractor needs a Windows runtime: the container is a PE file.", nameof(targetRids));

    var formatId = DetectFormatId(archivePath);
    var windowsStub = File.ReadAllBytes(FindStub(stubType, windowsRid, formatId));

    var posix = targetRids
      .Where(r => !r.StartsWith("win", StringComparison.Ordinal))
      .Select(r => new SfxPolyglot.TargetStub(r, File.ReadAllBytes(FindStub(stubType, r, formatId))))
      .ToList();

    using var archive = File.OpenRead(archivePath);
    SfxPolyglot.Write(outputExePath, windowsStub, posix, archive);
  }

  /// <summary>
  /// Creates a self-extracting archive from an in-memory archive stream.
  /// </summary>
  public static void Create(Stream archiveData, string outputExePath, StubType stubType, string? targetRid = null) {
    var stubPath = FindStub(stubType, targetRid, formatId: null);
    using var output = File.Create(outputExePath);

    // 1. Copy stub
    using (var stub = File.OpenRead(stubPath))
      stub.CopyTo(output);

    var archiveOffset = output.Position;

    // 2. Copy archive data
    archiveData.CopyTo(output);

    // 3. Write trailer
    SfxTrailer.Write(output, archiveOffset);
  }

  /// <summary>
  /// Creates a self-extracting archive using a custom stub file (for testing).
  /// </summary>
  public static void Create(string archivePath, string outputExePath, string stubPath) {
    using var output = File.Create(outputExePath);

    using (var stub = File.OpenRead(stubPath))
      stub.CopyTo(output);

    var archiveOffset = output.Position;

    using (var archive = File.OpenRead(archivePath))
      archive.CopyTo(output);

    SfxTrailer.Write(output, archiveOffset);
  }

  /// <summary>
  /// Wraps an existing archive into an SFX without recompressing.
  /// </summary>
  public static void WrapExisting(string existingArchive, string outputExe, StubType stubType, string? targetRid = null)
    => Create(existingArchive, outputExe, stubType, targetRid);

  /// <summary>
  /// Reads the SFX trailer from a file and returns (archiveOffset, archiveLength, format).
  /// Returns null if the file does not contain a valid SFX trailer.
  /// </summary>
  public static (long Offset, long Length, FormatDetector.Format Format)? ReadTrailer(string sfxPath) {
    using var fs = File.OpenRead(sfxPath);
    if (!SfxTrailer.TryRead(fs, out var location)) return null;

    // The trailer deliberately records no format, so it is sniffed from the payload every time.
    fs.Seek(location.Offset, SeekOrigin.Begin);
    var header = new byte[(int)Math.Min(512, location.Length)];
    var read = fs.Read(header, 0, header.Length);
    var format = FormatDetector.DetectByMagic(header.AsSpan(0, read));

    return (location.Offset, location.Length, format);
  }

  /// <summary>
  /// Extracts the archive portion from an SFX file to a directory.
  /// </summary>
  public static void Extract(string sfxPath, string outputDir, string? password = null) {
    var info = ReadTrailer(sfxPath) ?? throw new InvalidOperationException("Not a valid SFX file.");

    // Extract archive portion to temp file, then use ArchiveOperations
    var ext = FormatDetector.GetDefaultExtension(info.Format);
    var tempFile = Path.Combine(Path.GetTempPath(), $"sfx_{Guid.NewGuid():N}{ext}");
    try {
      using (var fs = File.OpenRead(sfxPath)) {
        fs.Seek(info.Offset, SeekOrigin.Begin);
        using var tempFs = File.Create(tempFile);
        var buf = new byte[81920];
        var remaining = info.Length;
        while (remaining > 0) {
          var toRead = (int)Math.Min(buf.Length, remaining);
          var read = fs.Read(buf, 0, toRead);
          if (read == 0) break;
          tempFs.Write(buf, 0, read);
          remaining -= read;
        }
      }

      ArchiveOperations.Extract(tempFile, outputDir, password, files: null);
    }
    finally {
      try { File.Delete(tempFile); } catch { /* best effort */ }
    }
  }

  /// <summary>
  /// Returns the current platform's runtime identifier.
  /// </summary>
  public static string CurrentRid() {
    var arch = RuntimeInformation.OSArchitecture switch {
      Architecture.X64 => "x64",
      Architecture.X86 => "x86",
      Architecture.Arm64 => "arm64",
      Architecture.Arm => "arm",
      _ => "x64",
    };

    if (OperatingSystem.IsWindows()) return $"win-{arch}";
    if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
    return $"linux-{arch}";
  }

  /// <summary>
  /// Finds a published single-file stub for the given type and target RID.
  /// Search order:
  /// 1. Embedded resource in Compression.Lib assembly (CI/published builds)
  /// 2. File system: stubs/{rid}/, repo build output, exe directory (dev builds)
  /// </summary>
  private static string FindStub(StubType stubType, string? targetRid = null, string? formatId = null) {
    var rid = targetRid ?? CurrentRid();
    var stubName = stubType == StubType.Cli ? "sfx-cli" : "sfx-ui";
    var suffix = rid.StartsWith("win") ? ".exe" : "";

    // A stub carved for this one format is a twentieth the size of the universal one, so it is
    // preferred whenever the matrix happens to have published it. Falling back is not a failure:
    // the universal stub reads every archive format there is.
    foreach (var name in CandidateStubNames(stubName, formatId)) {
      var stubExeName = name + suffix;

      var resourcePath = TryExtractEmbeddedStub(rid, stubExeName);
      if (resourcePath != null)
        return resourcePath;

      if (FindStubOnDisk(stubType, rid, stubExeName) is { } onDisk)
        return onDisk;
    }

    var fallbackName = stubName + suffix;
    var projectFolderName = stubType == StubType.Cli ? "Compression.Sfx.Cli" : "Compression.Sfx.Ui";
    throw new FileNotFoundException(
      $"SFX stub '{fallbackName}' not found for target '{rid}'. " +
      $"Publish the stub first: dotnet publish {projectFolderName} -r {rid} -c Release -p:SfxTier=Universal");
  }

  /// <summary>
  /// Stub names to try, best first: the one carved for this format, then the universal one, then
  /// the unsuffixed legacy name so an older stubs directory still works.
  /// </summary>
  private static IEnumerable<string> CandidateStubNames(string stubName, string? formatId) {
    if (!string.IsNullOrEmpty(formatId))
      yield return $"{stubName}-{formatId.ToLowerInvariant()}";

    yield return $"{stubName}-universal";
    yield return stubName;
  }

  private static string? FindStubOnDisk(StubType stubType, string rid, string stubExeName) {

    var projectFolder = stubType == StubType.Cli ? "Compression.Sfx.Cli" : "Compression.Sfx.Ui";
    // Both stubs target net10.0 since the GUI moved off WPF; AOT adds a platform segment to the path.
    var tfms = new[] { "net10.0" };

    var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
    var candidates = new List<string>();

    candidates.Add(Path.Combine(exeDir, "stubs", rid, stubExeName));

    var repoRoot = FindRepoRoot(exeDir);
    if (repoRoot != null) {
      candidates.Add(Path.Combine(repoRoot, "Compression.CLI", "stubs", rid, stubExeName));
      candidates.Add(Path.Combine(repoRoot, "Compression.Lib", "stubs", rid, stubExeName));

      foreach (var platform in new[] { "", "x64", "arm64" }) {
        foreach (var config in new[] { "Release", "Debug" }) {
          foreach (var tfm in tfms) {
            var bin = platform.Length == 0
              ? Path.Combine(repoRoot, projectFolder, "bin", config, tfm)
              : Path.Combine(repoRoot, projectFolder, "bin", platform, config, tfm);
            candidates.Add(Path.Combine(bin, rid, "publish", stubExeName));
            candidates.Add(Path.Combine(bin, rid, stubExeName));
            candidates.Add(Path.Combine(bin, stubExeName));
          }
        }
      }
    }

    candidates.Add(Path.Combine(exeDir, stubExeName));

    foreach (var candidate in candidates)
      if (File.Exists(candidate))
        return Path.GetFullPath(candidate);

    return null;
  }

  /// <summary>
  /// Tries to extract an SFX stub from embedded resources to a temp file.
  /// Returns the temp file path if found, null otherwise.
  /// Resource names follow: stubs/{rid}/sfx-cli[.exe]
  /// </summary>
  private static string? TryExtractEmbeddedStub(string rid, string stubExeName) {
    var assembly = typeof(SfxBuilder).Assembly;

    // Try both separator styles — MSBuild %(RecursiveDir) uses OS separators
    var resourceName = $"stubs/{rid}/{stubExeName}";
    var stream = assembly.GetManifestResourceStream(resourceName)
      ?? assembly.GetManifestResourceStream(resourceName.Replace('/', '\\'))
      ?? assembly.GetManifestResourceStream($"stubs/{rid}\\{stubExeName}");
    if (stream == null)
      return null;

    using (stream) {
      var tempDir = Path.Combine(Path.GetTempPath(), "cwb-sfx-stubs", rid);
      Directory.CreateDirectory(tempDir);
      var tempPath = Path.Combine(tempDir, stubExeName);

      // Only extract if not already cached or size differs
      if (!File.Exists(tempPath) || new FileInfo(tempPath).Length != stream.Length) {
        using var fs = File.Create(tempPath);
        stream.CopyTo(fs);
      }

      return tempPath;
    }
  }

  /// <summary>
  /// Walks up from a directory looking for the solution file to find the repo root.
  /// </summary>
  private static string? FindRepoRoot(string startDir) {
    var dir = Path.GetFullPath(startDir);
    for (var i = 0; i < 8; i++) {
      if (File.Exists(Path.Combine(dir, "CompressionWorkbench.slnx")))
        return dir;
      var parent = Path.GetDirectoryName(dir);
      if (parent == null || parent == dir) break;
      dir = parent;
    }
    return null;
  }
}
