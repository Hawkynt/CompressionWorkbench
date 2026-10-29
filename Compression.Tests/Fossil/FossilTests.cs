using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FileSystem.Fossil;

namespace Compression.Tests.Fossil;

/// <summary>
/// Fossil: the reader against a volume plan9port's <c>flfmt</c> formatted and its
/// <c>fossil</c> server filled over 9P (see <c>Fossil/ReferenceVectors/README.md</c>),
/// compared with what that server read back; plus refusals.
/// </summary>
[TestFixture]
public class FossilTests {

  private static byte[] Image() {
    using var res = typeof(FossilTests).Assembly.GetManifestResourceStream("FossilVectors.fossil.img.gz")
      ?? throw new InvalidOperationException("fossil.img.gz not embedded");
    using var gz = new GZipStream(res, CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gz.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, string> Manifest() {
    using var res = typeof(FossilTests).Assembly.GetManifestResourceStream("FossilVectors.fossil.manifest")
      ?? throw new InvalidOperationException("fossil.manifest not embedded");
    using var sr = new StreamReader(res);
    return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(l => l.TrimEnd('\r').Split("  ", 2)).ToDictionary(p => p[1], p => p[0]);
  }

  private static string Sha(byte[] d) => Convert.ToHexStringLower(SHA256.HashData(d));

  [Test]
  public void GivenServerWrittenVolume_WhenRead_ThenActiveTreeMatchesWhatTheServerReadBack() {
    var r = new FossilReader(new MemoryStream(Image(), writable: false));
    var manifest = Manifest();
    var files = r.Entries.Where(e => !e.IsDirectory && e.Path.StartsWith("active/", StringComparison.Ordinal)).ToDictionary(e => e.Path);
    Assert.That(files.Keys, Is.EquivalentTo(manifest.Keys));
    foreach (var (path, sha) in manifest) Assert.That(Sha(r.Extract(files[path])), Is.EqualTo(sha), path);
  }

  [Test]
  public void GivenServerWrittenVolume_WhenRead_ThenDirectoriesModesAndOwnersAreThoseTheServerCreated() {
    var r = new FossilReader(new MemoryStream(Image(), writable: false));
    var byPath = r.Entries.ToDictionary(e => e.Path);
    Assert.Multiple(() => {
      Assert.That(byPath["active/docs"].IsDirectory, Is.True);
      Assert.That(byPath["active/docs"].Mode & 0x1FF, Is.EqualTo(0x1FD), "d775");
      Assert.That(byPath["active/empty-dir"].IsDirectory, Is.True);
      Assert.That(byPath["active/docs/notes.txt"].Mode & 0x1FF, Is.EqualTo(0x180), "600");
      Assert.That(byPath["active/hello.txt"].Uid, Is.EqualTo("sys"));
      Assert.That(byPath["active/big.bin"].Source.Depth, Is.EqualTo(2), "900 KB at 4 KiB blocks needs a two-level pointer tree");
    });
  }

  [Test]
  public void GivenSnapshotTakenBeforeARewrite_WhenRead_ThenTheSnapshotKeepsTheOldBytes() {
    var r = new FossilReader(new MemoryStream(Image(), writable: false));
    var snap = r.Entries.Where(e => !e.IsDirectory && e.Path.StartsWith("snapshot/", StringComparison.Ordinal) && e.Path.EndsWith("/hello.txt", StringComparison.Ordinal)).ToList();
    Assert.That(snap, Has.Count.EqualTo(1), string.Join(", ", r.Entries.Select(e => e.Path)));
    Assert.That(Encoding.ASCII.GetString(r.Extract(snap[0])), Is.EqualTo("hello from fossil\n"));
    var active = r.Entries.Single(e => e.Path == "active/hello.txt");
    Assert.That(Encoding.ASCII.GetString(r.Extract(active)), Is.EqualTo("hello again, after the snapshot was taken\n"));
  }

  [Test]
  public void GivenVolume_WhenDetectedByMagic_ThenFossil()
    => Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Image().AsSpan(0, 192 * 1024)).ToString(), Is.EqualTo("Fossil"));

  [Test]
  public void GivenWrongHeaderMagic_WhenOpened_ThenInvalidData() {
    var img = Image();
    img[FossilReader.HeaderOffset] ^= 0xFF;
    Assert.Throws<InvalidDataException>(() => new FossilReader(new MemoryStream(img)));
  }

  [Test]
  public void GivenWrongSuperblockMagic_WhenOpened_ThenInvalidData() {
    var img = Image();
    var bs = BinaryPrimitives.ReadUInt16BigEndian(img.AsSpan(FossilReader.HeaderOffset + 6));
    var super = BinaryPrimitives.ReadUInt32BigEndian(img.AsSpan(FossilReader.HeaderOffset + 8));
    img[(int)(super * bs)] ^= 0xFF;
    Assert.Throws<InvalidDataException>(() => new FossilReader(new MemoryStream(img)));
  }

  [Test]
  public void GivenTruncatedImage_WhenRead_ThenRefusedNotGuessed() {
    var img = Image().AsSpan(0, 200 * 1024).ToArray();
    Assert.Catch(() => {
      var r = new FossilReader(new MemoryStream(img));
      foreach (var e in r.Entries) r.Extract(e);
    });
  }

  [Test]
  public void GivenDamagedMetaBlock_WhenRead_ThenRefused() {
    var img = Image();
    var magic = new byte[] { 0x56, 0x56, 0xFC, 0x7A };
    // Corrupt every meta block's magic: no directory can be listed any more.
    for (var i = img.AsSpan().IndexOf(magic); i >= 0; ) {
      img[i] = 0;
      var next = img.AsSpan(i + 1).IndexOf(magic);
      i = next < 0 ? -1 : i + 1 + next;
    }
    Assert.Throws<InvalidDataException>(() => new FossilReader(new MemoryStream(img)));
  }
}
