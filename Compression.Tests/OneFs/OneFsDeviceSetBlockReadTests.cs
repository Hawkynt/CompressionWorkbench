using FileSystem.OneFs;

namespace Compression.Tests.OneFs;

[TestFixture]
public sealed class OneFsDeviceSetBlockReadTests {

  [Test, Category("HappyPath")]
  public void ReadBlock_ReadsOnlySelectedMemberAndRestoresCallerPositions() {
    var firstBytes = BuildBlocks(2, seed: 17);
    var secondBytes = BuildBlocks(3, seed: 91);
    using var first = new MemoryStream(firstBytes, writable: false);
    using var second = new MemoryStream(secondBytes, writable: false);
    first.Position = 37;
    second.Position = 113;
    var set = OneFsDeviceSet.Open([first, second]);

    var destination = Enumerable.Repeat((byte)0xCC, OneFsReader.PhysicalBlockSize + 13).ToArray();
    set.ReadBlock(deviceIndex: 1, blockIndex: 2, destination);

    Assert.Multiple(() => {
      Assert.That(destination.AsSpan(0, OneFsReader.PhysicalBlockSize).ToArray(),
        Is.EqualTo(secondBytes.AsSpan(2 * OneFsReader.PhysicalBlockSize, OneFsReader.PhysicalBlockSize).ToArray()));
      Assert.That(destination.AsSpan(OneFsReader.PhysicalBlockSize).ToArray(),
        Is.All.EqualTo((byte)0xCC), "ReadBlock must not touch bytes beyond the requested 8 KiB block.");
      Assert.That(first.Position, Is.EqualTo(37), "An unselected member must be untouched.");
      Assert.That(second.Position, Is.EqualTo(113), "The selected member cursor must be restored after reading.");
    });
  }

  [Test, Category("Malformed")]
  public void ReadBlock_RejectsDeviceBlockAndBufferBounds() {
    var bytes = BuildBlocks(2, seed: 3);
    using var stream = new MemoryStream(bytes, writable: false);
    var set = OneFsDeviceSet.Open([stream]);
    var block = new byte[OneFsReader.PhysicalBlockSize];

    Assert.Multiple(() => {
      Assert.That(() => set.ReadBlock(-1, 0, block), Throws.TypeOf<ArgumentOutOfRangeException>());
      Assert.That(() => set.ReadBlock(1, 0, block), Throws.TypeOf<ArgumentOutOfRangeException>());
      Assert.That(() => set.ReadBlock(0, -1, block), Throws.TypeOf<ArgumentOutOfRangeException>());
      Assert.That(() => set.ReadBlock(0, 2, block), Throws.TypeOf<ArgumentOutOfRangeException>());
      Assert.That(() => set.ReadBlock(0, 0, new byte[OneFsReader.PhysicalBlockSize - 1]),
        Throws.TypeOf<ArgumentException>());
    });
  }

  [Test, Category("Regression")]
  public void ReadBlock_DoesNotTreatPartialTailAsACompleteBlock() {
    var bytes = BuildBlocks(1, seed: 51).Concat(new byte[127]).ToArray();
    using var stream = new MemoryStream(bytes, writable: false);
    var set = OneFsDeviceSet.Open([stream]);

    Assert.Multiple(() => {
      Assert.That(set.Devices[0].CompleteBlockCount, Is.EqualTo(1));
      Assert.That(set.Devices[0].PartialBlockBytes, Is.EqualTo(127));
      Assert.That(() => set.ReadBlock(0, 1, new byte[OneFsReader.PhysicalBlockSize]),
        Throws.TypeOf<ArgumentOutOfRangeException>(),
        "A partial device tail must never be promoted into an addressable OneFS block.");
    });
  }

  private static byte[] BuildBlocks(int count, int seed) {
    var result = new byte[checked(count * OneFsReader.PhysicalBlockSize)];
    for (var i = 0; i < result.Length; ++i)
      result[i] = unchecked((byte)(seed + i * 29 + i / OneFsReader.PhysicalBlockSize * 7));
    return result;
  }
}
