#pragma warning disable CS1591
using FileFormat.Lzh;

namespace Compression.Tests.Lzh;

/// <summary>
/// Every method and header level our LHA writer produces must extract in lhasa (<c>lha</c>), the
/// reference reader of the LHA family; and archives lhasa's own test suite holds must read here.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class LhaOracleTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb_lha_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static void RequireLha() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not available.");
    if (!FsInteropToolbox.WslHasTool("lha"))
      Assert.Ignore("lha is not installed. Run inside WSL / on Linux: `sudo apt install -y lhasa`.");
  }

  private static Dictionary<string, byte[]> Inputs() {
    var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(
      Enumerable.Range(1, 2500).Select(i => $"line {i:D5}: the quick brown fox jumps over the lazy dog #{i % 37}\n")));
    var random = new byte[9000];
    new Random(41).NextBytes(random);
    return new(StringComparer.Ordinal) {
      ["TEXT.TXT"] = text,                   // > 64 KiB: tree rebuilds, window wraps
      ["RANDOM.BIN"] = random,               // incompressible: stored fallback
      ["REPEAT.BIN"] = [.. random, .. random, .. random],
      ["ONE.TXT"] = "a"u8.ToArray(),
      ["EMPTY.TXT"] = [],
    };
  }

  public static IEnumerable<TestCaseData> WritableCases() {
    string[] methods = [
      LhaConstants.MethodLh0, LhaConstants.MethodLh1, LhaConstants.MethodLh4, LhaConstants.MethodLh5,
      LhaConstants.MethodLh6, LhaConstants.MethodLh7, LhaConstants.MethodLz4, LhaConstants.MethodLzs,
      LhaConstants.MethodLz5, LhaConstants.MethodPm0,
    ];
    foreach (var method in methods)
      foreach (var level in new[] { LhaHeaderLevel.Level0, LhaHeaderLevel.Level1, LhaHeaderLevel.Level2 })
        yield return new TestCaseData(method, level).SetName($"Writer_{method.Trim('-')}_{level}_ExtractsInLhasa");
  }

  [TestCaseSource(nameof(WritableCases))]
  public void Writer_Output_ExtractsInLhasa(string method, LhaHeaderLevel level) {
    RequireLha();
    var inputs = Inputs();
    var generation = method is LhaConstants.MethodLz4 or LhaConstants.MethodLzs or LhaConstants.MethodLz5
      ? LhaArchiverGeneration.LArc
      : method == LhaConstants.MethodPm0 ? LhaArchiverGeneration.PmArc1 : LhaArchiverGeneration.Extended;
    var writer = new LhaWriter(method, generation, level);
    foreach (var (name, data) in inputs)
      writer.AddFile(name, data);
    var archive = Path.Combine(this._dir, "ours.lzh");
    File.WriteAllBytes(archive, writer.ToArray());

    var into = Path.Combine(this._dir, "out");
    Directory.CreateDirectory(into);
    var test = FsInteropToolbox.RunWsl($"lha t {FsInteropToolbox.WinToWsl(archive)}");
    Assert.That(test.ExitCode, Is.EqualTo(0), $"lha t rejected our {method} {level} archive:\n{test.StdOut}\n{test.StdErr}");
    var x = FsInteropToolbox.RunWsl($"cd {FsInteropToolbox.WinToWsl(into)} && lha xq {FsInteropToolbox.WinToWsl(archive)}");
    Assert.That(x.ExitCode, Is.EqualTo(0), $"lha x failed:\n{x.StdOut}\n{x.StdErr}");
    foreach (var (name, data) in inputs) {
      var path = Path.Combine(into, name);
      if (!File.Exists(path))
        path = Directory.EnumerateFiles(into, "*", SearchOption.AllDirectories)
          .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) ?? path;
      Assert.That(File.Exists(path), Is.True, $"lha did not extract {name}");
      Assert.That(File.ReadAllBytes(path), Is.EqualTo(data).AsCollection, name);
    }
  }
}
