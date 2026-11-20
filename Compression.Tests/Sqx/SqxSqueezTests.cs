using Compression.Core.Dictionary.Sqx;
using FileFormat.Sqx;

namespace Compression.Tests.Sqx;

/// <summary>
/// Squeez CommandLine 5.2 (CWB_SQC: its Linux binary, run in WSL) against our reader and writer on
/// files large enough for match distances past 256 KiB, where the distance codes depend on the
/// dictionary size: every method of both formats, in both directions.
/// </summary>
[TestFixture]
[Category("EndToEnd")]
public sealed class SqxSqueezTests {
  private static readonly Dictionary<string, byte[]> Inputs = Make();

  private static Dictionary<string, byte[]> Make() {
    var rnd = new Random(3);
    var words = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 60_000).Select(i => $"word {i % 977} {i * 31 % 1013}\n")));
    return new() {
      ["r300.bin"] = Enumerable.Range(0, 300_000).Select(_ => (byte)rnd.Next(256)).ToArray(),
      ["t300.txt"] = words[..300_000],
      ["t700.txt"] = words[..700_000],
    };
  }

  private static string Sqc() {
    var sqc = Environment.GetEnvironmentVariable("CWB_SQC");
    if (string.IsNullOrEmpty(sqc)) Assert.Ignore("Set CWB_SQC to Squeez CommandLine 5.2 for Linux (sqc_linux.tgz of the SQX SDK mirror).");
    return sqc!;
  }

  [Test]
  public void Given_SqueezArchivesOfLargeFiles_When_Read_Then_EveryFileIsTheOriginal() {
    var sqc = Sqc();
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_sqxsq_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var results = new List<string>();
    try {
      foreach (var (name, data) in Inputs) File.WriteAllBytes(Path.Combine(dir, name), data);
      var w = FsInteropToolbox.WinToWsl(dir);
      var tmp = $"/tmp/cwb_sqxsq_{Guid.NewGuid():N}";
      var cmds = from f in new[] { "SQX1", "SQX2" } from m in new[] { 1, 2, 3, 4 } from n in Inputs.Keys
                 select $"timeout 60 {sqc} a -q -ep -fmt {f} -m{m} {tmp}/{n}.{f}.m{m}.sqx {tmp}/{n} < /dev/null > /dev/null 2>&1";
      FsInteropToolbox.RunWsl($"mkdir -p {tmp} && cp {w}/*.bin {w}/*.txt {tmp}/ && cd {tmp} && {string.Join(" ; ", cmds)} ; cp {tmp}/*.sqx {w}/; rm -rf {tmp}");
      var archives = Directory.GetFiles(dir, "*.sqx").Order().ToList();
      Assert.That(archives, Has.Count.EqualTo(24));
      foreach (var path in archives) {
        try {
          using var reader = new SqxReader(File.OpenRead(path));
          var e = reader.Entries[0];
          results.Add($"{Path.GetFileName(path)}: {(reader.ExtractEntry(e).AsSpan().SequenceEqual(Inputs[e.FileName]) ? "same" : "DIFFERENT")}");
        } catch (Exception x) {
          results.Add($"{Path.GetFileName(path)}: {x.GetType().Name} {x.Message}");
        }
      }
    } finally { Directory.Delete(dir, true); }
    Assert.That(results, Is.All.EndsWith("same"), string.Join("\n", results));
  }

  [Test]
  public void Given_OurArchivesOfLargeFiles_When_SqueezTestsAndExtractsThem_Then_EveryFileIsTheOriginal() {
    var sqc = Sqc();
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_sqxsq_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var names = new List<string>();
      foreach (var format in new[] { SqxTables.Format11, SqxTables.Format20 })
        foreach (var dict in new[] { 256 << 10, 1 << 20, 4 << 20 })
          for (byte method = 1; method <= 4; method += 3) {
            var writer = new SqxWriter(method, dict, format);
            foreach (var (name, data) in Inputs) writer.AddFile(name, data);
            var file = $"ours_{format}_{dict >> 10}k_m{method}.sqx";
            File.WriteAllBytes(Path.Combine(dir, file), writer.ToArray());
            names.Add(file);
          }
      var w = FsInteropToolbox.WinToWsl(dir);
      var tmp = $"/tmp/cwb_sqxsq_{Guid.NewGuid():N}";
      var cmds = names.Select(n => $"mkdir -p {tmp}/{n}.out && timeout 60 {sqc} x -q -o {tmp}/{n} {tmp}/{n}.out/ < /dev/null > /dev/null 2>&1; echo \"{n} RC=$?\"; for f in {string.Join(' ', Inputs.Keys)}; do cmp -s {tmp}/{n}.out/$f {w}/$f && echo \"{n} $f same\" || echo \"{n} $f DIFFERENT\"; done");
      foreach (var (name, data) in Inputs) File.WriteAllBytes(Path.Combine(dir, name), data);
      var run = FsInteropToolbox.RunWsl($"mkdir -p {tmp} && cp {w}/*.sqx {tmp}/ && cd {tmp} && {string.Join(" ; ", cmds)} ; rm -rf {tmp}");
      var lines = run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      Assert.That(lines.Where(l => l.Contains("RC=")), Is.All.EndsWith("RC=0"), run.StdOut);
      Assert.That(lines.Where(l => !l.Contains("RC=")).ToList(), Has.Count.EqualTo(names.Count * Inputs.Count).And.All.EndsWith("same"), run.StdOut);
    } finally { Directory.Delete(dir, true); }
  }
}
