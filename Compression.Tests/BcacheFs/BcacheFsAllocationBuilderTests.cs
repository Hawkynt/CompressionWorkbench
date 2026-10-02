using System.Buffers.Binary;
using FileSystem.BcacheFs;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// The derived allocation information, rule by rule. Each rule is one the
/// bcachefs checker enforces; <c>BcacheFsExternalConformanceTests</c> shows the
/// checker agreeing, these pin the rules where the checker is not installed.
/// </summary>
[TestFixture]
public sealed class BcacheFsAllocationBuilderTests {

  private const long DeviceSectors = 512L * BucketSectors;

  private static BcacheFsAllocationBuilder.Geometry Geometry() => new(
    DeviceSectors,
    [PrimarySbSector, PrimarySbSector + SbSlotSectors, DeviceSectors - SbSlotSectors],
    [(33, 16)]);

  private static Key Extent(ulong inode, long sector, uint sectors, ulong end) {
    var value = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(value, ExtentCrc32((int)sectors, 0));
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(8), ExtentPointer(sector));
    return new Key(KeyExtent, new Bpos(inode, end, SnapshotIdMax), sectors, value);
  }

  private static uint Flags(Key alloc) => BinaryPrimitives.ReadUInt32LittleEndian(alloc.Value.AsSpan(8));

  [Test, Category("HappyPath")]
  public void GivenOnlySuperblocksAndJournal_WhenBuilt_ThenEveryBucketIsCountedOnceAndThePartSbBucketIsFragmented() {
    var result = BcacheFsAllocationBuilder.Build(Geometry(), [], []);
    var usage = result.Usage;

    Assert.Multiple(() => {
      Assert.That(usage.Buckets.Aggregate(0UL, (a, b) => a + b), Is.EqualTo(512UL));
      Assert.That(usage.Buckets[DataJournal], Is.EqualTo(16UL));
      // Sectors 0..8 plus two slots in front, one at the tail: 8 + 3 × 2048.
      Assert.That(usage.Sectors[DataSb], Is.EqualTo(8UL + 3 * SbSlotSectors));
      // The front slots end eight sectors into bucket 32; the other 120 are fragmented.
      Assert.That(usage.Fragmented[DataSb], Is.EqualTo((ulong)(BucketSectors - 8)));
      Assert.That(usage.Fragmented[DataFree], Is.Zero, "a free bucket is unused, not fragmented");
      Assert.That(result.Alloc.All(k => k.Value.Length == AllocV4Bytes), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenABucketWrittenTo_WhenBuilt_ThenItsAllocKeyCarriesBackpointersStartAndTheWrittenFlags() {
    var result = BcacheFsAllocationBuilder.Build(Geometry(), [], [Extent(4096, 200 * BucketSectors, 128, 128)]);
    var alloc = result.Alloc.Single(k => k.Position.Offset == 200);

    Assert.Multiple(() => {
      Assert.That(alloc.Value[14], Is.EqualTo(DataUser));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(alloc.Value.AsSpan(16)), Is.EqualTo(128u));
      Assert.That((Flags(alloc) >> 2) & 0x3F, Is.EqualTo((uint)AllocV4U64s), "backpointers_start");
      Assert.That(Flags(alloc) & 0b11, Is.EqualTo(0b11u), "need_discard and need_inc_gen");
    });
  }

  [Test, Category("BoundaryValue")]
  [TestCase(1u, true)]
  [TestCase(127u, true)]
  [TestCase(128u, false)]
  public void GivenAUserBucket_WhenPartlyFilled_ThenOnlyThenItHasAFragmentationLruEntry(uint sectors, bool expected) {
    var result = BcacheFsAllocationBuilder.Build(Geometry(), [], [Extent(4096, 300 * BucketSectors, sectors, sectors)]);
    var alloc = result.Alloc.Single(k => k.Position.Offset == 300);
    var fragmentation = BinaryPrimitives.ReadUInt64LittleEndian(alloc.Value.AsSpan(48));

    Assert.That(fragmentation != 0, Is.EqualTo(expected));
    if (!expected) {
      Assert.That(result.Lru, Is.Empty);
      return;
    }
    Assert.Multiple(() => {
      Assert.That(fragmentation, Is.EqualTo(((ulong)sectors << 31) / BucketSectors));
      var lru = result.Lru.Single();
      Assert.That(lru.Type, Is.EqualTo(KeySet));
      Assert.That(lru.Position, Is.EqualTo(new Bpos((LruFragmentationId << LruTimeBits) | fragmentation, 300, 0)));
    });
  }

  [Test, Category("EdgeCase")]
  public void GivenAnEmptiedBucketThatKeepsItsGeneration_WhenBuilt_ThenItIsFreeWithoutNeedDiscardAndIndexedInBucketGens() {
    var generations = new Dictionary<long, BcacheFsAllocationBuilder.Generation> { [260] = new(3, 3) };
    var result = BcacheFsAllocationBuilder.Build(Geometry(), [], [], generations);
    var alloc = result.Alloc.Single(k => k.Position.Offset == 260);

    Assert.Multiple(() => {
      Assert.That(alloc.Value[14], Is.EqualTo(DataFree));
      Assert.That(alloc.Value[12], Is.EqualTo(3));
      Assert.That(Flags(alloc) & 1, Is.Zero, "need_discard would make the data type need_discard, not free");
      var gens = result.BucketGens.Single();
      Assert.That(gens.Position.Offset, Is.EqualTo(260UL / BucketGensNr));
      Assert.That(gens.Value[260 % BucketGensNr], Is.EqualTo(3));
      Assert.That(result.Freespace.Any(k => k.Position.Offset - k.Size <= 260 && 260 < k.Position.Offset), Is.True,
        "the bucket is still offered as free space");
    });
  }

  [Test, Category("BoundaryValue")]
  [TestCase((byte)15, 0UL)]
  [TestCase((byte)16, 1UL)]
  public void GivenAFreeBucketWhoseGenerationRanAhead_WhenBuilt_ThenItsFreespacePositionCarriesTheHighBits(byte ahead, ulong bits) {
    var generations = new Dictionary<long, BcacheFsAllocationBuilder.Generation> { [400] = new(ahead, 0) };
    var result = BcacheFsAllocationBuilder.Build(Geometry(), [], [], generations);
    var run = result.Freespace.Single(k => (k.Position.Offset & ((1UL << 56) - 1)) - k.Size <= 400
      && 400 < (k.Position.Offset & ((1UL << 56) - 1)));

    Assert.That(run.Position.Offset >> 56, Is.EqualTo(bits));
    if (bits != 0)
      Assert.That(run.Size, Is.EqualTo(1u), "a bucket whose bits differ from its neighbours' is a run of its own");
  }

  [Test, Category("HappyPath")]
  public void GivenAnExtentAndANode_WhenBuilt_ThenEachHasABackpointerAtItsSectorShiftedByTen() {
    var extent = Extent(4096, 250 * BucketSectors, 64, 64);
    var nodeMax = new Bpos(5, 6, 7);
    var result = BcacheFsAllocationBuilder.Build(Geometry(), [new(BtreeInodes, 0, nodeMax, 60 * BucketSectors)], [extent]);

    var forExtent = result.Backpointers.Single(k => k.Position.Offset == (ulong)(250 * BucketSectors) << BackpointerShift);
    var forNode = result.Backpointers.Single(k => k.Position.Offset == (ulong)(60 * BucketSectors) << BackpointerShift);
    Assert.Multiple(() => {
      Assert.That(forExtent.Value[0], Is.EqualTo(BtreeExtents));
      Assert.That(forExtent.Value[2], Is.EqualTo(DataUser));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(forExtent.Value.AsSpan(8)), Is.EqualTo(64u));
      Assert.That(ReadBpos(forExtent.Value.AsSpan(12)), Is.EqualTo(extent.Position));
      Assert.That(forNode.Value[0], Is.EqualTo(BtreeInodes));
      Assert.That(forNode.Value[1], Is.EqualTo(1), "a node's backpointer gives the level of the pointer to it");
      Assert.That(ReadBpos(forNode.Value.AsSpan(12)), Is.EqualTo(nodeMax));
    });
  }

  [Test, Category("ErrorHandling")]
  public void GivenFileDataInsideAJournalBucket_WhenBuilt_ThenItIsRefused()
    => Assert.Throws<InvalidOperationException>(() =>
      BcacheFsAllocationBuilder.Build(Geometry(), [], [Extent(4096, 40 * BucketSectors, 8, 8)]));
}
