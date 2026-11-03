using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Lib;
using FileFormat.InnoSetup;

namespace Compression.Tests.InnoSetup;

/// <summary>
/// Real installers compiled by ISCC 5.6.1 and 6.0.5 (see ReferenceVectors/README.md), checked
/// against what innoextract 1.9 extracts from them (oracle.tsv).
/// </summary>
[TestFixture]
public class InnoSetupReferenceVectorTests {

  private static readonly string[] Fixtures = [
    "inno561a-zip.exe", "inno561a-lzma2-solid.exe", "inno561u-lzma-solid.exe", "inno605-bzip.exe",
  ];

  private static byte[] Load(string name) {
    using var resource = typeof(InnoSetupReferenceVectorTests).Assembly.GetManifestResourceStream("InnoVectors." + name + ".gz")
      ?? throw new InvalidOperationException($"Missing embedded fixture {name}.");
    using var gz = new GZipStream(resource, CompressionMode.Decompress); // BCL as the test-side oracle
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, (long Size, string Sha)> Oracle(string fixture) {
    using var resource = typeof(InnoSetupReferenceVectorTests).Assembly.GetManifestResourceStream("InnoVectors.oracle.tsv")!;
    using var reader = new StreamReader(resource);
    var result = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
    while (reader.ReadLine() is { } line) {
      if (line.StartsWith('#') || line.Length == 0)
        continue;
      var f = line.Split('\t');
      if (f[0] == fixture)
        result[f[1]] = (long.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture), f[3]);
    }
    return result;
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCaseSource(nameof(Fixtures))]
  public void GivenRealInstaller_WhenListed_ThenNamesAndSizesMatchInnoextract(string fixture) {
    var oracle = Oracle(fixture);
    Assert.That(oracle, Is.Not.Empty);

    var entries = new InnoSetupFormatDescriptor().List(new MemoryStream(Load(fixture)), null);

    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(oracle.Keys));
    foreach (var e in entries)
      Assert.That(e.OriginalSize, Is.EqualTo(oracle[e.Name].Size), e.Name);
  }

  [TestCaseSource(nameof(Fixtures))]
  public void GivenRealInstaller_WhenExtracted_ThenEveryFileMatchesInnoextract(string fixture) {
    var oracle = Oracle(fixture);
    var dir = Path.Combine(Path.GetTempPath(), "cwb-inno-" + Guid.NewGuid().ToString("N"));
    try {
      new InnoSetupFormatDescriptor().Extract(new MemoryStream(Load(fixture)), dir, null, null);
      var written = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), p => Sha(File.ReadAllBytes(p)));
      Assert.That(written.Keys, Is.EquivalentTo(oracle.Keys));
      foreach (var (name, sha) in written)
        Assert.That(sha, Is.EqualTo(oracle[name].Sha), name);
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
    }
  }

  [TestCase("inno561a-zip.exe", InnoCompression.Zlib, false)]
  [TestCase("inno561a-lzma2-solid.exe", InnoCompression.Lzma2, false)]
  [TestCase("inno561u-lzma-solid.exe", InnoCompression.Lzma1, true)]
  [TestCase("inno605-bzip.exe", InnoCompression.BZip2, true)]
  public void GivenRealInstaller_WhenOpened_ThenVersionAndCompressorAreRecognised(string fixture, InnoCompression method, bool unicode) {
    var reader = new InnoSetupReader(new MemoryStream(Load(fixture)));
    Assert.Multiple(() => {
      Assert.That(reader.Compression, Is.EqualTo(method));
      Assert.That(reader.Version.IsUnicode, Is.EqualTo(unicode));
      Assert.That(reader.AppName, Is.EqualTo("Fixture"));
      Assert.That(reader.AppVersion, Is.EqualTo("1.0"));
    });
  }

  [Test]
  public void GivenRealInstallerOnDisk_WhenDetected_ThenInnoSetupIsChosen() {
    var path = Path.Combine(Path.GetTempPath(), "cwb-inno-" + Guid.NewGuid().ToString("N") + ".exe");
    try {
      File.WriteAllBytes(path, Load("inno561a-zip.exe"));
      Assert.That(FormatDetector.Detect(path), Is.EqualTo(FormatDetector.Format.InnoSetup));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void GivenInstallerWithOneCorruptedPayloadByte_WhenExtracted_ThenTheChecksumRefusesIt() {
    var data = Load("inno561a-zip.exe");
    var reader = new InnoSetupReader(new MemoryStream(data));
    var original = reader.Extract(reader.Entries.First(e => e.FileName == "app/sub/repeat.txt"));
    Assert.That(original, Has.Length.EqualTo(32000));

    // Corrupt a byte inside the last setup-1 chunk.
    var lastChunk = data.AsSpan().LastIndexOf("zlb\x1a"u8);
    Assert.That(lastChunk, Is.GreaterThan(0));
    data[lastChunk + 20] ^= 0xFF;
    var corrupted = new InnoSetupReader(new MemoryStream(data));
    Assert.That(() => corrupted.ExtractMany(corrupted.Entries, (_, _) => { }),
      Throws.InstanceOf<InvalidDataException>().Or.InstanceOf<EndOfStreamException>());
  }

  [Test]
  public void GivenInstallerCutBeforeItsHeaders_WhenListed_ThenAPartialMetadataEntryIsReturned() {
    var data = Load("inno561a-zip.exe");
    var tag = data.AsSpan().IndexOf("Inno Setup Setup Data"u8);
    Assert.That(tag, Is.GreaterThan(0));
    var cut = data.AsSpan(0, tag + 100).ToArray();
    var entries = new InnoSetupFormatDescriptor().List(new MemoryStream(cut), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
  }

  [TestCase("{app}\\bin\\x.dll", "app/bin/x.dll")]
  [TestCase("{app}\\..\\up.txt", "up.txt")]
  [TestCase("{{literal}\\a.txt", "{literal}/a.txt")]
  [TestCase("{app}\\a:b?.txt", "app/a$b$.txt")]
  [TestCase("{code:GetDir|x}\\f", "code$GetDir$x/f")]
  public void GivenDestination_WhenConverted_ThenInnoextractNamingIsUsed(string destination, string expected)
    => Assert.That(InnoSetupReader.ConvertPath(destination), Is.EqualTo(expected));

  [Test]
  public void GivenNoStream_WhenOpened_ThenArgumentNullException()
    => Assert.Throws<ArgumentNullException>(() => _ = new InnoSetupReader(null!));

  [Test]
  public void GivenRandomBytes_WhenOpened_ThenInvalidDataException()
    => Assert.That(() => new InnoSetupReader(new MemoryStream(new byte[4096])), Throws.InstanceOf<InvalidDataException>());

  /// <summary>
  /// Installers newer than any available innoextract (CWB_INNO_SELFCHECK: a directory of *.exe):
  /// every file must come out with the checksum the compiler stored for it.
  /// </summary>
  [Test]
  public void GivenLocalInstallersWithoutOracle_WhenExtracted_ThenEveryStoredChecksumMatches() {
    var dir = Environment.GetEnvironmentVariable("CWB_INNO_SELFCHECK");
    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
      Assert.Ignore("CWB_INNO_SELFCHECK not set.");
    foreach (var path in Directory.GetFiles(dir!, "*.exe")) {
      using var fs = File.OpenRead(path);
      var reader = new InnoSetupReader(fs);
      var count = 0;
      reader.ExtractMany(reader.Entries, (_, _) => ++count);
      File.AppendAllLines(Path.Combine(dir!, "selfcheck.txt"),
        [$"{Path.GetFileName(path)}: {reader.VersionText}, {count} files", .. reader.Entries.Select(e => $"  {e.FileName}\t{e.Size}")]);
      Assert.That(count, Is.EqualTo(reader.Entries.Count).And.GreaterThan(0), path);
    }
  }

  /// <summary>
  /// Further installers on this machine (set CWB_INNO_FIXTURES to a directory of *.exe with an
  /// oracle.tsv beside them, same format): every file must match innoextract.
  /// </summary>
  [Test]
  public void GivenLocalInstallers_WhenExtracted_ThenEveryFileMatchesTheirOracle() {
    var dir = Environment.GetEnvironmentVariable("CWB_INNO_FIXTURES");
    if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "oracle.tsv")))
      Assert.Ignore("CWB_INNO_FIXTURES not set.");
    var oracle = File.ReadAllLines(Path.Combine(dir!, "oracle.tsv")).Where(l => l.Length > 0 && !l.StartsWith('#'))
      .Select(l => l.Split('\t')).GroupBy(f => f[0]).ToDictionary(g => g.Key, g => g.ToDictionary(f => f[1], f => f[3]));
    foreach (var (fixture, files) in oracle) {
      using var fs = File.OpenRead(Path.Combine(dir!, fixture));
      var reader = new InnoSetupReader(fs);
      var got = new Dictionary<string, string>();
      reader.ExtractMany(reader.Entries, (e, data) => got[e.FileName] = Sha(data));
      Assert.That(got, Is.EquivalentTo(files), fixture);
    }
  }
}
