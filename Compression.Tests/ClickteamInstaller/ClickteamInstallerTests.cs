using System.Security.Cryptography;
using FileFormat.ClickteamInstaller;

namespace Compression.Tests.ClickteamInstaller;

/// <summary>
/// Clickteam Install Creator: real installers dropped into <c>test-corpus/clickteam/</c> (not
/// committed: third-party software; the sembiance.com fileFormatSamples "clickTeam" set) must
/// yield exactly the files cicdec 3.0.1 extracts from them, as <c>cicdec-manifest.tsv</c> records
/// (made by <c>mkmanifest.py</c>); installers cicdec does not extract completely are refused here
/// (FULL.exe and metadata.ini) rather than half-read. Without the corpus those cases are ignored.
/// </summary>
[TestFixture]
public sealed class ClickteamInstallerTests {
  private sealed record Row(string Installer, string Entry, long Size, string Sha256);

  private static readonly Lazy<Row[]> Manifest = new(() => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "ClickteamInstaller", "cicdec-manifest.tsv"))
    .Split('\n')
    .Select(l => l.TrimEnd('\r'))
    .Where(l => l.Length > 0 && !l.StartsWith('#'))
    .Select(l => l.Split('\t'))
    .Select(f => new Row(f[0], f[1], long.Parse(f[2]), f[3]))
    .ToArray());

  private static string CorpusDir => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus", "clickteam"));

  private static IEnumerable<TestCaseData> Installers() {
    if (!Directory.Exists(CorpusDir))
      yield break;
    foreach (var path in Directory.GetFiles(CorpusDir).Order(StringComparer.Ordinal))
      yield return new TestCaseData(path).SetName("cicdec agrees on " + Path.GetFileName(path));
  }

  private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCaseSource(nameof(Installers))]
  [Category("ThirdPartyProof")]
  public void Given_ARealInstaller_When_Extracted_Then_TheFilesAreThoseCicdecExtracts(string path) {
    var bytes = File.ReadAllBytes(path);
    var expected = Manifest.Value.Where(r => r.Installer == Hash(bytes)).ToArray();
    Assert.That(expected, Is.Not.Empty, "the installer is not in the manifest; run mkmanifest.py");
    var descriptor = new ClickteamInstallerFormatDescriptor();
    using var stream = new MemoryStream(bytes);
    if (expected[0].Entry == "!") {
      // cicdec cannot extract it; this reader must not pretend to (it may still list files it
      // can decode, but must not hand out wrong bytes).
      ClickteamReader? reader = null;
      try {
        reader = ClickteamReader.Open(bytes);
      } catch (Exception e) when (e is InvalidDataException or NotSupportedException) {
        Assert.That(descriptor.List(stream, null).Select(e => e.Name), Is.EqualTo(new[] { "FULL.exe", "metadata.ini" }));
        return;
      }
      foreach (var entry in reader.Entries)
        Assert.That(() => reader.Extract(entry), Throws.InstanceOf<InvalidDataException>().Or.InstanceOf<NotSupportedException>(), entry.Path);
      return;
    }
    Assert.That(Compression.Lib.FormatDetector.Detect(path), Is.EqualTo(Compression.Lib.FormatDetector.Format.ClickteamInstaller), "detected");
    var opened = ClickteamReader.Open(bytes);
    var got = opened.Entries.ToDictionary(e => e.Path, e => opened.Extract(e));
    Assert.Multiple(() => {
      Assert.That(got.Keys, Is.EquivalentTo(expected.Select(r => r.Entry)));
      foreach (var row in expected)
        if (got.TryGetValue(row.Entry, out var data)) {
          Assert.That(data.LongLength, Is.EqualTo(row.Size), row.Entry);
          Assert.That(Hash(data), Is.EqualTo(row.Sha256), row.Entry);
        }
    });
    Assert.That(descriptor.List(stream, null).Select(e => e.Name), Is.EquivalentTo(got.Keys));
  }

  [Test]
  public void Given_AnExecutableWithoutTheOverlay_When_Listed_Then_TheProblemIsReportedNotThrown() {
    var exe = new byte[1024];
    exe[0] = (byte)'M';
    exe[1] = (byte)'Z';
    var listed = new ClickteamInstallerFormatDescriptor().List(new MemoryStream(exe), null);
    Assert.That(listed.Select(e => e.Name), Is.EqualTo(new[] { "FULL.exe", "metadata.ini" }));
    Assert.That(ClickteamReader.IsClickteam(new MemoryStream(exe)), Is.False);
  }

  [Test]
  public void Given_AnOverlayWithoutAFileList_When_Opened_Then_ItIsRefused() {
    byte[] data = [0x4D, 0x5A, .. new byte[100], 0x77, 0x77, 0x67, 0x54, 0x29, 0x48, .. new byte[80]];
    Assert.Throws<NotSupportedException>(() => ClickteamReader.Open(data));
  }
}
