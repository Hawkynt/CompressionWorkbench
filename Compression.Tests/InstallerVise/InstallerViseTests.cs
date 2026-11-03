using System.Security.Cryptography;
using System.Text;
using FileFormat.InstallerVise;

namespace Compression.Tests.InstallerVise;

/// <summary>
/// Installer VISE: real installers dropped into <c>test-corpus/installer-vise/</c> (not committed:
/// third-party software; the 38 files of the sembiance.com fileFormatSamples "installerVISE" set)
/// must yield exactly the forks unvise extracts from them, as recorded in
/// <c>unvise-manifest.tsv</c> by <c>mkmanifest.py</c>; installers unvise refuses are refused here too.
/// Without the corpus those cases are ignored; the transport and catalog helpers are checked always.
/// </summary>
[TestFixture]
public sealed class InstallerViseTests {
  private sealed record Row(string Installer, string Entry, long Size, string Sha256);

  // Names can hold CR ("Icon\r") and tabs, so lines split on LF only and fields from both ends.
  private static readonly Lazy<Row[]> Manifest = new(() => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "InstallerVise", "unvise-manifest.tsv"))
    .Split('\n')
    .Select(l => l.TrimEnd('\r')) // a checkout may turn LF into CRLF; names keep their inner CRs
    .Where(l => l.Length > 0 && !l.StartsWith('#'))
    .Select(l => {
      var first = l.IndexOf('\t');
      var last = l.LastIndexOf('\t');
      var middle = l.LastIndexOf('\t', last - 1);
      return new Row(l[..first], l[(first + 1)..middle], long.Parse(l[(middle + 1)..last]), l[(last + 1)..]);
    })
    .ToArray());

  private static string CorpusDir => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus", "installer-vise"));

  private static IEnumerable<TestCaseData> Installers() {
    if (!Directory.Exists(CorpusDir))
      yield break;
    foreach (var path in Directory.GetFiles(CorpusDir).Order(StringComparer.Ordinal)) {
      if (path.EndsWith(".data", StringComparison.Ordinal) && File.Exists(path[..^5]))
        continue;
      yield return new TestCaseData(path).SetName("unvise agrees on " + Path.GetFileName(path));
    }
  }

  private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCaseSource(nameof(Installers))]
  [Category("ThirdPartyProof")]
  public void Given_ARealInstaller_When_Extracted_Then_TheForksAreThoseUnviseExtracts(string path) {
    var digest = Hash(File.ReadAllBytes(path));
    var expected = Manifest.Value.Where(r => r.Installer == digest).ToArray();
    Assert.That(expected, Is.Not.Empty, "the installer is not in the manifest; run mkmanifest.py");
    var descriptor = new InstallerViseFormatDescriptor();
    using var stream = File.OpenRead(path);
    if (expected[0].Entry == "!") {
      var listed = descriptor.List(stream, null);
      Assert.That(listed.Select(e => e.Name), Is.EqualTo(new[] { "FULL.bin", "metadata.ini" }), "refused as unvise refuses it");
      return;
    }
    // Compared in memory: a Windows file system cannot hold names like "Icon\r" or "Read me?".
    stream.Position = 0;
    var got = InstallerViseFormatDescriptor.Open(stream).Extract()
      .ToDictionary(f => InstallerViseFormatDescriptor.EntryName(f), f => f.Data);
    Assert.Multiple(() => {
      Assert.That(got.Keys, Is.EquivalentTo(expected.Select(r => r.Entry)));
      foreach (var row in expected)
        if (got.TryGetValue(row.Entry, out var data)) {
          Assert.That(data.LongLength, Is.EqualTo(row.Size), row.Entry);
          Assert.That(Hash(data), Is.EqualTo(row.Sha256), row.Entry);
        }
    });
    stream.Position = 0;
    var names = descriptor.List(stream, null).Where(e => !e.IsDirectory).Select(e => e.Name);
    Assert.That(names, Is.EquivalentTo(got.Keys));
  }

  [Test]
  public void Given_AMacBinaryWrapper_When_Unwrapped_Then_BothForksComeOut() {
    var data = "SVCT payload"u8.ToArray();
    var resource = "resource fork"u8.ToArray();
    var mb = new byte[128 + 128 + 128];
    mb[1] = 4;
    "Inst"u8.CopyTo(mb.AsSpan(2));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mb.AsSpan(83), (uint)data.Length);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(mb.AsSpan(87), (uint)resource.Length);
    data.CopyTo(mb, 128);
    resource.CopyTo(mb, 256);
    var (d, r) = ViseTransport.Unwrap(mb, null);
    Assert.That(d, Is.EqualTo(data));
    Assert.That(r, Is.EqualTo(resource));
    Assert.That(ViseInstaller.LooksLikeVise(mb));
  }

  [Test]
  public void Given_MacRomanNames_When_Converted_Then_TheyAreUnicode()
    => Assert.That(ViseCatalog.FromMacRoman([0x45, 0x56, 0x20, 0xC4, 0xA5, 0xF0, 0x8A]), Is.EqualTo("EV ƒ•ä"));

  [Test]
  public void Given_ADataForkWithoutItsResourceFork_When_Listed_Then_TheProblemIsReportedNotThrown() {
    var fork = new byte[64];
    "SVCT"u8.CopyTo(fork);
    var listed = new InstallerViseFormatDescriptor().List(new MemoryStream(fork), null);
    Assert.That(listed.Select(e => e.Name), Is.EqualTo(new[] { "FULL.bin", "metadata.ini" }));
    var dir = Directory.CreateTempSubdirectory("cwb-vise-").FullName;
    try {
      new InstallerViseFormatDescriptor().Extract(new MemoryStream(fork), dir, null, null);
      Assert.That(File.ReadAllText(Path.Combine(dir, "metadata.ini"), Encoding.UTF8), Does.Contain("resource fork").And.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void Given_ADamagedBinHexFork_When_Unwrapped_Then_TheCrcRefusesIt() {
    var text = "(This file must be converted with BinHex 4.0)\r\n:!!!!:"u8.ToArray();
    Assert.Throws<InvalidDataException>(() => ViseTransport.Unwrap(text, null));
  }
}
