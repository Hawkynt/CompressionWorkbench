#pragma warning disable CS1591
using System.Security.Cryptography;
using FileFormat.PackIt;

namespace Compression.Tests.PackIt;

/// <summary>
/// PackIt against The Unarchiver (<c>unar</c>, XADMaster), the maintained reader of the format:
/// what we write it must read, what it reads we must read the same way.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class PackItOracleTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb_pit_" + Guid.NewGuid().ToString("N")[..8]);
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

  /// <summary>Extracts <paramref name="archive"/> with unar (data forks only) and returns file name → bytes.</summary>
  private Dictionary<string, byte[]> Unar(string archive, bool requireSuccess = true) {
    var into = Path.Combine(this._dir, "unar_" + Path.GetFileNameWithoutExtension(archive));
    Directory.CreateDirectory(into);
    var r = FsInteropToolbox.RunWsl(
      $"unar -q -f -k skip -D -o {FsInteropToolbox.WinToWsl(into)} {FsInteropToolbox.WinToWsl(archive)}");
    if (requireSuccess)
      Assert.That(r.ExitCode, Is.EqualTo(0), $"unar failed:\n{r.StdOut}\n{r.StdErr}");
    return Directory.EnumerateFiles(into, "*", SearchOption.AllDirectories)
      .ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes, StringComparer.Ordinal);
  }

  [Test]
  public void ArchiveWeWrote_ExtractsInUnar() {
    RequireUnar();
    var text = "a short line of text\n"u8.ToArray();
    var random = new byte[5000];
    new Random(17).NextBytes(random);
    var path = Path.Combine(this._dir, "ours.pit");
    using (var fs = File.Create(path))
    using (var w = new PackItWriter(fs)) {
      w.AddFile("A.TXT", text);
      w.AddFile("B.BIN", random, "BINA", "????", resourceFork: "rsrc"u8.ToArray());
      w.AddFile("EMPTY", []);
    }

    var got = this.Unar(path);
    Assert.That(got["A.TXT"], Is.EqualTo(text));
    Assert.That(got["B.BIN"], Is.EqualTo(random));
    Assert.That(got["EMPTY"], Is.Empty);
  }

  [Test]
  public void HuffmanEntry_DecodesTheSameInUnarAndHere() {
    RequireUnar();
    var data = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("PackIt II Huffman, oracle check. ", 60)));
    var path = Path.Combine(this._dir, "huff.pit");
    using (var fs = File.Create(path)) {
      PackItTestEncoder.WriteCompressedEntry(fs, "HUFF.TXT", data, []);
      fs.Write("PEnd"u8);
    }

    Assert.That(this.Unar(path)["HUFF.TXT"], Is.EqualTo(data), "unar's reading of the stream");
    using var r = new PackItReader(File.OpenRead(path));
    Assert.That(r.Extract(r.Entries.Single()), Is.EqualTo(data), "our reading of the same stream");
  }

  /// <summary>
  /// Real PackIt archives, dropped into <c>test-corpus/packit/</c> (not committed: their redistribution
  /// terms are unknown). The dexvert sample set (sembiance.com/fileFormatSamples/archive/packIt) holds
  /// nine, seven of them Huffman-coded. Every data fork must match what unar extracts.
  /// </summary>
  [Test]
  public void RealArchives_InTestCorpus_MatchUnar() {
    var corpus = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus", "packit"));
    if (!Directory.Exists(corpus) || !Directory.EnumerateFiles(corpus, "*.pit").Any())
      Assert.Ignore($"No real .pit files under {corpus}.");
    RequireUnar();

    var checkedForks = 0;
    foreach (var file in Directory.EnumerateFiles(corpus, "*.pit").Order()) {
      // unar cannot open a resource-fork-only entry while skipping forks; every data fork still lands.
      var expected = this.Unar(file, requireSuccess: false);
      using var r = new PackItReader(File.OpenRead(file));
      Assert.That(r.IsIncomplete, Is.False, file);
      foreach (var e in r.Entries.Where(e => e.DataForkSize > 0)) {
        var ours = r.Extract(e);
        Assert.That(expected.TryGetValue(e.Name, out var theirs), Is.True, $"{file}: unar produced no '{e.Name}'");
        Assert.That(SHA256.HashData(ours), Is.EqualTo(SHA256.HashData(theirs!)), $"{file}: '{e.Name}'");
        ++checkedForks;
      }
    }
    Assert.That(checkedForks, Is.GreaterThan(0));
  }
}
