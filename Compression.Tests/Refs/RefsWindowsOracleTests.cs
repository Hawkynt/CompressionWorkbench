using System.Security.Cryptography;
using System.Text.Json;
using FileSystem.Refs;

namespace Compression.Tests.Refs;

/// <summary>
/// ReFS volumes Windows formatted itself — Server 2022 (ReFS 3.7) and Server 2025 (ReFS 3.14),
/// each with 4 KiB clusters, 64 KiB clusters and integrity streams — captured by
/// <c>tools/foreign-os-oracles/refs-capture.ps1</c> on GitHub-hosted runners. Windows' own
/// Get-FileHash of every file is the oracle (<c>Refs/WindowsOracle/*.json</c>). The volumes are
/// Microsoft-written and not committed: set <c>CWB_REFS_WINDOWS_DIR</c> to the directory holding
/// the workflow's artifacts (<c>windows-2022/default.refs</c>, ...); each image is checked against
/// its recorded SHA-256 before use.
/// </summary>
[TestFixture]
public sealed class RefsWindowsOracleTests {

  private static readonly string[] Volumes = [
    "windows-2022-default", "windows-2022-cluster64k", "windows-2022-integrity",
    "windows-2025-default", "windows-2025-cluster64k", "windows-2025-integrity",
  ];

  private sealed record OracleFile(string Path, long Size, string Sha256);
  private sealed record Oracle(string Variant, string RefsVersion, string ImageFile, long ImageBytes, string ImageSha256, List<OracleFile> Files);

  private static Oracle Load(string volume) {
    using var resource = typeof(RefsWindowsOracleTests).Assembly.GetManifestResourceStream($"RefsWindowsOracle.{volume}.json")
                         ?? throw new InvalidOperationException($"{volume}.json is not embedded.");
    using var doc = JsonDocument.Parse(resource);
    var root = doc.RootElement;
    var image = root.GetProperty("image");
    return new Oracle(
      root.GetProperty("variant").GetString()!, root.GetProperty("refsVersion").GetString()!,
      image.GetProperty("file").GetString()!, image.GetProperty("bytes").GetInt64(), image.GetProperty("sha256").GetString()!,
      root.GetProperty("files").EnumerateArray()
        .Select(static f => new OracleFile(f.GetProperty("path").GetString()!, f.GetProperty("size").GetInt64(), f.GetProperty("sha256").GetString()!))
        .ToList());
  }

  private static string ImagePath(Oracle oracle) {
    var dir = Environment.GetEnvironmentVariable("CWB_REFS_WINDOWS_DIR");
    if (string.IsNullOrWhiteSpace(dir))
      Assert.Ignore("Set CWB_REFS_WINDOWS_DIR to the foreign-os-oracles ReFS artifacts (windows-2022/, windows-2025/).");
    var path = Path.Combine(dir!, oracle.ImageFile.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(path)) Assert.Ignore($"{path} is not there.");
    using (var stream = File.OpenRead(path))
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(stream)), Is.EqualTo(oracle.ImageSha256), "not the captured volume");
    return path;
  }

  [Test, Category("HappyPath")]
  public void GivenTheRecordedOracles_WhenLoaded_ThenEachNamesTheFileSetWindowsHashed([ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    Assert.Multiple(() => {
      Assert.That(oracle.Files, Has.Count.EqualTo(209));
      Assert.That(oracle.Files.Select(static f => f.Path), Does.Contain("dir1/nested/deep.txt"));
      Assert.That(oracle.RefsVersion, Is.EqualTo(volume.StartsWith("windows-2022", StringComparison.Ordinal) ? "3.7" : "3.14"));
    });
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeWindowsFormatted_WhenListedAndExtracted_ThenEveryFileHasTheBytesWindowsHashed([ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    var image = ImagePath(oracle);
    var descriptor = new RefsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_refs_win_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      using var stream = File.OpenRead(image);
      var listed = descriptor.List(stream, null).Where(static e => !e.IsDirectory).ToDictionary(static e => e.Name, static e => e.OriginalSize);
      stream.Position = 0;
      descriptor.Extract(stream, outDir, null, null);
      Assert.Multiple(() => {
        foreach (var file in oracle.Files) {
          Assert.That(listed.TryGetValue(file.Path, out var size), Is.True, $"{file.Path} listed");
          Assert.That(size, Is.EqualTo(file.Size), $"{file.Path} size");
          var extracted = Path.Combine(outDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
          Assert.That(File.Exists(extracted), Is.True, $"{file.Path} extracted");
          Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(extracted))), Is.EqualTo(file.Sha256), file.Path);
        }
      });
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test, Category("ExternalFsInterop")]
  public void GivenAVolumeWindowsFormatted_WhenAnEditIsAsked_ThenItIsRefusedAndTheImageIsUnchanged([ValueSource(nameof(Volumes))] string volume) {
    var oracle = Load(volume);
    var source = ImagePath(oracle);
    var copy = Path.Combine(Path.GetTempPath(), "cwb_refs_edit_" + Guid.NewGuid().ToString("N")[..8] + ".refs");
    File.Copy(source, copy);
    try {
      var descriptor = new RefsFormatDescriptor();
      using (var stream = File.Open(copy, FileMode.Open, FileAccess.ReadWrite)) {
        Assert.Multiple(() => {
          Assert.Throws<NotSupportedException>(() => descriptor.Add(stream, [Compression.Registry.ArchiveInputInfo.InMemory("replace-me.bin", new byte[65536])]));
          Assert.Throws<NotSupportedException>(() => descriptor.Remove(stream, ["remove-me.bin"]));
          Assert.Throws<NotSupportedException>(() => descriptor.Rename(stream, "rename-me.txt", "renamed.txt"));
          Assert.Throws<NotSupportedException>(() => descriptor.Defragment(stream));
        });
      }
      using var check = File.OpenRead(copy);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(check)), Is.EqualTo(oracle.ImageSha256));
    } finally {
      File.Delete(copy);
    }
  }
}
