using System.IO.Compression;
using System.Security.Cryptography;
using FileSystem.Chfs;

namespace Compression.Tests.Chfs;

/// <summary>
/// CHFS: the reader against images NetBSD 11.1's <c>makefs -t chfs</c> built (see
/// <c>Chfs/ReferenceVectors/README.md</c>), compared with the manifest NetBSD took of the
/// source tree; plus refusals.
/// </summary>
[TestFixture]
public class ChfsTests {

  private static byte[] Image(string name) {
    using var res = typeof(ChfsTests).Assembly.GetManifestResourceStream($"ChfsVectors.{name}.img.gz")
      ?? throw new InvalidOperationException($"{name}.img.gz not embedded");
    using var gz = new GZipStream(res, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private record Expected(char Kind, uint Mode, long Size, string? Sha, string? Target);

  private static Dictionary<string, Expected> Manifest() {
    using var res = typeof(ChfsTests).Assembly.GetManifestResourceStream("ChfsVectors.chfs.manifest")
      ?? throw new InvalidOperationException("chfs.manifest not embedded");
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

  [TestCase("chfs-nand", true, 2048, 131072)]
  [TestCase("chfs-nor", false, 512, 16384)]
  public void GivenMakefsImage_WhenRead_ThenTreeModesAndBytesMatchTheSource(string name, bool nand, int page, int eraseBlock) {
    var r = new ChfsReader(new MemoryStream(Image(name), writable: false));
    Assert.That((r.IsNand, r.PageSize, r.EraseBlockSize), Is.EqualTo((nand, page, eraseBlock)));
    var expected = Manifest();
    // makefs copies a name without its terminator and then measures it with strlen, so the
    // directory entry for "empty-dir" was written with 14 bytes, "empty-dirempty", the
    // name CRC covering all 14. The image holds that name; the reader reports what is stored.
    expected["empty-dirempty"] = expected["empty-dir"];
    expected.Remove("empty-dir");
    Assert.That(r.Entries.Select(e => e.Path), Is.EquivalentTo(expected.Keys));
    Assert.Multiple(() => {
      foreach (var e in r.Entries) {
        var x = expected[e.Path];
        switch (x.Kind) {
          case 'D':
            Assert.That(e.IsDirectory, Is.True, e.Path);
            Assert.That(e.Mode & 0xFFF, Is.EqualTo(x.Mode), $"{e.Path}: mode");
            break;
          case 'L': Assert.That(System.Text.Encoding.UTF8.GetString(e.Data), Is.EqualTo(x.Target), e.Path); break;
          default:
            Assert.That(e.Mode & 0xFFF, Is.EqualTo(x.Mode), $"{e.Path}: mode");
            Assert.That(e.Data.LongLength, Is.EqualTo(x.Size), e.Path);
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(e.Data)), Is.EqualTo(x.Sha), e.Path);
            break;
        }
      }
    });
  }

  [Test]
  public void GivenImage_WhenDetectedByMagic_ThenChfs()
    => Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Image("chfs-nand")).ToString(), Is.EqualTo("Chfs"));

  [Test]
  public void GivenFlippedDataByte_WhenRead_ThenRefused() {
    var img = Image("chfs-nor");
    var at = img.AsSpan().IndexOf("echo start"u8);
    img[at] ^= 0x20;
    Assert.Throws<InvalidDataException>(() => new ChfsReader(new MemoryStream(img)));
  }

  [Test]
  public void GivenDamagedEraseBlockHeader_WhenRead_ThenRefused() {
    var img = Image("chfs-nand");
    img[2048 + 4] ^= 0x01;   // the NAND eraseblock header's lid
    Assert.Throws<InvalidDataException>(() => new ChfsReader(new MemoryStream(img)));
  }

  [Test]
  public void GivenWrongMagic_WhenOpened_ThenInvalidData() {
    var img = Image("chfs-nand");
    img[0] = 0;
    Assert.Throws<InvalidDataException>(() => new ChfsReader(new MemoryStream(img)));
  }
}
