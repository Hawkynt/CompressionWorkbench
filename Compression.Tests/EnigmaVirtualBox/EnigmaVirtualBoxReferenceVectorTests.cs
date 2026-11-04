using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Lib;
using FileFormat.EnigmaVirtualBox;

namespace Compression.Tests.EnigmaVirtualBox;

/// <summary>
/// Executables Enigma Virtual Box boxed (see ReferenceVectors/README.md), checked against what
/// evbunpack extracts from them (oracle.tsv).
/// </summary>
[TestFixture]
public class EnigmaVirtualBoxReferenceVectorTests {

  private static byte[] Load(string fixture) {
    using var resource = typeof(EnigmaVirtualBoxReferenceVectorTests).Assembly.GetManifestResourceStream("EnigmaVirtualBoxVectors." + fixture + ".gz")!;
    using var gz = new GZipStream(resource, CompressionMode.Decompress); // BCL as the test-side oracle
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, (long Size, string Sha)> Oracle(string fixture) {
    using var resource = typeof(EnigmaVirtualBoxReferenceVectorTests).Assembly.GetManifestResourceStream("EnigmaVirtualBoxVectors.oracle.tsv")!;
    using var reader = new StreamReader(resource);
    var result = new Dictionary<string, (long, string)>();
    while (reader.ReadLine() is { } line)
      if (line.Length > 0 && !line.StartsWith('#') && line.Split('\t') is var f && f[0] == fixture)
        result[f[1]] = (long.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture), f[3]);
    return result;
  }

  private static readonly string[] Fixtures = ["x64-evb1100.exe", "x86-evb970.exe", "x86-evb780.exe", "evb-stored.exe", "evb-compressed.exe"];

  [TestCaseSource(nameof(Fixtures))]
  public void GivenBox_WhenListed_ThenFilesAndSizesMatchEvbunpack(string fixture) {
    var oracle = Oracle(fixture);
    var entries = new EnigmaVirtualBoxFormatDescriptor().List(new MemoryStream(Load(fixture)), null).Where(e => !e.IsDirectory).ToList();
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(oracle.Keys));
    foreach (var e in entries)
      Assert.That(e.OriginalSize, Is.EqualTo(oracle[e.Name].Size), e.Name);
  }

  [TestCaseSource(nameof(Fixtures))]
  public void GivenBox_WhenExtracted_ThenEveryFileMatchesEvbunpack(string fixture) {
    var oracle = Oracle(fixture);
    var dir = Path.Combine(Path.GetTempPath(), "cwb-evb-" + Guid.NewGuid().ToString("N"));
    try {
      new EnigmaVirtualBoxFormatDescriptor().Extract(new MemoryStream(Load(fixture)), dir, null, null);
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

  [TestCase("x64-evb1100.exe", false)]
  [TestCase("x86-evb970.exe", false)]
  [TestCase("x86-evb780.exe", true)]
  [TestCase("evb-stored.exe", false)]
  public void GivenBox_WhenOpened_ThenItsTableLayoutIsRecognised(string fixture, bool legacy)
    => Assert.That(new EnigmaVirtualBoxReader(new MemoryStream(Load(fixture))).IsLegacyLayout, Is.EqualTo(legacy));

  [Test]
  public void GivenNestedBox_WhenOpened_ThenFoldersPrecedeTheirFilesAndOnlyNonEmptyFilesAreCompressed() {
    var stored = new EnigmaVirtualBoxReader(new MemoryStream(Load("evb-stored.exe")));
    var compressed = new EnigmaVirtualBoxReader(new MemoryStream(Load("evb-compressed.exe")));
    Assert.Multiple(() => {
      Assert.That(stored.Entries.Select(e => (e.FileName, e.IsDirectory)), Is.EqualTo(new[] {
        ("hello.txt", false), ("empty.txt", false), ("data", true), ("data/random.bin", false), ("data/sub", true), ("data/sub/pattern.bin", false),
      }));
      Assert.That(stored.Entries.Any(e => e.IsCompressed), Is.False);
      Assert.That(compressed.Entries.Where(e => e.IsCompressed).Select(e => e.FileName), Is.EquivalentTo(new[] { "hello.txt", "data/random.bin", "data/sub/pattern.bin" }));
      // pattern.bin spans four 64 KB chunks, random.bin two (aPLib makes random data larger).
      Assert.That(compressed.Entries.Single(e => e.FileName == "data/sub/pattern.bin").StoredSize, Is.LessThan(5000));
      Assert.That(compressed.Entries.Single(e => e.FileName == "data/random.bin").StoredSize, Is.GreaterThan(70000));
    });
  }

  [Test]
  public void GivenBoxWithADamagedChunk_WhenExtracted_ThenItIsRefused() {
    var data = Load("evb-compressed.exe");
    var reader = new EnigmaVirtualBoxReader(new MemoryStream(data));
    var pattern = reader.Entries.Single(e => e.FileName == "data/sub/pattern.bin");
    // Cut the first chunk's length in the table: the chunks no longer add up.
    data[pattern.Offset + 8] ^= 0x40;
    var damaged = new EnigmaVirtualBoxReader(new MemoryStream(data));
    Assert.That(() => damaged.Extract(damaged.Entries.Single(e => e.FileName == pattern.FileName)), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  public void GivenBoxCutInsideItsTable_WhenListed_ThenAPartialMetadataEntryIsReturned() {
    var data = Load("evb-stored.exe");
    var header = (int)new EnigmaVirtualBoxReader(new MemoryStream(data)).HeaderOffset;
    var entries = new EnigmaVirtualBoxFormatDescriptor().List(new MemoryStream(data[..(header + 200)]), null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
  }

  [Test]
  public void GivenNonBoxes_WhenProbed_ThenTheyAreNotEnigmaVirtualBoxes() {
    var withoutSection = Load("evb-stored.exe");
    // Rename ".enigma1" so the header is no longer where the loader keeps it.
    var at = withoutSection.AsSpan().IndexOf(".enigma1"u8);
    withoutSection[at + 1] = (byte)'x';
    Assert.Multiple(() => {
      Assert.That(EnigmaVirtualBoxReader.IsEnigmaVirtualBox(new MemoryStream(new byte[4096])), Is.False);
      Assert.That(EnigmaVirtualBoxReader.IsEnigmaVirtualBox(new MemoryStream("MZ"u8.ToArray())), Is.False);
      Assert.That(EnigmaVirtualBoxReader.IsEnigmaVirtualBox(new MemoryStream(withoutSection)), Is.False);
      Assert.That(EnigmaVirtualBoxReader.IsEnigmaVirtualBox(new MemoryStream(Load("x86-evb780.exe"))), Is.True);
    });
  }

  [Test]
  public void GivenBoxOnDisk_WhenDetected_ThenEnigmaVirtualBoxIsChosen() {
    var path = Path.Combine(Path.GetTempPath(), "cwb-evb-" + Guid.NewGuid().ToString("N") + ".exe");
    try {
      try {
        File.WriteAllBytes(path, Load("evb-stored.exe"));
        _ = File.ReadAllBytes(path).Length;
      } catch (IOException ex) {
        Assert.Inconclusive("The machine's malware scanner keeps the boxed executable off the disk: " + ex.Message);
      }
      Assert.That(FormatDetector.Detect(path), Is.EqualTo(FormatDetector.Format.EnigmaVirtualBox));
    } finally {
      File.Delete(path);
    }
  }
}
