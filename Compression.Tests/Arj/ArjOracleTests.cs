#pragma warning disable CS1591
using FileFormat.Arj;

namespace Compression.Tests.Arj;

/// <summary>
/// The open-source ARJ port writes an archive with each method (1-4), and our reader must hand back
/// the original bytes. The committed reference vectors pin the same thing offline; this re-asks the
/// tool on every run so a different tool version is noticed.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class ArjOracleTests {
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

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4)]
  public void ArjMethod_WrittenByArj_ReadByUs(int method) {
    RequireTool("arj", "arj");
    var inputs = this.Inputs();
    this.Run($"arj a -m{method} -e -y out.arj {string.Join(' ', inputs.Keys)} > /dev/null");

    using var reader = new ArjReader(File.OpenRead(Path.Combine(this._dir, "out.arj")));
    // ARJ falls back to storing a member that does not shrink, so only the text is sure to use the method.
    Assert.That((int)reader.Entries.Single(e => e.FileName == "TEXT.TXT").Method, Is.EqualTo(method));
    foreach (var entry in reader.Entries)
      Assert.That(reader.ExtractEntry(entry), Is.EqualTo(inputs[entry.FileName]).AsCollection, entry.FileName);
  }
}
