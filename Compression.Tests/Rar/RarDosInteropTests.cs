using System.Text;
using Compression.Registry;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// The DOS RAR releases under DOSBox, both directions: the archive each old RAR writes extracts
/// here byte for byte, and what we write in its era's format passes its <c>rar t</c> and
/// extracts byte for byte there. Needs <c>CWB_DOSBOX</c> (a dosbox-x or dosbox executable) and
/// <c>CWB_TOOL_RAR_140</c>, <c>_155</c>, <c>_200</c>, <c>_250</c> or <c>_393</c> naming that
/// version's <c>RAR.EXE</c> (for 3.93 its <c>rar32.exe</c>, beside <c>emx.exe</c>/<c>rsx.exe</c>).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class RarDosInteropTests {
  private static string? DosBox => Environment.GetEnvironmentVariable("CWB_DOSBOX") is { Length: > 0 } p && File.Exists(p) ? p : null;

  private static string? DosRar(string version)
    => Environment.GetEnvironmentVariable("CWB_TOOL_RAR_" + version) is { Length: > 0 } p && File.Exists(p) ? p : null;

  private static readonly byte[] Text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 300).Select(i => $"line {i % 37}: old rar keeps this text\r\n")));
  private static readonly byte[] Noise = MakeNoise();

  private static byte[] MakeNoise() {
    var b = new byte[12_000];
    new Random(140).NextBytes(b);
    return b;
  }

  private static Dictionary<string, byte[]> Inputs() => new(StringComparer.Ordinal) {
    ["TEXT.TXT"] = Text, ["NOISE.BIN"] = Noise, ["EMPTY.DAT"] = [],
  };

  /// <summary>Copies the DOS RAR into a fresh drive C:, runs <paramref name="lines"/> as a batch file.</summary>
  private static string RunDos(string version, string drive, IEnumerable<string> lines) {
    var exe = DosRar(version)!;
    if (version == "393") {
      foreach (var f in Directory.GetFiles(Path.GetDirectoryName(exe)!))
        if (!f.EndsWith(".sfx", StringComparison.OrdinalIgnoreCase)) File.Copy(f, Path.Combine(drive, Path.GetFileName(f)), true);
    } else
      File.Copy(exe, Path.Combine(drive, "RAR.EXE"), true);
    var bat = "@echo off\r\nset PATH=C:\\\r\n" + string.Join("\r\n", lines) + "\r\n";
    File.WriteAllBytes(Path.Combine(drive, "GO.BAT"), Encoding.ASCII.GetBytes(bat));
    var r = RarTools.Run(DosBox!, ["-silent", "-nolog", "-fastlaunch", "-c", $"mount c \"{drive}\"", "-c", "c:", "-c", "call GO.BAT", "-c", "exit"], null, timeoutMs: 300_000);
    return r.Output;
  }

  private static string Exe(string version) => version == "393" ? "rar32" : "RAR";

  private static void RequireTools(string version) {
    if (DosBox == null) Assert.Ignore("Set CWB_DOSBOX to a dosbox-x or dosbox executable.");
    if (DosRar(version) == null) Assert.Ignore($"Set CWB_TOOL_RAR_{version} to that DOS RAR.");
  }

  [TestCase("140")]
  [TestCase("155")]
  [TestCase("200")]
  [TestCase("250")]
  [TestCase("393")]
  public void Given_ArchivesAnOldRarWrites_When_WeExtractThem_Then_EveryFileMatches(string version) {
    RequireTools(version);
    var drive = RarTools.TempDir("dosrar");
    try {
      var files = Inputs();
      foreach (var (n, d) in files) File.WriteAllBytes(Path.Combine(drive, n), d);
      var list = string.Join(" ", files.Keys);
      var x = Exe(version);
      RunDos(version, drive, [
        $"{x} a -m3 M3.RAR {list} > NUL",
        $"{x} a -m0 M0.RAR {list} > NUL",
        $"{x} a -s SOLID.RAR {list} > NUL",
        $"{x} a -psecret PW.RAR {list} > NUL",
        $"{x} a -v5 VOL.RAR NOISE.BIN TEXT.TXT > NUL",
      ]);
      foreach (var (archive, password) in new[] { ("M3.RAR", (string?)null), ("M0.RAR", null), ("SOLID.RAR", null), ("PW.RAR", "secret"), ("VOL.RAR", null) }) {
        var path = Path.Combine(drive, archive);
        if (!File.Exists(path)) {
          Assert.That(archive, Is.EqualTo("VOL.RAR"), $"RAR {version} wrote no {archive}");
          continue;   // RAR 3.93 for DOS stops at its next-volume prompt under DOSBox
        }
        using var r = RarReader.Open(path, password);
        var got = RarConformanceSet.ExtractAll(r);
        foreach (var name in archive == "VOL.RAR" ? ["NOISE.BIN", "TEXT.TXT"] : files.Keys.ToArray())
          Assert.That(got[name], Is.EqualTo(files[name]), $"RAR {version} {archive}: {name}");
      }
    } finally {
      RarTools.TryDelete(drive);
    }
  }

  public static IEnumerable<TestCaseData> OursForOldRar() {
    var r15 = new Dictionary<string, string> { [FormatOptionKeys.TargetCompatibility] = "Rar1_5" };
    var r4 = new Dictionary<string, string> { [FormatOptionKeys.TargetCompatibility] = "Rar4" };
    foreach (var v in new[] { "155", "200", "250", "393" })
      yield return new TestCaseData(v, "OURS15.RAR", r15, null, 1L).SetName($"Rar{v}_Tests_OurRar15Target");
    yield return new TestCaseData("393", "OURS29.RAR", r4, null, 1L).SetName("Rar393_Tests_OurRar4");
    yield return new TestCaseData("393", "OURS29S.RAR", r4, null, 0L).SetName("Rar393_Tests_OurRar4_Solid");
    yield return new TestCaseData("393", "OURS29P.RAR", r4, "secret", 1L).SetName("Rar393_Tests_OurRar4_Password");
    yield return new TestCaseData("393", "OURS29V.RAR", new Dictionary<string, string>(r4) { [FormatOptionKeys.VolumeSize] = "8k" }, null, 1L).SetName("Rar393_Tests_OurRar4_Volumes");
  }

  [TestCaseSource(nameof(OursForOldRar))]
  public void Given_AnArchiveWeWroteInAnOldFormat_When_TheOldRarTestsAndExtractsIt_Then_ItIsAcceptedByteExact(
      string version, string name, Dictionary<string, string> fmt, string? password, long solidSize) {
    RequireTools(version);
    var drive = RarTools.TempDir("dosours");
    try {
      var files = Inputs();
      var inputs = files.Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value)).ToList();
      using (var fs = File.Create(Path.Combine(drive, name)))
        new RarFormatDescriptor().Create(fs, inputs, new FormatCreateOptions {
          Password = password, SolidSize = solidSize, FormatSpecific = new Dictionary<string, string>(fmt, StringComparer.OrdinalIgnoreCase),
        });
      Directory.CreateDirectory(Path.Combine(drive, "OUT"));
      var x = Exe(version);
      var p = password != null ? $"-p{password} " : "";
      RunDos(version, drive, [$"{x} t -y {p}{name} > TEST.LOG", "cd OUT", $"{x} x -y {p}..\\{name} > ..\\X.LOG", "cd .."]);
      var log = File.ReadAllText(Path.Combine(drive, "TEST.LOG"), Encoding.Latin1);
      Assert.That(log, Does.Contain("All OK"), $"RAR {version} t {name}:\n{log}");
      Assert.That(log, Does.Not.Contain("broken").And.Not.Contain("CRC failed").And.Not.Contain("rror"), log);
      foreach (var (n, d) in files)
        Assert.That(File.ReadAllBytes(Path.Combine(drive, "OUT", n)), Is.EqualTo(d), $"RAR {version} x {name}: {n}");
    } finally {
      RarTools.TryDelete(drive);
    }
  }
}
