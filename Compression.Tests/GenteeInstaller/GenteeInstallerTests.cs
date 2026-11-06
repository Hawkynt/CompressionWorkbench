using System.Security.Cryptography;
using FileFormat.GenteeInstaller;

namespace Compression.Tests.GenteeInstaller;

/// <summary>
/// Gentee launcher installers. Real setups dropped into <c>test-corpus/gentee/</c> (not committed:
/// CreateInstall's own setups 6.0.0, 7.5.0, 8.0.0, 8.4.0 and 8.5.0 and CreateInstall Light 8.5.0
/// from createinstall.com, which CreateInstall builds) must extract every file of their GEA
/// archives against the CRC-32 CreateInstall recorded for it, and list what
/// <c>gentee-manifest.tsv</c> pins. Without the corpus those cases are ignored.
/// </summary>
[TestFixture]
public sealed class GenteeInstallerTests {
  private sealed record Row(string Installer, int Entries, int ArchiveEntries, string ListingSha256);

  private static readonly Lazy<Row[]> Manifest = new(() => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "GenteeInstaller", "gentee-manifest.tsv"))
    .Split('\n')
    .Select(l => l.TrimEnd('\r'))
    .Where(l => l.Length > 0 && !l.StartsWith('#'))
    .Select(l => l.Split('\t'))
    .Select(f => new Row(f[0], int.Parse(f[1]), int.Parse(f[2]), f[3]))
    .ToArray());

  private static string CorpusDir => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus", "gentee"));

  private static IEnumerable<TestCaseData> Installers() {
    if (!Directory.Exists(CorpusDir))
      yield break;
    foreach (var path in Directory.GetFiles(CorpusDir).Order(StringComparer.Ordinal))
      yield return new TestCaseData(path).SetName("every CRC-32 matches in " + Path.GetFileName(path));
  }

  private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  /// <summary>The listing as names, sizes and content hashes, one per line, hashed.</summary>
  private static string Listing(IEnumerable<(GenteeInstallerEntry Entry, byte[] Data)> files) =>
    Hash(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', files.Select(f => $"{f.Entry.Path}\t{f.Data.Length}\t{Hash(f.Data)}"))));

  [TestCaseSource(nameof(Installers))]
  [Category("ThirdPartyProof")]
  public void Given_ARealInstaller_When_Extracted_Then_EveryFileMatchesTheCrcCreateInstallRecorded(string path) {
    var bytes = File.ReadAllBytes(path);
    var row = Manifest.Value.SingleOrDefault(r => r.Installer == Hash(bytes));
    Assert.That(row, Is.Not.Null, "the installer is not in the manifest");
    Assert.That(Compression.Lib.FormatDetector.Detect(path), Is.EqualTo(Compression.Lib.FormatDetector.Format.GenteeInstaller), "detected");

    var reader = GenteeInstallerReader.Open(bytes);
    // ExtractAll checks every archive file against its recorded CRC-32 and throws on a mismatch.
    var files = reader.ExtractAll().ToList();
    var archived = reader.Entries.Count(e => e.Path.StartsWith("temp/", StringComparison.Ordinal) || e.Path.StartsWith("setup/", StringComparison.Ordinal));
    Assert.Multiple(() => {
      Assert.That(files, Has.Count.EqualTo(row!.Entries));
      Assert.That(archived, Is.EqualTo(row.ArchiveEntries));
      Assert.That(files.Single(f => f.Entry.Path == "gentee.dll").Data.AsSpan(0, 2).SequenceEqual("MZ"u8), Is.True, "the runtime is a PE image");
      Assert.That(Listing(files), Is.EqualTo(row.ListingSha256));
    });
  }

  [Test]
  public void Given_AnExecutableWithoutTheLauncherHeader_When_Listed_Then_TheProblemIsReportedNotThrown() {
    var exe = new byte[4096];
    exe[0] = (byte)'M';
    exe[1] = (byte)'Z';
    "Gentee Launcher\0"u8.CopyTo(exe.AsSpan(512)); // a header that does not point at itself
    Assert.That(GenteeInstallerReader.IsGentee(new MemoryStream(exe)), Is.False);
    var listed = new GenteeInstallerFormatDescriptor().List(new MemoryStream(exe), null);
    Assert.That(listed.Select(e => e.Name), Is.EqualTo(new[] { "FULL.exe", "metadata.ini" }));
  }

  [Test]
  public void Given_ADamagedLzgeStream_When_Decoded_Then_ItIsRefused() {
    var output = new byte[64];
    Assert.Throws<InvalidDataException>(() => Lzge.Decode([0xFF, 0xFF, 0xFF], output, 0, output.Length));
  }
}
