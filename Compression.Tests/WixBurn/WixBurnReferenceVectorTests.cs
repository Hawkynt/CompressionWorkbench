using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Lib;
using FileFormat.WixBurn;

namespace Compression.Tests.WixBurn;

/// <summary>
/// A bundle built by WiX 3.14 candle/light (see ReferenceVectors/README.md), checked against what
/// WiX's own dark.exe -x extracts from it (oracle.tsv).
/// </summary>
[TestFixture]
public class WixBurnReferenceVectorTests {

  private const string Fixture = "burn-v3.exe";

  private static byte[] Load() {
    using var resource = typeof(WixBurnReferenceVectorTests).Assembly.GetManifestResourceStream("WixBurnVectors." + Fixture + ".gz")!;
    using var gz = new GZipStream(resource, CompressionMode.Decompress); // BCL as the test-side oracle
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, (long Size, string Sha)> Oracle() {
    using var resource = typeof(WixBurnReferenceVectorTests).Assembly.GetManifestResourceStream("WixBurnVectors.oracle.tsv")!;
    using var reader = new StreamReader(resource);
    var result = new Dictionary<string, (long, string)>();
    while (reader.ReadLine() is { } line)
      if (line.Length > 0 && !line.StartsWith('#') && line.Split('\t') is var f && f[0] == Fixture)
        result[f[1]] = (long.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture), f[3]);
    return result;
  }

  [Test]
  public void GivenBundle_WhenListed_ThenNamesAndSizesMatchDark() {
    var oracle = Oracle();
    var entries = new WixBurnFormatDescriptor().List(new MemoryStream(Load()), null);
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(oracle.Keys));
    foreach (var e in entries)
      Assert.That(e.OriginalSize, Is.EqualTo(oracle[e.Name].Size), e.Name);
  }

  [Test]
  public void GivenBundle_WhenExtracted_ThenEveryPayloadMatchesDark() {
    var oracle = Oracle();
    var dir = Path.Combine(Path.GetTempPath(), "cwb-burn-" + Guid.NewGuid().ToString("N"));
    try {
      new WixBurnFormatDescriptor().Extract(new MemoryStream(Load()), dir, null, null);
      var written = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(dir, p).Replace('\\', '/'), p => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))));
      Assert.That(written.Keys, Is.EquivalentTo(oracle.Keys));
      foreach (var (name, sha) in written)
        Assert.That(sha, Is.EqualTo(oracle[name].Sha), name);
    } finally {
      if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public void GivenBundle_WhenOpened_ThenTheSectionHeaderIsRead() {
    var reader = new WixBurnReader(new MemoryStream(Load()));
    Assert.Multiple(() => {
      Assert.That(reader.SectionVersion, Is.EqualTo(2u));
      Assert.That(reader.ContainerCount, Is.EqualTo(2));
      Assert.That(reader.StubSize, Is.GreaterThan(0));
    });
  }

  [Test]
  public void GivenBundleOnDisk_WhenDetected_ThenWixBurnIsChosen() {
    var path = Path.Combine(Path.GetTempPath(), "cwb-burn-" + Guid.NewGuid().ToString("N") + ".exe");
    try {
      File.WriteAllBytes(path, Load());
      Assert.That(FormatDetector.Detect(path), Is.EqualTo(FormatDetector.Format.WixBurn));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void GivenBundleWithACorruptedPayload_WhenExtracted_ThenTheManifestHashRefusesIt() {
    var data = Load();
    var reader = new WixBurnReader(new MemoryStream(data));
    var repeat = reader.Entries.Single(e => e.FileName == "AttachedContainer/data/repeat.txt");
    // The attached container is the last one; flip a byte of its stored payload area.
    data[^20] ^= 0x55;
    var damaged = new WixBurnReader(new MemoryStream(data));
    Assert.That(() => damaged.Extract(damaged.Entries.Single(e => e.FileName == repeat.FileName)), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  public void GivenBundleCutBeforeItsContainers_WhenListed_ThenAPartialMetadataEntryIsReturned() {
    var data = Load();
    var stub = (int)new WixBurnReader(new MemoryStream(data)).StubSize;
    var entries = new WixBurnFormatDescriptor().List(new MemoryStream(data[..(stub + 10)]), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
  }

  [Test]
  public void GivenPlainPe_WhenProbed_ThenItIsNotABundle()
    => Assert.That(WixBurnFormatDescriptor.IsWixBurn(new MemoryStream(new byte[512])), Is.False);
}
