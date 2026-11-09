using System.IO.Compression;
using System.Security.Cryptography;
using FileSystem.Lfs;

namespace Compression.Tests.Lfs;

/// <summary>
/// LFS: the reader against a volume NetBSD 11.1 created with <c>newfs_lfs</c> and filled
/// through its kernel (see <c>Lfs/ReferenceVectors/README.md</c>), compared with the
/// manifest NetBSD took from the mounted volume; plus refusals.
/// </summary>
[TestFixture]
public class LfsTests {

  internal static byte[] Image() {
    using var res = typeof(LfsTests).Assembly.GetManifestResourceStream("LfsVectors.lfs.img.gz")
      ?? throw new InvalidOperationException("lfs.img.gz not embedded");
    using var gz = new GZipStream(res, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  internal record Expected(char Kind, uint Mode, long Size, string? Sha, string? Target);

  internal static Dictionary<string, Expected> ParseManifest(string text) {
    var map = new Dictionary<string, Expected>();
    foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
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

  internal static void AssertMatches(LfsReader r, Dictionary<string, Expected> expected) {
    Assert.That(r.Entries.Select(e => e.Path), Is.EquivalentTo(expected.Keys));
    Assert.Multiple(() => {
      foreach (var e in r.Entries) {
        var x = expected[e.Path];
        switch (x.Kind) {
          case 'D': Assert.That(e.IsDirectory, Is.True, e.Path); Assert.That(e.Mode & 0xFFF, Is.EqualTo(x.Mode), e.Path); break;
          case 'L': Assert.That(System.Text.Encoding.UTF8.GetString(r.Extract(e)), Is.EqualTo(x.Target), e.Path); break;
          default:
            Assert.That(e.Mode & 0xFFF, Is.EqualTo(x.Mode), $"{e.Path}: mode");
            var data = r.Extract(e);
            Assert.That(data.LongLength, Is.EqualTo(x.Size), e.Path);
            Assert.That(Convert.ToHexStringLower(SHA256.HashData(data)), Is.EqualTo(x.Sha), e.Path);
            break;
        }
      }
    });
  }

  [Test]
  public void GivenKernelWrittenVolume_WhenRead_ThenTreeModesAndBytesMatchTheMountedVolume() {
    using var res = typeof(LfsTests).Assembly.GetManifestResourceStream("LfsVectors.lfs.manifest")!;
    using var sr = new StreamReader(res);
    var r = new LfsReader(new MemoryStream(Image(), writable: false));
    AssertMatches(r, ParseManifest(sr.ReadToEnd()));
    Assert.That((r.BlockSize, r.FragmentSize), Is.EqualTo((1024, 512)), "newfs_lfs defaults for a 24 MiB volume");
  }

  [Test]
  public void GivenKernelWrittenVolume_WhenRead_ThenTheHardLinkSharesTheInode() {
    var r = new LfsReader(new MemoryStream(Image(), writable: false));
    Assert.That(r.Entries.Single(e => e.Path == "etc/motd").Ino, Is.EqualTo(r.Entries.Single(e => e.Path == "etc/motd-hardlink").Ino));
  }

  [Test]
  public void GivenVolume_WhenDetectedByMagic_ThenLfs()
    => Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Image().AsSpan(0, 65536)).ToString(), Is.EqualTo("Lfs"));

  [Test]
  public void GivenEveryCheckpointDamaged_WhenOpened_ThenInvalidData() {
    var img = Image();
    // Flip one byte in each superblock copy's checksummed area.
    for (var at = 0; at + 512 <= img.Length; at += 512)
      if (BitConverter.ToUInt32(img, at) == 0x070162) img[at + 100] ^= 0xFF;
    Assert.Throws<InvalidDataException>(() => new LfsReader(new MemoryStream(img)));
  }

  [Test]
  public void GivenTruncatedVolume_WhenRead_ThenRefusedNotGuessed() {
    var img = Image().AsSpan(0, 64 * 1024).ToArray();
    Assert.Catch(() => {
      var r = new LfsReader(new MemoryStream(img));
      foreach (var e in r.Entries) r.Extract(e);
    });
  }
}
