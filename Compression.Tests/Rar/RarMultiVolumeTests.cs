using FileFormat.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// Volume sets as RAR writes them: each volume its own archive with the volume flags, files
/// cut across volumes with split-before/split-after headers. unrar and 7-Zip acceptance of the
/// same output is in <see cref="RarToolInteropTests"/>; rar-made sets in <see cref="RarConformanceTests"/>.
/// </summary>
[TestFixture]
public class RarMultiVolumeTests {
  private static byte[] Data(int size, int seed) {
    var d = new byte[size];
    new Random(seed).NextBytes(d);
    return d;
  }

  [Category("RoundTrip")]
  [TestCase(RarConstants.MethodStore, null)]
  [TestCase(RarConstants.MethodNormal, null)]
  [TestCase(RarConstants.MethodNormal, "secret")]
  public void Given_Rar5Volumes_When_ReadAsASet_Then_FilesSpanningVolumesComeBack(int method, string? password) {
    var d1 = Data(5000, 1);
    var d2 = Data(9000, 2);
    var volumes = RarWriter.CreateSplit(2048, [("a.bin", d1), ("b.bin", d2)], method, password);
    Assert.That(volumes, Has.Length.GreaterThan(3));
    Assert.That(volumes.All(v => v.Length <= 2048), Is.True, "no volume exceeds the size");
    using var reader = new RarReader(volumes.Select(v => (Stream)new MemoryStream(v)).ToList(), password);
    Assert.That(reader.IsVolume, Is.True);
    Assert.That(reader.Entries.Select(e => e.Name), Is.EqualTo(new[] { "a.bin", "b.bin" }));
    Assert.That(reader.Entries[1].VolumeCount, Is.GreaterThan(1));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("RoundTrip")]
  [TestCase(RarConstants.Rar4MethodStore, null)]
  [TestCase(RarConstants.Rar4MethodNormal, null)]
  [TestCase(RarConstants.Rar4MethodNormal, "secret")]
  public void Given_Rar4Volumes_When_ReadAsASet_Then_FilesSpanningVolumesComeBack(byte method, string? password) {
    var d1 = Data(5000, 3);
    var d2 = Data(9000, 4);
    var volumes = Rar4Writer.CreateSplit(2048, [("a.bin", d1), ("b.bin", d2)], method, password);
    Assert.That(volumes, Has.Length.GreaterThan(3));
    Assert.That(volumes.All(v => v.Length <= 2048), Is.True);
    using var reader = new RarReader(volumes.Select(v => (Stream)new MemoryStream(v)).ToList(), password);
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("Exception")]
  [Test]
  public void Given_AVolumeSetWithoutItsLastVolume_When_TheSplitFileIsExtracted_Then_ItIsRefused() {
    var volumes = RarWriter.CreateSplit(2048, [("a.bin", Data(9000, 5))], RarConstants.MethodStore);
    using var reader = new RarReader(volumes[..^1].Select(v => (Stream)new MemoryStream(v)).ToList(), null);
    Assert.That(reader.Entries[0].IsIncomplete, Is.True);
    Assert.Throws<InvalidDataException>(() => reader.Extract(0));
  }

  [Category("Boundary")]
  [Test]
  public void Given_AVolumeSizeBelow1K_When_Writing_Then_TheWriterRefusesIt()
    => Assert.Throws<ArgumentOutOfRangeException>(() => RarWriter.CreateSplit(512, [("a", new byte[10])]));

  [TestCase("x.part1.rar", 0, "x.part1.rar")]
  [TestCase("x.part1.rar", 1, "x.part2.rar")]
  [TestCase("x.part01.rar", 9, "x.part10.rar")]
  [TestCase("x.rar", 1, "x.r00")]
  [TestCase("x.rar", 101, "x.s00")]
  [TestCase("x.r05", 0, "x.rar")]
  [TestCase("x.r05", 7, "x.r06")]
  public void Given_AVolumeName_When_TheSetIsEnumerated_Then_RarsNamingSchemesAreFollowed(string name, int index, string expected) {
    var seq = RarVolumeNames.SequenceOf(Path.Combine("d", name))!;
    Assert.That(Path.GetFileName(seq(index)), Is.EqualTo(expected));
  }
}
