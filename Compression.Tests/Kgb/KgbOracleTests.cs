using System.Security.Cryptography;
using FileFormat.Kgb;

namespace Compression.Tests.Kgb;

/// <summary>
/// Live round trips against Debian's <c>kgb</c> 1.0b4+ds (apt package <c>kgb</c>), run in WSL or
/// natively on Linux: it must extract what we write byte for byte, and we must extract what it
/// writes. Without the tool every test here is ignored.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class KgbOracleTests {
  private string _tmp = null!;

  [SetUp]
  public void Setup() {
    if (!FsInteropToolbox.WslHasTool("kgb"))
      Assert.Ignore("kgb is not installed (apt install kgb).");
    this._tmp = Path.Combine(Path.GetTempPath(), $"cwb_kgb_oracle_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static List<(string Name, byte[] Data)> Files(int seed) {
    var rng = new Random(seed);
    var random = new byte[7000];
    rng.NextBytes(random);
    var call = new byte[6000];
    for (var i = 0; i < call.Length; i += 5) {
      call[i] = 0xE8;
      var target = rng.Next(-4000, 4000);
      for (var k = 1; k < 5 && i + k < call.Length; ++k)
        call[i + k] = (byte)(target >> (8 * (k - 1)));
    }
    return [("notes.txt", KgbTests.Text(500)), ("random.bin", random), ("sub/call.exe", call), ("empty", [])];
  }

  [TestCase(0)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(5)]
  [TestCase(6)]
  [TestCase(7)]
  [TestCase(9)]
  public void OurArchive_KgbExtractsByteIdentical(int level) {
    var files = Files(level);
    var archive = Path.Combine(this._tmp, "ours.kgb");
    using (var fs = File.Create(archive))
      KgbArchive.Write(fs, level, files);

    // kgb does not create directories, and extracting on a Windows-mounted drive trips over
    // directory times, so it runs in a Linux temporary directory.
    var run = FsInteropToolbox.RunWsl(
      $"d=$(mktemp -d) && cd \"$d\" && mkdir sub && kgb {FsInteropToolbox.WinToWsl(archive)} >/dev/null && " +
      "sha256sum notes.txt random.bin sub/call.exe empty; rc=$?; rm -rf \"$d\"; exit $rc");
    Assert.That(run.ExitCode, Is.Zero, run.StdErr + run.StdOut);
    var sums = run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.Split("  ", 2)).ToDictionary(p => p[1].Trim(), p => p[0]);
    foreach (var (name, data) in files)
      Assert.That(sums[name], Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(data))), name);
  }

  [TestCase(1)]
  [TestCase(4)]
  public void KgbArchive_WeExtractByteIdentical(int level) {
    var files = Files(10 + level);
    var src = Path.Combine(this._tmp, "src");
    foreach (var (name, data) in files) {
      var path = Path.Combine(src, name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllBytes(path, data);
    }
    var archive = Path.Combine(this._tmp, "theirs.kgb");
    var run = FsInteropToolbox.RunWsl(
      $"cd {FsInteropToolbox.WinToWsl(src)} && kgb -{level} {FsInteropToolbox.WinToWsl(archive)} " +
      string.Join(' ', files.Select(f => f.Name)) + " >/dev/null");
    Assert.That(run.ExitCode, Is.Zero, run.StdErr + run.StdOut);

    using var fs = File.OpenRead(archive);
    var kgb = KgbArchive.Read(fs);
    Assert.That(kgb.Level, Is.EqualTo(level));
    var got = new List<(string, byte[])>();
    kgb.Decode(fs, _ => true, (e, d) => got.Add((e.Name, d)));
    Assert.That(got, Is.EqualTo(files));
  }
}
