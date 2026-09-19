using FileSystem.OneFs;

namespace Compression.Tests.OneFs;

[TestFixture]
public sealed class OneFsMediaCorrelatorTests {

  [Test, Category("HappyPath")]
  public void CompareSameOffset_ReportsStableAndVariantBytesWithoutMovingCursors() {
    var firstBytes = BuildBlocks(2, seed: 11);
    var secondBytes = firstBytes.ToArray();
    var thirdBytes = firstBytes.ToArray();
    secondBytes[OneFsReader.PhysicalBlockSize + 17] ^= 0x5A;
    thirdBytes[OneFsReader.PhysicalBlockSize + 17] ^= 0x5A;
    thirdBytes[OneFsReader.PhysicalBlockSize + 91] ^= 0x33;

    using var first = new MemoryStream(firstBytes, writable: false);
    using var second = new MemoryStream(secondBytes, writable: false);
    using var third = new MemoryStream(thirdBytes, writable: false);
    first.Position = 3;
    second.Position = 5;
    third.Position = 7;

    var correlator = new OneFsMediaCorrelator(OneFsDeviceSet.Open([first, second, third]));
    var result = correlator.CompareSameOffset(1);

    Assert.Multiple(() => {
      Assert.That(result.BlockIndex, Is.EqualTo(1));
      Assert.That(result.Members.Select(static member => member.DeviceIndex), Is.EqualTo(new[] { 0, 1, 2 }));
      Assert.That(result.Members.Select(static member => member.BlockIndex), Is.All.EqualTo(1));
      Assert.That(result.StableByteCount, Is.EqualTo(OneFsReader.PhysicalBlockSize - 2));
      Assert.That(result.VariantByteCount, Is.EqualTo(2));
      Assert.That(result.AllBytesStable, Is.False);
      Assert.That(result.AllFingerprintsEqual, Is.False);
      Assert.That(first.Position, Is.EqualTo(3));
      Assert.That(second.Position, Is.EqualTo(5));
      Assert.That(third.Position, Is.EqualTo(7));
    });
  }

  [Test, Category("HappyPath")]
  public void CompareSameOffset_SkipsShorterMembersRatherThanReadingPartialOrMissingBlocks() {
    var longBytes = BuildBlocks(3, seed: 23);
    var shortBytes = BuildBlocks(1, seed: 47).Concat(new byte[19]).ToArray();
    using var longStream = new MemoryStream(longBytes, writable: false);
    using var shortStream = new MemoryStream(shortBytes, writable: false);

    var correlator = new OneFsMediaCorrelator(OneFsDeviceSet.Open([longStream, shortStream]));
    var result = correlator.CompareSameOffset(2);

    Assert.Multiple(() => {
      Assert.That(result.Members, Has.Count.EqualTo(1));
      Assert.That(result.Members[0].DeviceIndex, Is.EqualTo(0));
      Assert.That(result.AllBytesStable, Is.True);
      Assert.That(result.AllFingerprintsEqual, Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void FindExactCopies_ReturnsOnlyByteIdenticalCandidateGroups() {
    var blocks = new[] {
      BuildBlock(seed: 3),
      BuildBlock(seed: 71),
      BuildBlock(seed: 3),
      BuildBlock(seed: 129),
      BuildBlock(seed: 71),
      BuildBlock(seed: 3),
    };
    using var stream = new MemoryStream(blocks.SelectMany(static block => block).ToArray(), writable: false);
    stream.Position = 41;
    var correlator = new OneFsMediaCorrelator(OneFsDeviceSet.Open([stream]));

    var result = correlator.FindExactCopies(0, [5, 0, 4, 2, 1, 3, 5]);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(2));
      Assert.That(result[0].BlockIndices, Is.EqualTo(new long[] { 0, 2, 5 }));
      Assert.That(result[1].BlockIndices, Is.EqualTo(new long[] { 1, 4 }));
      Assert.That(result[0].Sha256, Has.Length.EqualTo(64));
      Assert.That(result[1].Sha256, Has.Length.EqualTo(64));
      Assert.That(result[0].Sha256, Is.Not.EqualTo(result[1].Sha256));
      Assert.That(stream.Position, Is.EqualTo(41));
    });
  }

  [Test, Category("Malformed")]
  public void Correlator_RejectsMissingOffsetsMembersAndNullCandidates() {
    using var stream = new MemoryStream(BuildBlocks(2, seed: 7), writable: false);
    var correlator = new OneFsMediaCorrelator(OneFsDeviceSet.Open([stream]));

    Assert.Multiple(() => {
      Assert.That(() => correlator.CompareSameOffset(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
      Assert.That(() => correlator.CompareSameOffset(2), Throws.TypeOf<ArgumentOutOfRangeException>());
      Assert.That(() => correlator.FindExactCopies(-1, [0]), Throws.TypeOf<ArgumentOutOfRangeException>());
      Assert.That(() => correlator.FindExactCopies(1, [0]), Throws.TypeOf<ArgumentOutOfRangeException>());
      Assert.That(() => correlator.FindExactCopies(0, null!), Throws.TypeOf<ArgumentNullException>());
      Assert.That(() => correlator.FindExactCopies(0, [0, 2]), Throws.TypeOf<ArgumentOutOfRangeException>());
    });
  }

  [Test, Category("Regression")]
  public void FindExactCopies_DoesNotReportUniqueCandidates() {
    using var stream = new MemoryStream(BuildBlocks(3, seed: 19), writable: false);
    var correlator = new OneFsMediaCorrelator(OneFsDeviceSet.Open([stream]));

    var result = correlator.FindExactCopies(0, [0, 1, 2]);

    Assert.That(result, Is.Empty);
  }

  private static byte[] BuildBlocks(int count, int seed)
    => Enumerable.Range(0, count).SelectMany(index => BuildBlock(seed + index * 43)).ToArray();

  private static byte[] BuildBlock(int seed) {
    var result = new byte[OneFsReader.PhysicalBlockSize];
    for (var i = 0; i < result.Length; ++i)
      result[i] = unchecked((byte)(seed + i * 29 + i / 97));
    return result;
  }
}
