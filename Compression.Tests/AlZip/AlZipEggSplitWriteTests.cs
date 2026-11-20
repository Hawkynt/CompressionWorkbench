using FileFormat.AlZip;
using FileFormat.Egg;

namespace Compression.Tests.AlZip;

/// <summary>Splitting our own archives: joined back, the volumes are the archive again.</summary>
[TestFixture]
public sealed class AlZipEggSplitWriteTests {

  private static byte[] Data(int seed, int length) {
    var d = new byte[length];
    new Random(seed).NextBytes(d);
    return d;
  }

  private static byte[] Alz() {
    using var ms = new MemoryStream();
    using (var w = new AlZipWriter(ms, leaveOpen: true, store: true)) {
      w.AddFile("a.bin", Data(1, 50000));
      w.AddFile("b/c.bin", Data(2, 30000));
    }
    return ms.ToArray();
  }

  private static byte[] Egg() {
    using var ms = new MemoryStream();
    using (var w = new EggWriter(ms, leaveOpen: true, headerId: 0x1234)) {
      w.AddEntry("a.bin", Data(1, 50000), EggCompressionMethod.Store);
      w.AddEntry("b/c.bin", Data(2, 30000), EggCompressionMethod.Store);
    }
    return ms.ToArray();
  }

  [TestCase(4096)]
  [TestCase(20000)]
  [TestCase(65536)]
  public void GivenAnAlz_WhenSplitAndJoined_ThenItIsTheArchiveAgain(int size) {
    var archive = Alz();
    var volumes = AlZipVolumes.Split(archive, size);
    Assert.That(volumes, Has.All.Matches<byte[]>(v => v.Length <= size));
    Assert.That(volumes.Take(volumes.Count - 1), Has.All.Matches<byte[]>(v => AlZipVolumes.ContinuesInNextVolume(v)));
    Assert.That(AlZipVolumes.ContinuesInNextVolume(volumes[^1]), Is.False);
    Assert.That(AlZipVolumes.Join(volumes), Is.EqualTo(archive));
  }

  [Test]
  public void GivenAnAlzThatFitsOneVolume_WhenSplit_ThenItStaysOne() {
    var archive = Alz();
    Assert.That(AlZipVolumes.Split(archive, archive.Length), Is.EqualTo(new[] { archive }));
  }

  [Test]
  public void GivenATinyAlzVolumeSize_WhenSplit_ThenItIsRefused() =>
    Assert.Throws<ArgumentOutOfRangeException>(() => AlZipVolumes.Split(Alz(), 40));

  [TestCase(4096)]
  [TestCase(20000)]
  [TestCase(65536)]
  public void GivenAnEgg_WhenSplitAndJoined_ThenEveryMemberReadsBack(int size) {
    var volumes = EggVolumes.Split(Egg(), size);
    Assert.That(volumes, Has.Count.GreaterThan(1));
    Assert.That(volumes, Has.All.Matches<byte[]>(v => v.Length <= size));
    using var r = EggReader.FromJoinedVolumes(EggVolumes.Join(volumes));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "a.bin")), Is.EqualTo(Data(1, 50000)));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "b/c.bin")), Is.EqualTo(Data(2, 30000)));
  }

  [Test]
  public void GivenAnEggThatFitsOneVolume_WhenSplit_ThenItStaysOne() {
    var archive = Egg();
    Assert.That(EggVolumes.Split(archive, archive.Length), Is.EqualTo(new[] { archive }));
  }
}
