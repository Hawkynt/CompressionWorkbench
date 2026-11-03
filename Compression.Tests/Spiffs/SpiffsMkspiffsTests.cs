#pragma warning disable CS1591
using System.Security.Cryptography;
using FileSystem.Spiffs;

namespace Compression.Tests.Spiffs;

/// <summary>
/// mkspiffs, built in the configuration each profile names, unpacks what the writer made
/// and must get every file back byte-exact. The builds are expected on the WSL PATH as
/// <c>mkspiffs-esp32</c>, <c>mkspiffs-esp8266</c> and <c>mkspiffs-generic</c> (see
/// <c>ReferenceVectors/generate.sh</c> for the build lines); the test skips without them.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
[Category("Wsl")]
public sealed class SpiffsMkspiffsTests {

  [TestCase("ESP-IDF / Arduino ESP32", "mkspiffs-esp32")]
  [TestCase("Arduino ESP8266", "mkspiffs-esp8266")]
  [TestCase("mkspiffs generic", "mkspiffs-generic")]
  public void GivenAnImageTheWriterMade_WhenMkspiffsUnpacksIt_ThenEveryFileComesBackByteExact(string profile, string tool) {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"{tool} is not on the WSL PATH.");
    var files = new Dictionary<string, byte[]> {
      ["empty"] = [],
      ["a/b/one-page"] = SpiffsTests.Pattern(251, 1),
      ["a/two-pages"] = SpiffsTests.Pattern(252, 2),
      ["index-pages.bin"] = SpiffsTests.Pattern(60_000, 3),
      [new string('n', 30)] = [7],
    };
    var g = SpiffsFormatDescriptor.Profiles[profile];
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_spiffs_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var img = Path.Combine(dir, "w.bin");
      var image = SpiffsTests.Create(profile, files);
      File.WriteAllBytes(img, image);
      var outDir = Path.Combine(dir, "out");
      var r = FsInteropToolbox.RunWsl(
        $"{tool} -u {FsInteropToolbox.WinToWsl(outDir)} -p {g.PageSize} -b {g.BlockSize} -s {image.Length} {FsInteropToolbox.WinToWsl(img)} 2>&1");
      Assert.That(r.ExitCode, Is.Zero, r.StdOut + r.StdErr);
      foreach (var (name, data) in files) {
        var path = Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar));
        Assert.That(File.Exists(path), Is.True, $"{name} not unpacked:\n{r.StdOut}");
        Assert.That(SHA256.HashData(File.ReadAllBytes(path)), Is.EqualTo(SHA256.HashData(data)), name);
      }
      Assert.That(Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).Count(), Is.EqualTo(files.Count));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
