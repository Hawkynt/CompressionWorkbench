using System.Buffers.Binary;
using FileSystem.Wafl;

namespace Compression.Tests.Wafl;

[TestFixture]
public class WaflClassicAllocationMapTests {

  [Test, Category("HappyPath")]
  public void BlockMap_ZeroEntryIsFree() {
    var entry = WaflClassicAllocationMaps.ReadBlockMapEntry([0, 0, 0, 0], littleEndian: false);

    Assert.Multiple(() => {
      Assert.That(entry.IsFree, Is.True);
      Assert.That(entry.InActiveFileSystem, Is.False);
      Assert.That(entry.SnapshotMask, Is.Zero);
      Assert.That(entry.ReservedBits, Is.Zero);
      Assert.That(entry.ConsistencyPointBit, Is.False);
    });
  }

  [Test, Category("HappyPath")]
  public void BlockMap_DecodesPublishedBitFields() {
    const uint raw =
      (1u << 0) |       // active file system
      (1u << 1) |       // snapshot 0
      (1u << 20) |      // snapshot 19
      (0b10_0000u << 21) |
      (1u << 31);       // consistency-point bit
    Span<byte> bytes = stackalloc byte[sizeof(uint)];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, raw);

    var entry = WaflClassicAllocationMaps.ReadBlockMapEntry(bytes, littleEndian: false);

    Assert.Multiple(() => {
      Assert.That(entry.IsFree, Is.False);
      Assert.That(entry.InActiveFileSystem, Is.True);
      Assert.That(entry.IsReferencedBySnapshot(0), Is.True);
      Assert.That(entry.IsReferencedBySnapshot(19), Is.True);
      Assert.That(entry.IsReferencedBySnapshot(1), Is.False);
      Assert.That(entry.ReservedBits, Is.EqualTo(0b10_0000u));
      Assert.That(entry.ConsistencyPointBit, Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void BlockMap_AcceptsLittleEndianEntries() {
    Span<byte> bytes = stackalloc byte[sizeof(uint)];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1u << 7);

    var entry = WaflClassicAllocationMaps.ReadBlockMapEntry(bytes, littleEndian: true);

    Assert.That(entry.IsReferencedBySnapshot(6), Is.True);
  }

  [Test, Category("HappyPath")]
  public void BlockMap_ReadsContiguousEntries() {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(bytes[..4], 0);
    BinaryPrimitives.WriteUInt32BigEndian(bytes[4..], 1);

    var entries = WaflClassicAllocationMaps.ReadBlockMap(bytes, littleEndian: false);

    Assert.Multiple(() => {
      Assert.That(entries, Has.Length.EqualTo(2));
      Assert.That(entries[0].IsFree, Is.True);
      Assert.That(entries[1].InActiveFileSystem, Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void InodeMap_DecodesAllocatedAndFreeCounts() {
    var entries = WaflClassicAllocationMaps.ReadInodeMap([32, 5, 0]);

    Assert.Multiple(() => {
      Assert.That(entries[0], Is.EqualTo(new WaflClassicInodeMapEntry(32, 0)));
      Assert.That(entries[0].IsFull, Is.True);
      Assert.That(entries[1], Is.EqualTo(new WaflClassicInodeMapEntry(5, 27)));
      Assert.That(entries[2], Is.EqualTo(new WaflClassicInodeMapEntry(0, 32)));
      Assert.That(entries[2].IsEmpty, Is.True);
    });
  }

  [Test, Category("Sad")]
  public void BlockMap_RejectsPartialEntry() {
    Assert.Throws<ArgumentException>(() => _ = WaflClassicAllocationMaps.ReadBlockMapEntry([0, 0, 0], false));
    Assert.Throws<InvalidDataException>(() => _ = WaflClassicAllocationMaps.ReadBlockMap([0, 0, 0, 0, 0], false));
  }

  [Test, Category("Sad")]
  public void BlockMap_RejectsSnapshotIndexOutsidePublishedTwentyBits() {
    var entry = WaflClassicAllocationMaps.ReadBlockMapEntry([0, 0, 0, 0], false);

    Assert.Multiple(() => {
      Assert.Throws<ArgumentOutOfRangeException>(() => entry.IsReferencedBySnapshot(-1));
      Assert.Throws<ArgumentOutOfRangeException>(() => entry.IsReferencedBySnapshot(20));
    });
  }

  [Test, Category("Sad")]
  public void InodeMap_RejectsImpossibleAllocatedCount() {
    Assert.Throws<InvalidDataException>(() => _ = WaflClassicAllocationMaps.ReadInodeMapEntry(33));
  }
}
