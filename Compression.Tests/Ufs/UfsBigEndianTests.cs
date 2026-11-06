#pragma warning disable CS1591
using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.Ufs;

namespace Compression.Tests.Ufs;

/// <summary>
/// Big-endian UFS1 and UFS2: created, edited and maintained in the volume's own byte
/// order. The NetBSD reference volumes come from <c>newfs -B be</c> (see
/// <c>ReferenceVectors/README.md</c>); NetBSD itself judges what is written here in
/// <see cref="UfsNetBsdTests"/>.
/// </summary>
[TestFixture]
public sealed class UfsBigEndianTests {
  private const int Block = 16384;

  /// <summary>SHA-256 of the files NetBSD 11.1 wrote into both <c>netbsd-newfs-ufs*-be</c> volumes, as NetBSD's <c>cksum -a sha256</c> reported them.</summary>
  internal static readonly Dictionary<string, string> NetBsdFiles = new() {
    ["hello.txt"] = "ffb17af88768c552b6380fb9a63d6c320ec8af393e43d96e30b960b59e73b858",
    ["empty"] = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
    ["sub/numbers.txt"] = "5af7b95208fdcff454bab3f5eddf567a688a3796c703d4fef91072e38645c062",
    ["sub/deeper/x.txt"] = "e9aafd96cb59fb217dcca861b06be25698e046aa70d3fac058038de086c9cc33",
  };

  internal static readonly Dictionary<string, byte[]> Tree = new() {
    ["hello.txt"] = "hello big-endian ufs\n"u8.ToArray(),
    ["empty.bin"] = [],
    ["direct.bin"] = Ufs2WriterTests.Pattern(5 * Block + 300, 1),
    ["sub/indirect.bin"] = Ufs2WriterTests.Pattern(1_500_000, 2),
    ["sub/deeper/n.txt"] = "nested\n"u8.ToArray(),
  };

  internal static MemoryStream Vector(string name) {
    using var resource = typeof(UfsBigEndianTests).Assembly.GetManifestResourceStream("UfsVectors." + name)
      ?? throw new FileNotFoundException($"embedded reference vector {name} is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var ms = new MemoryStream();
    gzip.CopyTo(ms);
    ms.Position = 0;
    return ms;
  }

  internal static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  internal static Dictionary<string, string> Hashes(Stream image) {
    image.Position = 0;
    using var r = new UfsReader(image, leaveOpen: true);
    return r.Entries.Where(e => !e.IsDirectory && !e.IsSymlink).ToDictionary(e => e.Name, e => Sha(r.Extract(e)));
  }

  [TestCase("UFS1")]
  [TestCase("UFS2")]
  public void GivenTheBigEndianOption_WhenAVolumeIsCreated_ThenEveryFieldIsBigEndianAndEveryFileReadsBack(string version) {
    var image = Ufs2WriterTests.Create(Tree, Ufs2WriterTests.Options(version, bigEndian: true));
    using var ms = new MemoryStream(image);
    using (var r = new UfsReader(ms, leaveOpen: true)) {
      Assert.That(r.IsBigEndian, Is.True);
      Assert.That(r.Version, Is.EqualTo(version == "UFS2" ? 2 : 1));
      var magicAt = r.SuperblockOffset + 1372;
      Assert.That(image.AsSpan((int)magicAt, 4).ToArray(),
        Is.EqualTo(version == "UFS2" ? new byte[] { 0x19, 0x54, 0x01, 0x19 } : [0x00, 0x01, 0x19, 0x54]));
    }
    Assert.That(Hashes(ms), Is.EquivalentTo(Tree.ToDictionary(t => t.Key, t => Sha(t.Value))));
  }

  [TestCase("UFS1")]
  [TestCase("UFS2")]
  public void GivenTheSameTree_WhenCreatedInBothByteOrders_ThenBothVolumesHaveTheSameLength(string version) {
    var le = Ufs2WriterTests.Create(Tree, Ufs2WriterTests.Options(version, bigEndian: false));
    var be = Ufs2WriterTests.Create(Tree, Ufs2WriterTests.Options(version, bigEndian: true));
    Assert.That(be.Length, Is.EqualTo(le.Length));
    Assert.That(be, Is.Not.EqualTo(le));
  }

  [TestCase("netbsd-newfs-ufs1-be.img.gz")]
  [TestCase("netbsd-newfs-ufs2-be.img.gz")]
  public void GivenANetBsdBigEndianVolume_WhenRead_ThenEveryFileMatchesNetBsdsHash(string vector) {
    using var image = Vector(vector);
    Assert.That(Hashes(image), Is.EquivalentTo(NetBsdFiles));
  }

  /// <summary>The edits the NetBSD oracle test replays: adds into new and existing folders, a large add, a remove.</summary>
  internal static Dictionary<string, string> EditNetBsdVolume(Stream image) {
    var d = new UfsFormatDescriptor();
    var big = Ufs2WriterTests.Pattern(700_000, 7);
    d.Add(image, [
      ArchiveInputInfo.InMemory("added/deep/note.txt", "added by the editor\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("sub/big.bin", big),
    ]);
    d.Remove(image, ["sub/deeper/x.txt"]);
    var expected = new Dictionary<string, string>(NetBsdFiles);
    expected.Remove("sub/deeper/x.txt");
    expected["added/deep/note.txt"] = Sha("added by the editor\n"u8.ToArray());
    expected["sub/big.bin"] = Sha(big);
    return expected;
  }

  [TestCase("netbsd-newfs-ufs1-be.img.gz")]
  [TestCase("netbsd-newfs-ufs2-be.img.gz")]
  public void GivenANetBsdBigEndianVolume_WhenEdited_ThenItStaysBigEndianAndEveryFileReadsBack(string vector) {
    using var image = Vector(vector);
    var length = image.Length;
    var expected = EditNetBsdVolume(image);
    Assert.That(image.Length, Is.EqualTo(length));
    Assert.That(Hashes(image), Is.EquivalentTo(expected));
    image.Position = 0;
    using var r = new UfsReader(image, leaveOpen: true);
    Assert.That(r.IsBigEndian, Is.True);
  }

  [TestCase("UFS1")]
  [TestCase("UFS2")]
  public void GivenABigEndianFragmentedVolume_WhenDefragmented_ThenEveryFileIsOneRunAndKeepsItsBytes(string version) {
    var (ms, files) = Ufs2MaintenanceTests.Fragmented(Ufs2WriterTests.Options(version, bigEndian: true));
    Assert.That(Ufs2MaintenanceTests.Pieces(ms, "big.bin"), Has.Count.GreaterThan(1), "the scenario fragments big.bin");
    new UfsFormatDescriptor().Defragment(ms);
    Ufs2MaintenanceTests.AssertHolds(ms, files);
    foreach (var name in files.Keys)
      Assert.That(Ufs2MaintenanceTests.Pieces(ms, name), Has.Count.EqualTo(1), name);
  }

  [TestCase("UFS1")]
  [TestCase("UFS2")]
  public void GivenABigEndianVolume_WhenWipedAndBlocksMoved_ThenEveryFileKeepsItsBytes(string version) {
    var (ms, files, to) = Ufs2MaintenanceTests.Moved(zeroSource: true, Ufs2WriterTests.Options(version, bigEndian: true));
    Assert.That(Ufs2MaintenanceTests.Pieces(ms, "dir/b.bin")[0].Offset, Is.EqualTo(to));
    new UfsFormatDescriptor().WipeUnusedSpace(ms);
    Ufs2MaintenanceTests.AssertHolds(ms, files);
  }

  [Test]
  public void GivenABigEndianVolume_WhenAFileIsAddedOnThePathOfAFile_ThenItIsRefusedAndTheImageIsUntouched() {
    using var image = Vector("netbsd-newfs-ufs1-be.img.gz");
    var before = image.ToArray();
    Assert.Throws<IOException>(() => UfsModifier.AddFile(image, "hello.txt/child", [1]));
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }
}
