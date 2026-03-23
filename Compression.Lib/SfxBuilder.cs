using System.Runtime.InteropServices;

namespace Compression.Lib;

/// <summary>
/// Creates self-extracting archives by concatenating a stub executable with archive data.
/// Layout: [stub.exe][archive data][8-byte archive offset (int64 LE)][4-byte magic "SFX!"]
/// </summary>
internal static class SfxBuilder {

  private static readonly byte[] Magic = [(byte)'S', (byte)'F', (byte)'X', (byte)'!'];

  internal enum StubType { Cli, Ui }

  /// <summary>
  /// Known runtime identifiers for cross-platform stub publishing.
  /// </summary>
  internal static readonly string[] SupportedTargets = [
    "win-x64", "win-x86", "win-arm64",
    "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64",
    "osx-x64", "osx-arm64",
  ];

  /// <summary>
  /// Creates a self-extracting archive by combining a stub with an archive file.
  /// </summary>
  internal static void Create(string archivePath, string outputExePath, StubType stubType, string? targetRid = null) {
    var stubPath = FindStub(stubType, targetRid);
    using var output = File.Create(outputExePath);

    // 1. Copy stub
    using (var stub = File.OpenRead(stubPath))
      stub.CopyTo(output);

    var archiveOffset = output.Position;

    // 2. Copy archive data
    using (var archive = File.OpenRead(archivePath))
      archive.CopyTo(output);

    // 3. Write trailer: [8-byte offset][4-byte magic]
    Span<byte> trailer = stackalloc byte[12];
    BitConverter.TryWriteBytes(trailer, archiveOffset);
    Magic.CopyTo(trailer[8..]);
    output.Write(trailer);
  }

  /// <summary>
  /// Creates a self-extracting archive from an in-memory archive stream.
  /// </summary>
  internal static void Create(Stream archiveData, string outputExePath, StubType stubType, string? targetRid = null) {
    var stubPath = FindStub(stubType, targetRid);
    using var output = File.Create(outputExePath);

    // 1. Copy stub
    using (var stub = File.OpenRead(stubPath))
      stub.CopyTo(output);

    var archiveOffset = output.Position;

    // 2. Copy archive data
    archiveData.CopyTo(output);

    // 3. Write trailer
    Span<byte> trailer = stackalloc byte[12];
    BitConverter.TryWriteBytes(trailer, archiveOffset);
    Magic.CopyTo(trailer[8..]);
    output.Write(trailer);
  }

  /// <summary>
  /// Creates a self-extracting archive using a custom stub file (for testing).
  /// </summary>
  internal static void Create(string archivePath, string outputExePath, string stubPath) {
    using var output = File.Create(outputExePath);

    using (var stub = File.OpenRead(stubPath))
      stub.CopyTo(output);

    var archiveOffset = output.Position;

    using (var archive = File.OpenRead(archivePath))
      archive.CopyTo(output);

    Span<byte> trailer = stackalloc byte[12];
    BitConverter.TryWriteBytes(trailer, archiveOffset);
    Magic.CopyTo(trailer[8..]);
    output.Write(trailer);
  }

  /// <summary>
  /// Wraps an existing archive into an SFX without recompressing.
  /// </summary>
  internal static void WrapExisting(string existingArchive, string outputExe, StubType stubType, string? targetRid = null)
    => Create(existingArchive, outputExe, stubType, targetRid);

  /// <summary>
  /// Reads the SFX trailer from a file and returns (archiveOffset, archiveLength, format).
  /// Returns null if the file does not contain a valid SFX trailer.
  /// </summary>
  internal static (long Offset, long Length, FormatDetector.Format Format)? ReadTrailer(string sfxPath) {
    using var fs = File.OpenRead(sfxPath);
    if (fs.Length < 12) return null;

    fs.Seek(-12, SeekOrigin.End);
    Span<byte> trailer = stackalloc byte[12];
    fs.ReadExactly(trailer);

    if (trailer[8] != Magic[0] || trailer[9] != Magic[1] || trailer[10] != Magic[2] || trailer[11] != Magic[3])
      return null;

    var offset = BitConverter.ToInt64(trailer[..8]);
    var length = fs.Length - 12 - offset;
    if (offset < 0 || offset >= fs.Length - 12 || length <= 0)
      return null;

    fs.Seek(offset, SeekOrigin.Begin);
    var header = new byte[(int)Math.Min(512, length)];
    var read = fs.Read(header, 0, header.Length);
    var format = FormatDetector.DetectByMagic(header.AsSpan(0, read));

    return (offset, length, format);
  }

  /// <summary>
  /// Extracts the archive portion from an SFX file to a directory.
  /// </summary>
  internal static void Extract(string sfxPath, string outputDir, string? password = null) {
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
  internal static string CurrentRid() {
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
  private static string FindStub(StubType stubType, string? targetRid = null) {
    var rid = targetRid ?? CurrentRid();
    var stubName = stubType == StubType.Cli ? "sfx-cli" : "sfx-ui";
    var stubExeName = rid.StartsWith("win") ? $"{stubName}.exe" : stubName;

    // 1. Try embedded resource first (populated by CI or publish-sfx-stubs.ps1)
    var resourcePath = TryExtractEmbeddedStub(rid, stubExeName);
    if (resourcePath != null)
      return resourcePath;

    // 2. File system fallback (development builds)
    var projectFolder = stubType == StubType.Cli ? "Compression.Sfx.Cli" : "Compression.Sfx.Ui";
    var tfms = stubType == StubType.Ui ? new[] { "net10.0-windows", "net10.0" } : new[] { "net10.0" };

    var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
    var candidates = new List<string>();

    candidates.Add(Path.Combine(exeDir, "stubs", rid, stubExeName));

    var repoRoot = FindRepoRoot(exeDir);
    if (repoRoot != null) {
      candidates.Add(Path.Combine(repoRoot, "Compression.CLI", "stubs", rid, stubExeName));
      candidates.Add(Path.Combine(repoRoot, "Compression.Lib", "stubs", rid, stubExeName));

      foreach (var config in new[] { "Release", "Debug" }) {
        foreach (var tfm in tfms) {
          candidates.Add(Path.Combine(repoRoot, projectFolder, "bin", config, tfm, rid, "publish", stubExeName));
        }
      }

      foreach (var config in new[] { "Release", "Debug" }) {
        foreach (var tfm in tfms) {
          candidates.Add(Path.Combine(repoRoot, projectFolder, "bin", config, tfm, stubExeName));
        }
      }
    }

    candidates.Add(Path.Combine(exeDir, stubExeName));

    foreach (var candidate in candidates) {
      if (File.Exists(candidate))
        return Path.GetFullPath(candidate);
    }

    throw new FileNotFoundException(
      $"SFX stub '{stubExeName}' not found for target '{rid}'. " +
      $"Publish the stub first: dotnet publish {projectFolder} -r {rid} -c Release"
    );
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
