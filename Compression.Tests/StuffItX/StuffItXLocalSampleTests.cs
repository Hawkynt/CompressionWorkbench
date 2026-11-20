using FileFormat.StuffItX;

namespace Compression.Tests.StuffItX;

/// <summary>
/// Real StuffIt X archives kept outside the repository (set <c>CWB_STUFFITX_SAMPLES</c> to a folder
/// of .sitx files): every data stream must decode and pass the CRC-32 the archive stores for it.
/// The streams' codings are written to the test output.
/// </summary>
[TestFixture, Category("ThemVsUs")]
public class StuffItXLocalSampleTests {
  private static IEnumerable<TestCaseData> Samples() {
    var dir = Environment.GetEnvironmentVariable("CWB_STUFFITX_SAMPLES");
    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) {
      yield return new TestCaseData("").SetName("NoLocalStuffItXSamples");
      yield break;
    }
    foreach (var f in Directory.EnumerateFiles(dir, "*.sitx").Order(StringComparer.Ordinal))
      yield return new TestCaseData(f).SetName("Sample_" + Path.GetFileNameWithoutExtension(f).Replace(' ', '_'));
  }

  [TestCaseSource(nameof(Samples))]
  public void GivenLocalSample_WhenEveryStreamIsDecoded_ThenEachPassesItsStoredCrc(string path) {
    if (path.Length == 0) Assert.Ignore("Set CWB_STUFFITX_SAMPLES to a folder of .sitx files.");
    StuffItXReader r;
    try {
      r = new StuffItXReader(File.OpenRead(path));
    } catch (NotSupportedException ex) {
      Assert.Inconclusive($"refused as a whole: {ex.Message}"); // an encrypted catalog, for one
      return;
    }
    using var _ = r;
    var refused = new List<string>();
    foreach (var id in r.StreamIds) {
      var method = r.StreamMethod(id);
      try {
        var length = r.DecodeStream(id).Length;
        TestContext.Out.WriteLine($"stream {id}: {method}, {length} bytes, ok");
      } catch (NotSupportedException ex) {
        refused.Add($"stream {id}: {method}: {ex.Message}");
      }
    }
    foreach (var line in refused) TestContext.Out.WriteLine(line);
    // a coding nobody can decode (method 7, encryption) is refused, never guessed: not a failure
    if (refused.Count > 0) Assert.Inconclusive($"{refused.Count} stream(s) use a refused coding; every other stream passed its CRC-32.");
  }

  /// <summary>Where unar can open a local sample, every data fork we extract equals unar's byte for byte.</summary>
  [TestCaseSource(nameof(Samples)), Category("ExternalInterop")]
  public void GivenLocalSampleUnarOpens_WhenExtractedByBoth_ThenDataForksMatch(string path) {
    if (path.Length == 0) Assert.Ignore("Set CWB_STUFFITX_SAMPLES to a folder of .sitx files.");
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_sitx_local_{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(tmp);
      var archive = Path.Combine(tmp, "sample.sitx"); // a plain name: sample names carry spaces and parentheses
      File.Copy(path, archive);
      var outDir = Path.Combine(tmp, "out");
      var run = FsInteropToolbox.RunWsl($"unar -q -f -k hidden -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(archive)}'");
      if (run.ExitCode != 0 || !Directory.Exists(outDir)) Assert.Ignore($"unar cannot open this sample: {run.StdErr.Trim()}");
      using var r = new StuffItXReader(File.OpenRead(path));
      var compared = 0;
      foreach (var e in r.Entries.Where(e => !e.IsDirectory && !e.IsResourceFork)) {
        var theirs = Path.Combine(outDir, e.FullPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(theirs)) continue; // unar skips what it cannot decode (method 7, encryption)
        byte[] expected;
        try {
          expected = File.ReadAllBytes(theirs);
        } catch (IOException) {
          continue; // unar made a symbolic link here, which Windows cannot open through WSL
        }
        Assert.That(r.Extract(e), Is.EqualTo(expected), e.FullPath);
        ++compared;
      }
      TestContext.Out.WriteLine($"{compared} data forks equal unar's");
      Assert.That(compared, Is.GreaterThan(0));
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }
}
