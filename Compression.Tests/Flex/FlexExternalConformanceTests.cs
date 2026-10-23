using Compression.Registry;
using FileSystem.Flex;

namespace Compression.Tests.Flex;

/// <summary>
/// Two-way oracle for FLEX disks with <c>flexfloppy</c> (laurent-fr/flextools, used as a
/// black box): it must catalogue and extract what we create and edit, and we must read
/// what it formats and adds — including into a disk we wrote.
/// </summary>
/// <remarks>
/// Skips unless <c>flexfloppy</c> is found in WSL/Linux, at <c>$CWB_FLEXFLOPPY</c>, on the
/// PATH, or at <c>~/flex/flextools/flexfloppy/flexfloppy</c> (where
/// <c>Flex/ReferenceVectors/generate.sh</c> expects it).
/// </remarks>
[TestFixture]
[Category("ExternalFsInterop")]
[Category("Wsl")]
public sealed class FlexExternalConformanceTests {

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), $"cwb_flex_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, true); } catch { /* best effort */ }
  }

  private static string? _tool;

  private static string Tool() {
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("WSL/Linux shell not available.");
    _tool ??= FsInteropToolbox.RunWsl(
      "for c in \"$CWB_FLEXFLOPPY\" \"$(command -v flexfloppy)\" \"$HOME/flex/flextools/flexfloppy/flexfloppy\"; do " +
      "[ -n \"$c\" ] && [ -x \"$c\" ] && { echo \"$c\"; break; }; done").StdOut.Trim();
    if (_tool.Length == 0) Assert.Ignore("flexfloppy (https://github.com/laurent-fr/flextools) is not installed.");
    return _tool;
  }

  private static string W(string p) => FsInteropToolbox.WinToWsl(p);

  private (int Code, string Out) Ff(string args) {
    var r = FsInteropToolbox.RunWsl($"'{Tool()}' {args} 2>&1");
    return (r.ExitCode, r.StdOut + r.StdErr);
  }

  private static byte[] Padded(byte[] data) {
    var p = new byte[Math.Max(1, (data.Length + 251) / 252) * 252];
    data.CopyTo(p, 0);
    return p;
  }

  [Test]
  public void GivenOurDisk_WhenFlexfloppyExtractsIt_ThenEveryFileMatches() {
    var files = new Dictionary<string, byte[]> {
      ["HELLO.TXT"] = "HELLO\r"u8.ToArray(),
      ["ONE.BIN"] = new byte[252],
      ["BIG.DAT"] = Enumerable.Range(0, 5000).Select(i => (byte)(i * 13)).ToArray(),
    };
    var img = Path.Combine(this._tmp, "ours.dsk");
    using (var fs = File.Create(img))
      new FlexFormatDescriptor().Create(fs, files.Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value)).ToList(),
        new FormatCreateOptions { FormatSpecific = FormatCreateOptions.FormatSpecificFrom([new("VolumeLabel", "OURS")]) });

    var cat = this.Ff($"--in {W(img)} --cat");
    Assert.That(cat.Code, Is.Zero, cat.Out);
    Assert.That(cat.Out, Does.Contain("OURS"));
    this.AssertFlexfloppyExtracts(img, files.ToDictionary(kv => kv.Key, kv => Padded(kv.Value)));
  }

  [Test]
  public void GivenFlexfloppyDisk_WhenWeAddAndRemoveAndFlexfloppyAddsMore_ThenBothSidesAgree() {
    var img = Path.Combine(this._tmp, "theirs.dsk");
    var src = Path.Combine(this._tmp, "THEIRS.TXT");
    File.WriteAllBytes(src, Enumerable.Range(0, 700).Select(i => (byte)('A' + i % 26)).ToArray());
    Assert.That(this.Ff($"--new --tracks 40 --sectors 18 --label THEIRS --out {W(img)}").Code, Is.Zero);
    Assert.That(this.Ff($"--in {W(img)} --add {W(src)}").Code, Is.Zero);

    // We read what flexfloppy wrote …
    var disk = new FlexDisk(new MemoryStream(File.ReadAllBytes(img)));
    var theirs = disk.Entries.Single(e => e.FullName == "THEIRS.TXT");
    Assert.That(disk.Extract(theirs).AsSpan(0, 700).ToArray(), Is.EqualTo(File.ReadAllBytes(src)));

    // … edit it in place …
    var ours = Enumerable.Range(0, 3000).Select(i => (byte)(i ^ 0x5A)).ToArray();
    using (var fs = File.Open(img, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new FlexFormatDescriptor();
      d.Add(fs, [ArchiveInputInfo.InMemory("OURS.BIN", ours), ArchiveInputInfo.InMemory("GONE.TXT", [1, 2, 3])]);
      d.Remove(fs, ["GONE.TXT"]);
    }

    // … and flexfloppy adds into our edit and extracts everything.
    var later = Path.Combine(this._tmp, "LATER.TXT");
    File.WriteAllBytes(later, "added by flexfloppy after us\r"u8.ToArray());
    var add = this.Ff($"--in {W(img)} --add {W(later)}");
    Assert.That(add.Code, Is.Zero, add.Out);
    var extracted = this.AssertFlexfloppyExtracts(img, new() { ["OURS.BIN"] = Padded(ours) });
    Assert.That(extracted.Keys, Is.EquivalentTo(new[] { "THEIRS.TXT", "OURS.BIN", "LATER.TXT" }), "GONE.TXT stays deleted for flexfloppy");

    var after = new FlexDisk(new MemoryStream(File.ReadAllBytes(img)));
    Assert.That(after.Extract(after.Entries.Single(e => e.FullName == "LATER.TXT")).AsSpan(0, 29).ToArray(), Is.EqualTo(File.ReadAllBytes(later)));
    Assert.That(after.Entries.Select(e => e.FullName), Is.EquivalentTo(extracted.Keys));
  }

  /// <summary>Runs <c>flexfloppy --extract</c> and checks the given files; returns everything it extracted.</summary>
  private Dictionary<string, byte[]> AssertFlexfloppyExtracts(string img, Dictionary<string, byte[]> expected) {
    var outDir = Path.Combine(this._tmp, $"x_{Guid.NewGuid():N}");
    Directory.CreateDirectory(outDir);
    var r = this.Ff($"--in {W(img)} --extract {W(outDir)}");
    Assert.That(r.Code, Is.Zero, r.Out);
    var got = Directory.GetFiles(outDir).ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes);
    foreach (var (n, d) in expected) {
      Assert.That(got.ContainsKey(n), Is.True, $"flexfloppy did not extract {n}:\n{r.Out}");
      Assert.That(got[n], Is.EqualTo(d), $"{n} as flexfloppy extracts it");
    }
    return got;
  }
}
