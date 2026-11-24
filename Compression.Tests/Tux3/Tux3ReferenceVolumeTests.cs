using System.IO.Compression;
using System.Security.Cryptography;
using FileSystem.Tux3;

namespace Compression.Tests.Tux3;

/// <summary>
/// Tux3 volumes of the 2015-06-01 revision that the linux-tux3 user tools made
/// (<c>ReferenceVectors/README.md</c>, <c>tux3-gen.sh</c>): a directory tree written through
/// tux3fuse, files written into the root by <c>tux3 write</c>, a hard link and a symlink, each as
/// the tools left it (deltas still in the log) and after <c>tux3 unify</c>.
/// </summary>
[TestFixture]
public class Tux3ReferenceVolumeTests {

  private static byte[] Resource(string name) {
    using var res = typeof(Tux3ReferenceVolumeTests).Assembly.GetManifestResourceStream("Tux3Vectors." + name)
      ?? throw new InvalidOperationException(name + " not embedded");
    using var ms = new MemoryStream();
    if (name.EndsWith(".gz", StringComparison.Ordinal)) {
      using var gz = new GZipStream(res, CompressionMode.Decompress);
      gz.CopyTo(ms);
    } else res.CopyTo(ms);
    return ms.ToArray();
  }

  private static Tux3Reader Open(string volume) => new(new MemoryStream(Resource(volume + ".img.gz"), writable: false));

  private static string[] Lines(string name) => System.Text.Encoding.UTF8.GetString(Resource(name))
    .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static l => l.TrimEnd('\r')).ToArray();

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCase("tree")]
  [TestCase("tree-unified")]
  [TestCase("links")]
  [TestCase("links-unified")]
  [TestCase("cli")]
  [TestCase("cli-unified")]
  [Category("HappyPath"), Category("Oracle")]
  public void GivenAVolumeTux3fuseReadsBack_WhenRead_ThenEveryEntryIsWhatTux3fuseReadsBack(string volume) {
    using var reader = Open(volume);
    Assert.That(reader.Revision, Is.EqualTo("2015-06-01"));
    var byPath = reader.Entries.ToDictionary(static e => e.Name, StringComparer.Ordinal);
    var manifest = Lines(volume + ".manifest");
    Assert.That(byPath.Keys, Is.EquivalentTo(manifest.Select(static l => l.Split(' ')[1])));
    Assert.Multiple(() => {
      foreach (var line in manifest) {
        var f = line.Split(' ');
        var e = byPath[f[1]];
        if (f[0] == "D") {
          Assert.That(e.IsDirectory, Is.True, f[1]);
          continue;
        }
        if (f[0] == "L") {
          Assert.That(e.Node!.IsSymlink, Is.True, f[1]);
          Assert.That(e.Node.LinkTarget, Is.EqualTo(line[(line.IndexOf(" -> ", StringComparison.Ordinal) + 4)..]), f[1]);
          continue;
        }
        Assert.That(e.IsDirectory, Is.False, f[1]);
        Assert.That(e.Size, Is.EqualTo(long.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture)), f[1]);
        Assert.That(Convert.ToString(e.Node!.Mode & 0xFFF, 8), Is.EqualTo(f[3]), f[1] + " mode");
        Assert.That(e.Node.Links, Is.EqualTo(uint.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture)), f[1] + " links");
        Assert.That(Sha(reader.Extract(e)), Is.EqualTo(f[5]), f[1]);
      }
    });
  }

  [Test, Category("Spec")]
  public void GivenAVolumeTheToolsLeftWithDeltasInTheLog_WhenRead_ThenTheIndexRecordsWereReplayed() {
    using var written = Open("tree");
    using var unified = Open("tree-unified");
    Assert.Multiple(() => {
      Assert.That(written.Tree!.ReplayedRecords, Is.GreaterThan(0), "the 150-entry directory split nodes the log still holds");
      Assert.That(written.Entries.Select(static e => e.Name), Is.EquivalentTo(unified.Entries.Select(static e => e.Name)));
    });
  }

  [TestCase("links")]
  [TestCase("links-unified")]
  [Category("HappyPath"), Category("Oracle")]
  public void GivenAHardLinkAndASymlinkTheToolsMade_WhenRead_ThenTheyNameTheFileTheyPointAt(string volume) {
    using var reader = Open(volume);
    var byPath = reader.Entries.ToDictionary(static e => e.Name, StringComparer.Ordinal);
    Assert.Multiple(() => {
      Assert.That(byPath["hard-hello"].Node!.Inum, Is.EqualTo(byPath["hello.txt"].Node!.Inum));
      Assert.That(byPath["hello.txt"].Node!.Links, Is.EqualTo(2u));
      Assert.That(byPath["sym-hello"].Node!.IsSymlink, Is.True);
      Assert.That(byPath["sym-hello"].Node!.LinkTarget, Is.EqualTo("hello.txt"));
      Assert.That(reader.Extract(byPath["hard-hello"]), Is.EqualTo("hello from tux3\n"u8.ToArray()));
    });
  }

  [Test, Category("Exception")]
  public void GivenInodeLeavesWithoutTheirMagic_WhenRead_ThenTheVolumeIsRefused() {
    var bytes = Resource("tree-unified.img.gz");
    var damaged = 0;
    for (var at = 0; at < bytes.Length; at += 4096)
      if (bytes[at] == 0x90 && bytes[at + 1] == 0xDE) {
        bytes[at + 1] ^= 0xFF;
        ++damaged;
      }
    Assert.That(damaged, Is.GreaterThan(0));
    Assert.Throws<InvalidDataException>(() => _ = new Tux3Reader(new MemoryStream(bytes)));
  }

  [Test, Category("Exception")]
  public void GivenAVolumeCutShort_WhenOpened_ThenItIsRefused() {
    var bytes = Resource("tree.img.gz").AsSpan(0, 64 * 1024).ToArray();
    Assert.Throws<InvalidDataException>(() => _ = new Tux3Reader(new MemoryStream(bytes)));
  }
}
