using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using FileSystem.Axfs;

namespace Compression.Tests.Axfs;

/// <summary>
/// AXFS: the reader against images <c>mkfs.axfs</c> built (see
/// <c>Axfs/ReferenceVectors/README.md</c>), compared with the manifest of the tree the tool
/// was given; plus refusals for images the reader cannot read faithfully.
/// </summary>
[TestFixture]
public class AxfsTests {

  private static byte[] Image(string name) {
    using var res = typeof(AxfsTests).Assembly.GetManifestResourceStream($"AxfsVectors.{name}.img.gz")
      ?? throw new InvalidOperationException($"{name}.img.gz not embedded");
    using var gz = new GZipStream(res, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private record Expected(char Kind, uint Mode, long Size, string? Sha, string? Target);

  private static Dictionary<string, Expected> Manifest(string name) {
    using var res = typeof(AxfsTests).Assembly.GetManifestResourceStream($"AxfsVectors.{name}.manifest")
      ?? throw new InvalidOperationException($"{name}.manifest not embedded");
    using var sr = new StreamReader(res);
    var map = new Dictionary<string, Expected>();
    foreach (var raw in sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
      var line = raw.TrimEnd('\r');
      var p = line.Split(' ');
      var mode = Convert.ToUInt32(p[1], 8);
      switch (p[0]) {
        case "D": map[p[2]] = new('D', mode, 0, null, null); break;
        case "L": map[p[2]] = new('L', mode, 0, null, line[(line.IndexOf(" -> ", StringComparison.Ordinal) + 4)..]); break;
        default: map[p[4]] = new('F', mode, long.Parse(p[2]), p[3], null); break;
      }
    }
    return map;
  }

  [TestCase("axfs-compressed")]
  [TestCase("axfs-xip")]
  public void GivenMkfsAxfsImage_WhenRead_ThenTreeModesAndBytesMatchTheSource(string name) {
    var r = new AxfsReader(new MemoryStream(Image(name), writable: false));
    var expected = Manifest(name);
    Assert.That(r.Entries.Select(e => e.Path), Is.EquivalentTo(expected.Keys));
    Assert.Multiple(() => {
      foreach (var e in r.Entries) {
        var x = expected[e.Path];
        Assert.That(e.Mode & 0xFFF, Is.EqualTo(x.Mode), $"{e.Path}: permission bits");
        switch (x.Kind) {
          case 'D': Assert.That(e.IsDirectory, Is.True, e.Path); break;
          case 'L':
            Assert.That(e.IsSymlink, Is.True, e.Path);
            Assert.That(System.Text.Encoding.UTF8.GetString(r.Extract(e)), Is.EqualTo(x.Target), $"{e.Path}: link target");
            break;
          default:
            Assert.That(e.IsFile, Is.True, e.Path);
            var data = r.Extract(e);
            Assert.That(data.LongLength, Is.EqualTo(x.Size), $"{e.Path}: size");
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(x.Sha), $"{e.Path}: content");
            break;
        }
      }
    });
  }

  [Test]
  public void GivenXipImage_WhenRead_ThenTheElfExecutableComesFromTheXipRegion() {
    // mkfs.axfs -a places pages of owner-executable ELF files in the XIP region; the
    // compressed image of the same tree has none, so the two images differ there.
    var xip = new AxfsReader(new MemoryStream(Image("axfs-xip"))).CountNodes();
    var plain = new AxfsReader(new MemoryStream(Image("axfs-compressed"))).CountNodes();
    Assert.That(xip.Xip, Is.EqualTo(1), "the 7004-byte ELF file: its one full page is XIP, its partial tail is not");
    Assert.That(plain.Xip, Is.Zero);
    Assert.That(plain.ByteAligned, Is.GreaterThan(0), "incompressible pages");
    Assert.That(plain.Compressed, Is.GreaterThan(0), "compressible pages");
  }

  [Test]
  public void GivenImage_WhenListedThroughDescriptor_ThenSymlinkCarriesItsTarget() {
    var d = new AxfsFormatDescriptor();
    var list = d.List(new MemoryStream(Image("axfs-compressed")), null);
    var link = list.Single(e => e.Name == "bin/motd-link");
    Assert.That(link.IsSymlink, Is.True);
    Assert.That(link.LinkTarget, Is.EqualTo("../etc/motd"));
    Assert.That(d.ExtractEntryToMemory(new MemoryStream(Image("axfs-compressed")), "etc/motd", null), Is.EqualTo("hello axfs\n"u8.ToArray()));
  }

  [Test]
  public void GivenImage_WhenDetectedByMagic_ThenAxfs()
    => Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Image("axfs-compressed")).ToString(), Is.EqualTo("Axfs"));

  // ── refusals ──────────────────────────────────────────────────────────

  [Test]
  public void GivenWrongMagic_WhenOpened_ThenInvalidData() {
    var img = Image("axfs-compressed");
    img[0] ^= 0xFF;
    Assert.Throws<InvalidDataException>(() => new AxfsReader(new MemoryStream(img)));
  }

  [Test]
  public void GivenUnknownVersion_WhenOpened_ThenRefused() {
    var img = Image("axfs-compressed");
    img[240] = 3;
    Assert.Throws<InvalidDataException>(() => new AxfsReader(new MemoryStream(img)));
  }

  [Test]
  public void GivenNonZlibCompressionType_WhenOpened_ThenRefused() {
    var img = Image("axfs-compressed");
    img[243] = 1;
    Assert.Throws<InvalidDataException>(() => new AxfsReader(new MemoryStream(img)));
  }

  [Test]
  public void GivenTruncatedImage_WhenRead_ThenRefusedNotGuessed() {
    var img = Image("axfs-compressed").AsSpan(0, 2000).ToArray();
    Assert.Catch(() => {
      var r = new AxfsReader(new MemoryStream(img));
      foreach (var e in r.Entries) r.Extract(e);
    });
  }

  [Test]
  public void GivenCorruptCblock_WhenExtracted_ThenRefused() {
    var img = Image("axfs-compressed");
    // The compressed region descriptor's fsoffset (superblock field at 120 points at it).
    var desc = (int)BinaryPrimitives.ReadUInt64BigEndian(img.AsSpan(120));
    var start = (int)BinaryPrimitives.ReadUInt64BigEndian(img.AsSpan(desc));
    for (var i = start + 2; i < start + 40; ++i) img[i] ^= 0x5A;
    var r = new AxfsReader(new MemoryStream(img));
    var manifest = Manifest("axfs-compressed");
    var refused = 0;
    foreach (var e in r.Entries.Where(e => e.IsFile)) {
      byte[] data;
      try { data = r.Extract(e); } catch (InvalidDataException) { ++refused; continue; }
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(manifest[e.Path].Sha),
        $"{e.Path}: a file the reader returns must be the right bytes");
    }
    Assert.That(refused, Is.GreaterThan(0), "the damaged cblock's file is refused");
  }
}
