using System.Diagnostics;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// The command line reaches RAR volumes and passwords: <c>cwb create … -v 16k -p …</c> writes a
/// real volume set, and <c>cwb extract … -p …</c> reads it back from its first volume.
/// </summary>
[TestFixture]
[Category("End2End")]
public class RarCliTests {
  private static string LocateCli() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("The CLI project builds the win-x64 host executable.");
#if DEBUG
    const string configuration = "Debug";
#else
    const string configuration = "Release";
#endif
    var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    var path = Path.Combine(root, "Compression.CLI", "bin", configuration, "net10.0", "win-x64", "cwb.exe");
    if (!File.Exists(path)) Assert.Ignore($"CLI executable was not built at {path}.");
    return path;
  }

  private static (int Code, string Output) Cli(string cli, string cwd, params string[] args) {
    var psi = new ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = cwd };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, output);
  }

  [TestCase("set.part1.rar", null)]
  [TestCase("set.part1.rar", "--opt TargetCompatibility=Rar4")]
  [TestCase("set.rar", "--opt TargetCompatibility=Rar4")]
  public void Given_VolumeAndPasswordOptions_When_CreatedAndExtractedByTheCli_Then_TheSetRoundTrips(string name, string? extra) {
    var cli = LocateCli();
    var dir = RarTools.TempDir("rarcli");
    try {
      var data = new byte[60_000];
      new Random(7).NextBytes(data);
      File.WriteAllBytes(Path.Combine(dir, "data.bin"), data);
      var args = new List<string> { "create", name, "data.bin", "-v", "16k", "-p", "secret" };
      if (extra != null) args.AddRange(extra.Split(' '));
      var create = Cli(cli, dir, [.. args]);
      Assert.That(create.Code, Is.Zero, create.Output);
      Assert.That(Directory.GetFiles(dir, "set*").Length, Is.GreaterThan(2), "volumes");

      using (var reader = RarReader.Open(Path.Combine(dir, name), "secret"))
        Assert.That(reader.VolumeCount, Is.GreaterThan(2));

      var extract = Cli(cli, dir, "extract", name, "-o", "out", "-p", "secret");
      Assert.That(extract.Code, Is.Zero, extract.Output);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "out", "data.bin")), Is.EqualTo(data));

      var wrong = Cli(cli, dir, "extract", name, "-o", "out2", "-p", "wrong");
      Assert.That(wrong.Code, Is.Not.Zero, "a wrong password must fail");
      Assert.That(File.Exists(Path.Combine(dir, "out2", "data.bin")), Is.False, "no garbage left behind");
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  [TestCase(false)]
  [TestCase(true)]
  public void Given_AFolderWithAHiddenFile_When_TheCliArchivesIt_Then_IncludeHiddenDecidesWhetherItIsTaken(bool includeHidden) {
    var cli = LocateCli();
    var dir = RarTools.TempDir("rarclihidden");
    try {
      var src = Path.Combine(dir, "src");
      Directory.CreateDirectory(src);
      File.WriteAllText(Path.Combine(src, "shown.txt"), "shown");
      var hidden = Path.Combine(src, ".hidden.txt");
      File.WriteAllText(hidden, "hidden");
      File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
      var args = new List<string> { "create", "h.rar", "src" };
      if (includeHidden) args.Add("--include-hidden");
      var create = Cli(cli, dir, [.. args]);
      Assert.That(create.Code, Is.Zero, create.Output);
      using var reader = RarReader.Open(Path.Combine(dir, "h.rar"), null);
      var names = reader.Entries.Select(e => e.Name.Replace('\\', '/')).ToList();
      Assert.That(names, Does.Contain("src/shown.txt"));
      Assert.That(names.Contains("src/.hidden.txt"), Is.EqualTo(includeHidden), string.Join(", ", names));
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  [TestCase("Rar5")]
  [TestCase("Rar4")]
  public void Given_ARecoveryRecordOption_When_TheArchiveIsDamaged_Then_CwbRepairRestoresIt(string target) {
    var cli = LocateCli();
    var dir = RarTools.TempDir("rarclirr");
    try {
      var data = new byte[120_000];
      new Random(3).NextBytes(data);
      File.WriteAllBytes(Path.Combine(dir, "data.bin"), data);
      var create = Cli(cli, dir, "create", "p.rar", "data.bin", "-m", "store", "--opt", "RecoveryRecord=5", "--opt", "TargetCompatibility=" + target);
      Assert.That(create.Code, Is.Zero, create.Output);
      var original = File.ReadAllBytes(Path.Combine(dir, "p.rar"));
      Assert.That(Cli(cli, dir, "repair", "p.rar", "--check").Code, Is.Zero, "intact");

      var damaged = (byte[])original.Clone();
      for (var i = 3_000; i < 3_400; ++i) damaged[i] ^= 0x33;
      File.WriteAllBytes(Path.Combine(dir, "p.rar"), damaged);
      Assert.That(Cli(cli, dir, "repair", "p.rar", "--check").Code, Is.EqualTo(1), "damaged");
      var repair = Cli(cli, dir, "repair", "p.rar");
      Assert.That(repair.Code, Is.Zero, repair.Output);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "fixed.p.rar")), Is.EqualTo(original));
    } finally {
      RarTools.TryDelete(dir);
    }
  }

  [TestCase("Rar5")]
  [TestCase("Rar4")]
  public void Given_RecoveryVolumes_When_AVolumeIsLost_Then_CwbRepairRebuildsIt(string target) {
    var cli = LocateCli();
    var dir = RarTools.TempDir("rarclirev");
    try {
      var data = new byte[90_000];
      new Random(4).NextBytes(data);
      File.WriteAllBytes(Path.Combine(dir, "data.bin"), data);
      var create = Cli(cli, dir, "create", "s.part1.rar", "data.bin", "-m", "store", "-v", "20k", "--opt", "RecoveryVolumes=1", "--opt", "TargetCompatibility=" + target);
      Assert.That(create.Code, Is.Zero, create.Output);
      Assert.That(File.Exists(Path.Combine(dir, "s.part1.rev")), Is.True, create.Output);
      var original = File.ReadAllBytes(Path.Combine(dir, "s.part2.rar"));
      File.Delete(Path.Combine(dir, "s.part2.rar"));
      Assert.That(Cli(cli, dir, "repair", "s.part1.rar", "--check").Code, Is.EqualTo(1));
      var repair = Cli(cli, dir, "repair", "s.part1.rar");
      Assert.That(repair.Code, Is.Zero, repair.Output);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "s.part2.rar")), Is.EqualTo(original));
    } finally {
      RarTools.TryDelete(dir);
    }
  }
}
