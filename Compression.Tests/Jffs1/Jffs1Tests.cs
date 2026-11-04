using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using FileSystem.Jffs1;

namespace Compression.Tests.Jffs1;

/// <summary>
/// JFFS v1: the reader against images <c>mkfs.jffs</c> built in both byte orders (see
/// <c>Jffs1/ReferenceVectors/README.md</c>), compared with the manifest of the source tree;
/// plus the refusals for logs it will not replay.
/// </summary>
[TestFixture]
public class Jffs1Tests {

  private static byte[] Image(string name) {
    using var res = typeof(Jffs1Tests).Assembly.GetManifestResourceStream($"Jffs1Vectors.{name}.img.gz")
      ?? throw new InvalidOperationException($"{name}.img.gz not embedded");
    using var gz = new GZipStream(res, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private record Expected(char Kind, uint Mode, long Size, string? Sha, string? Target);

  private static Dictionary<string, Expected> Manifest(string name) {
    using var res = typeof(Jffs1Tests).Assembly.GetManifestResourceStream($"Jffs1Vectors.{name}.manifest")
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

  [TestCase("jffs1-le", false)]
  [TestCase("jffs1-be", true)]
  public void GivenMkfsJffsImage_WhenRead_ThenTreeModesAndBytesMatchTheSource(string name, bool bigEndian) {
    var r = new Jffs1Reader(new MemoryStream(Image(name), writable: false));
    Assert.That(r.BigEndian, Is.EqualTo(bigEndian));
    var expected = Manifest(name);
    Assert.That(r.Entries.Select(e => e.Path), Is.EquivalentTo(expected.Keys));
    Assert.Multiple(() => {
      foreach (var e in r.Entries) {
        var x = expected[e.Path];
        Assert.That(e.Mode & 0xFFF, Is.EqualTo(x.Mode), $"{e.Path}: permission bits");
        switch (x.Kind) {
          case 'D': Assert.That(e.IsDirectory, Is.True, e.Path); break;
          case 'L': Assert.That(System.Text.Encoding.UTF8.GetString(e.Data), Is.EqualTo(x.Target), e.Path); break;
          default:
            Assert.That(e.Data.LongLength, Is.EqualTo(x.Size), e.Path);
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(e.Data)), Is.EqualTo(x.Sha), e.Path);
            break;
        }
      }
    });
  }

  [Test]
  public void GivenImage_WhenDetectedByMagic_ThenJffs1() {
    Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Image("jffs1-le")).ToString(), Is.EqualTo("Jffs1"));
    Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Image("jffs1-be")).ToString(), Is.EqualTo("Jffs1"));
  }

  // ── refusals ──────────────────────────────────────────────────────────

  /// <summary>Offset of the node whose name is <paramref name="name"/>.</summary>
  private static int NodeOffset(byte[] img, string name) {
    var pos = 0;
    while (pos + 60 <= img.Length) {
      int nsize = img[pos + 48];
      var dsize = (int)BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(pos + 40));
      if (System.Text.Encoding.ASCII.GetString(img, pos + 60, nsize) == name) return pos;
      pos += 60 + ((nsize + 3) & ~3) + ((dsize + 3) & ~3);
    }
    throw new InvalidOperationException(name);
  }

  private static void FixHeaderChecksum(byte[] img, int pos) {
    var h = img.AsSpan(pos, 60).ToArray();
    h[51] = 0; h[58] = 0; h[59] = 0;
    uint s = 0;
    foreach (var b in h) s += b;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(pos + 58), (ushort)s);
  }

  [Test]
  public void GivenDeletedFlag_WhenRead_ThenRefusedNotReplayed() {
    var img = Image("jffs1-le");
    var at = NodeOffset(img, "motd");
    img[at + 50] = 0x80;
    FixHeaderChecksum(img, at);
    Assert.Throws<InvalidDataException>(() => new Jffs1Reader(new MemoryStream(img)));
  }

  [Test]
  public void GivenCorruptData_WhenRead_ThenRefused() {
    var img = Image("jffs1-le");
    var at = NodeOffset(img, "random.bin");
    img[at + 60 + 12] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => new Jffs1Reader(new MemoryStream(img)));
  }

  [Test]
  public void GivenBadHeaderChecksum_WhenRead_ThenTheNodeIsSkippedAndItsFileMissing() {
    var img = Image("jffs1-le");
    var at = NodeOffset(img, "motd");
    img[at + 58] ^= 0xFF;
    // The node of etc/motd is ignored as the kernel would; without its inode the tree is
    // still consistent, and the file is simply not there.
    var r = new Jffs1Reader(new MemoryStream(img));
    Assert.That(r.SkippedNodes, Is.GreaterThanOrEqualTo(1));
    Assert.That(r.Entries.Select(e => e.Path), Does.Not.Contain("etc/motd"));
  }

  [Test]
  public void GivenWrongMagic_WhenOpened_ThenInvalidData() {
    var img = Image("jffs1-le");
    img[0] = 0;
    Assert.Throws<InvalidDataException>(() => new Jffs1Reader(new MemoryStream(img)));
  }
}
