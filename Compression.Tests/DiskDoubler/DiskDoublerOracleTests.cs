#pragma warning disable CS1591
using System.Security.Cryptography;
using FileFormat.DiskDoubler;

namespace Compression.Tests.DiskDoubler;

/// <summary>
/// DiskDoubler against The Unarchiver (<c>unar</c>, XADMaster), the maintained reader of the format.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class DiskDoublerOracleTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb_dd_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static void RequireUnar() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not available.");
    if (!FsInteropToolbox.WslHasTool("unar"))
      Assert.Ignore("unar is not installed. Run inside WSL / on Linux: `sudo apt install -y unar`.");
  }

  /// <summary>Extracts with unar, data forks only; returns whether unar succeeded and the single file it wrote.</summary>
  private (bool Ok, byte[]? Data) UnarDataFork(string file) {
    var into = Path.Combine(this._dir, "unar_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(into);
    var r = FsInteropToolbox.RunWsl(
      $"unar -q -f -k skip -D -o {FsInteropToolbox.WinToWsl(into)} {FsInteropToolbox.WinToWsl(file)}");
    var files = Directory.GetFiles(into, "*", SearchOption.AllDirectories);
    return (r.ExitCode == 0, files.Length == 1 ? File.ReadAllBytes(files[0]) : null);
  }

  [Test]
  public void FileWeWrote_ExtractsInUnar() {
    RequireUnar();
    var payload = new byte[6000];
    new Random(11).NextBytes(payload);
    var path = Path.Combine(this._dir, "ours.dd");
    var w = new DiskDoublerWriter();
    w.SetFile(payload, "BINA", "????");
    using (var fs = File.Create(path))
      w.WriteTo(fs);

    var (ok, data) = this.UnarDataFork(path);
    Assert.That(ok, Is.True, "unar rejected a file we wrote");
    Assert.That(data, Is.EqualTo(payload));
  }

  /// <summary>
  /// Real DiskDoubler files, dropped into <c>test-corpus/diskdoubler/</c> (not committed: their
  /// redistribution terms are unknown). The dexvert sample set (sembiance.com/fileFormatSamples/archive/
  /// diskDoubler) covers methods 1, 6, 8 and 9. Every data fork we decode must match unar's; the ones we
  /// refuse must be refused with <see cref="NotSupportedException"/>, never returned undecoded.
  /// </summary>
  [Test]
  public void RealFiles_InTestCorpus_MatchUnarOrAreRefused() {
    var corpus = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus", "diskdoubler"));
    if (!Directory.Exists(corpus))
      Assert.Ignore($"No real DiskDoubler files under {corpus}.");
    RequireUnar();

    var decoded = 0;
    foreach (var file in Directory.EnumerateFiles(corpus).Order()) {
      var head = new byte[300];
      using (var fs = File.OpenRead(file))
        head = head[..fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)];
      if (!DiskDoublerReader.IsDiskDoubler(head))
        continue;

      using var r = new DiskDoublerReader(File.OpenRead(file));
      var data = r.Entries.FirstOrDefault(e => e.IsDataFork && e.OriginalSize > 0);
      if (data is null)
        continue;
      if (!data.IsSupported) {
        Assert.Throws<NotSupportedException>(() => r.Extract(data), Path.GetFileName(file));
        continue;
      }
      var (ok, theirs) = this.UnarDataFork(file);
      if (!ok) {
        // unar rejects the fork (the corpus's one method-1 file decodes 2 bytes short of its size and
        // fails its checksum, in unar and in gzip alike): we must reject it too, not return it.
        Assert.Throws<InvalidDataException>(() => r.Extract(data), $"{Path.GetFileName(file)}: unar refused it");
        continue;
      }
      Assert.That(theirs, Is.Not.Null, $"{Path.GetFileName(file)}: unar produced no single data fork");
      Assert.That(SHA256.HashData(r.Extract(data)), Is.EqualTo(SHA256.HashData(theirs!)),
        $"{Path.GetFileName(file)} (method {data.Method})");
      ++decoded;
    }
    if (decoded == 0)
      Assert.Ignore("No DiskDoubler file in the corpus uses a method this reader decodes.");
  }
}
