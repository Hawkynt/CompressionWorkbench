#pragma warning disable CS1591
using FileFormat.Xar;

namespace Compression.Tests.Xar;

/// <summary>
/// libarchive's bsdtar writes a XAR archive with each encoding, and our reader must hand back the
/// original bytes. The committed reference vectors pin the same thing offline; this re-asks the
/// tool on every run so a different tool version is noticed.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class XarOracleTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb_theirs_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static void RequireTool(string tool, string package) {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not available.");
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"{tool} is not installed. Run inside WSL / on Linux: `sudo apt install -y {package}`.");
  }

  private Dictionary<string, byte[]> Inputs() {
    var text = string.Concat(Enumerable.Range(1, 400).Select(i => $"line {i:D5}: the quick brown fox jumps over the lazy dog\n"));
    var random = new byte[3000];
    new Random(23).NextBytes(random);
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["TEXT.TXT"] = System.Text.Encoding.ASCII.GetBytes(text),
      ["RANDOM.BIN"] = random,
      ["REPEAT.BIN"] = [.. random, .. random],
      ["ONE.TXT"] = "a"u8.ToArray(),
    };
    foreach (var (name, bytes) in files)
      File.WriteAllBytes(Path.Combine(this._dir, name), bytes);
    return files;
  }

  private void Run(string command) {
    var r = FsInteropToolbox.RunWsl($"cd {FsInteropToolbox.WinToWsl(this._dir)} && {command}");
    Assert.That(r.ExitCode, Is.EqualTo(0), $"{command}\n{r.StdOut}\n{r.StdErr}");
  }

  [TestCase("none", "none")]
  [TestCase("gzip", "zlib")]
  [TestCase("bzip2", "bzip2")]
  [TestCase("lzma", "lzma")]
  [TestCase("xz", "xz")]
  public void XarEncoding_WrittenByBsdtar_ReadByUs(string compression, string method) {
    RequireTool("bsdtar", "libarchive-tools");
    var inputs = this.Inputs();
    this.Run($"bsdtar --format xar --options xar:compression={compression} -cf out.xar {string.Join(' ', inputs.Keys)}");

    using var fs = File.OpenRead(Path.Combine(this._dir, "out.xar"));
    var reader = new XarReader(fs);
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory)) {
      if (entry.FileName == "TEXT.TXT")
        Assert.That(entry.Method, Is.EqualTo(method));
      Assert.That(reader.Extract(entry), Is.EqualTo(inputs[entry.FileName]).AsCollection, entry.FileName);
    }
  }
}
