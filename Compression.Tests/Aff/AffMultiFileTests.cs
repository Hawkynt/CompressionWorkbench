using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Aff;

namespace Compression.Tests.Aff;

/// <summary>
/// AFFLIB's multi-file layouts, written by affconvert (afflib-tools 3.7.20, see
/// MultiFileVectors/make-fixtures.sh): an AFD directory of two <c>file_NNN.aff</c> and an AFM
/// sidecar with four split raw files. Both must rebuild the source medium (oracle.tsv).
/// </summary>
[TestFixture]
public sealed class AffMultiFileTests {
  private static readonly string[] Members = [
    "ev.afd_file_000.aff", "ev.afd_file_001.aff", "ev.afm", "ev.000", "ev.001", "ev.002", "ev.003",
  ];

  private string _dir = null!;

  private static byte[] Load(string name) {
    using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("AffMultiFileVectors." + name + ".gz")
      ?? throw new FileNotFoundException(name);
    using var gz = new GZipStream(resource, CompressionMode.Decompress); // BCL as the test-side oracle
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static (string Sha, long Size) Source() {
    using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("AffMultiFileVectors.oracle.tsv")!;
    using var reader = new StreamReader(resource);
    var row = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r').Split('\t')).First(f => f[0] == "disk.raw");
    return (row[1], long.Parse(row[2]));
  }

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb-aff-multi-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(this._dir, "ev.afd"));
    foreach (var m in Members) {
      var target = m.StartsWith("ev.afd_", StringComparison.Ordinal)
        ? Path.Combine(this._dir, "ev.afd", m["ev.afd_".Length..])
        : Path.Combine(this._dir, m);
      File.WriteAllBytes(target, Load(m));
    }
  }

  [TearDown]
  public void TearDown() {
    if (Directory.Exists(this._dir))
      Directory.Delete(this._dir, recursive: true);
  }

  private string Afd(int n) => Path.Combine(this._dir, "ev.afd", $"file_{n:000}.aff");
  private string Afm => Path.Combine(this._dir, "ev.afm");

  private static string ExtractDisk(string path) {
    var outDir = Path.Combine(Path.GetDirectoryName(path)!, "x-" + Guid.NewGuid().ToString("N"));
    using (var s = File.OpenRead(path))
      new AffFormatDescriptor().Extract(s, outDir, null, [AffFormatDescriptor.DiskName]);
    return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(outDir, AffFormatDescriptor.DiskName))));
  }

  [TestCase(0)]
  [TestCase(1)]
  public void GivenAnyFileOfAnAfdDirectory_WhenExtracted_ThenTheWholeMediumComesBack(int member)
    => Assert.That(ExtractDisk(this.Afd(member)), Is.EqualTo(Source().Sha));

  [Test]
  public void GivenAnAfdDirectory_WhenListed_ThenTheMediumHasItsFullSize() {
    using var s = File.OpenRead(this.Afd(0));
    var entries = new AffFormatDescriptor().List(s, null);
    Assert.That(entries.Single(e => e.Name == AffFormatDescriptor.DiskName).OriginalSize, Is.EqualTo(Source().Size));
  }

  [Test]
  public void GivenAnAfmSidecar_WhenExtracted_ThenItsRawFilesRebuildTheMedium()
    => Assert.That(ExtractDisk(this.Afm), Is.EqualTo(Source().Sha));

  [Test]
  public void GivenAnAfmWithoutItsRawFiles_WhenExtracted_ThenItIsRefused() {
    var afm = File.ReadAllBytes(this.Afm);
    var listed = new AffFormatDescriptor().List(new MemoryStream(afm), null);
    Assert.That(listed.Single(e => e.Name == AffFormatDescriptor.DiskName).OriginalSize, Is.EqualTo(Source().Size));
    Assert.Throws<NotSupportedException>(() =>
      new AffFormatDescriptor().Extract(new MemoryStream(afm), Path.Combine(this._dir, "y"), null, [AffFormatDescriptor.DiskName]));
  }

  [Test]
  public void GivenAnAfmWithAMissingMiddleRawFile_WhenExtracted_ThenItIsRefused() {
    File.Delete(Path.Combine(this._dir, "ev.001"));
    Assert.Throws<InvalidDataException>(() => ExtractDisk(this.Afm));
  }

  [Test]
  public void GivenAnAfmWhoseRawFileWasAltered_WhenExtracted_ThenTheStoredHashRefusesIt() {
    var part = Path.Combine(this._dir, "ev.002");
    var bytes = File.ReadAllBytes(part);
    bytes[1000] ^= 0xFF;
    File.WriteAllBytes(part, bytes);
    Assert.Throws<InvalidDataException>(() => ExtractDisk(this.Afm));
  }

  [Test]
  public void GivenAnAfdWhoseFirstFileIsMissing_WhenALaterFileIsOpened_ThenItIsNotTakenAsTheImage() {
    File.Delete(this.Afd(0));
    using var s = File.OpenRead(this.Afd(1));
    var names = new AffFormatDescriptor().List(s, null).Select(e => e.Name);
    Assert.That(names, Does.Contain("FULL.aff"));
  }

  [Test]
  public void GivenAnAfdMember_WhenTheImageIsReplaced_ThenItIsRefusedAndUnchanged() {
    var before = File.ReadAllBytes(this.Afd(0));
    using (var s = new FileStream(this.Afd(0), FileMode.Open, FileAccess.ReadWrite))
      Assert.Throws<NotSupportedException>(() => new AffFormatDescriptor().Add(s,
        [ArchiveInputInfo.InMemory(AffFormatDescriptor.DiskName, new byte[4096])]));
    Assert.That(File.ReadAllBytes(this.Afd(0)), Is.EqualTo(before));
  }
}
