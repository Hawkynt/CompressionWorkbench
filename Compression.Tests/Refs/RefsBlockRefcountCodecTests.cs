using System.Buffers.Binary;
using FileSystem.Refs;

namespace Compression.Tests.Refs;

[TestFixture]
public sealed class RefsBlockRefcountCodecTests {
  [Test, Category("HappyPath")]
  public void AdjustCounts_PreservesDedupFlagsAndRefreshesTotal() {
    var row = BuildRow(0x4000);
    WriteRaw(row, 3, 0x8002);
    WriteRaw(row, 9, 0x4000);
    RefsBlockRefcountCodec.RefreshTotal(row);

    var changed = RefsBlockRefcountCodec.AdjustCounts(
      row,
      new Dictionary<int, int> { [3] = +2 });

    Assert.Multiple(() => {
      Assert.That(RefsBlockRefcountCodec.ReadRaw(changed, 3), Is.EqualTo(0x8004));
      Assert.That(RefsBlockRefcountCodec.ReadRaw(changed, 9), Is.EqualTo(0x4000));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(changed.AsSpan(0x18, 4)), Is.EqualTo(4));
      Assert.That(RefsBlockRefcountCodec.HasValidTotal(changed), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void AdjustCounts_DecrementsToZeroWithoutDroppingFlags() {
    var row = BuildRow(0x8000);
    WriteRaw(row, 1, 0x8001);
    RefsBlockRefcountCodec.RefreshTotal(row);

    var changed = RefsBlockRefcountCodec.AdjustCounts(
      row,
      new Dictionary<int, int> { [1] = -1 });

    Assert.Multiple(() => {
      Assert.That(RefsBlockRefcountCodec.ReadRaw(changed, 1), Is.EqualTo(0x8000));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(changed.AsSpan(0x18, 4)), Is.Zero);
      Assert.That(RefsBlockRefcountCodec.IsUnflaggedZeroRow(changed), Is.False);
    });
  }

  [Test, Category("HappyPath")]
  public void AdjustCounts_RecognizesRemovableZeroRow() {
    var row = BuildRow(0xC000);
    WriteRaw(row, 20, 1);
    RefsBlockRefcountCodec.RefreshTotal(row);

    var changed = RefsBlockRefcountCodec.AdjustCounts(
      row,
      new Dictionary<int, int> { [20] = -1 });

    Assert.That(RefsBlockRefcountCodec.IsUnflaggedZeroRow(changed), Is.True);
  }

  [Test, Category("ErrorHandling")]
  public void AdjustCounts_RejectsUnderflowAndOverflow() {
    var row = BuildRow(0x10000);
    RefsBlockRefcountCodec.RefreshTotal(row);

    Assert.Throws<InvalidOperationException>(() => RefsBlockRefcountCodec.AdjustCounts(
      row,
      new Dictionary<int, int> { [0] = -1 }));

    WriteRaw(row, 0, RefsBlockRefcountCodec.CountMask);
    RefsBlockRefcountCodec.RefreshTotal(row);
    Assert.Throws<InvalidOperationException>(() => RefsBlockRefcountCodec.AdjustCounts(
      row,
      new Dictionary<int, int> { [0] = +1 }));
  }

  [Test, Category("HappyPath")]
  public void BuildKey_UsesAlignedRangeAndFixedCount() {
    var key = RefsBlockRefcountCodec.BuildKey(0x123400UL);

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(0, 8)), Is.EqualTo(0x123400UL));
      Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8, 8)), Is.EqualTo(0x400UL));
    });
  }

  [Test, Category("HappyPath")]
  public void BuildFreshValue_InitializesRangeStampAndZeroTotal() {
    var row = RefsBlockRefcountCodec.BuildFreshValue(0x123400UL, modificationStamp: 0xE5);

    Assert.Multiple(() => {
      Assert.That(row, Has.Length.EqualTo(0x820));
      Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(row.AsSpan(0x00, 8)), Is.EqualTo(0x123400UL));
      Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(row.AsSpan(0x08, 8)), Is.EqualTo(0x400UL));
      Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(row.AsSpan(0x10, 8)), Is.EqualTo(0xE5UL));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(0x18, 4)), Is.Zero);
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(0x81C, 4)), Is.Zero);
      Assert.That(RefsBlockRefcountCodec.HasValidTotal(row), Is.True);
      Assert.That(RefsBlockRefcountCodec.IsUnflaggedZeroRow(row), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void AddCloneReferences_FreshRowMaterializesImplicitOwnerAsCountTwo() {
    var row = RefsBlockRefcountCodec.BuildFreshValue(0x4000UL, modificationStamp: 0x99);

    var changed = RefsBlockRefcountCodec.AddCloneReferences(
      row,
      new Dictionary<int, int> { [7] = 1 });

    Assert.Multiple(() => {
      Assert.That(RefsBlockRefcountCodec.ReadCount(changed, 7), Is.EqualTo(2));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(changed.AsSpan(0x18, 4)), Is.EqualTo(2));
      Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(changed.AsSpan(0x10, 8)), Is.EqualTo(0x99UL));
    });
  }

  [Test, Category("HappyPath")]
  public void AddCloneReferences_ExistingRowZeroSlotStillMaterializesCountTwo() {
    var row = BuildRow(0x8000);
    WriteRaw(row, 3, 2); // another shared cluster makes the sparse row already exist
    RefsBlockRefcountCodec.RefreshTotal(row);

    var changed = RefsBlockRefcountCodec.AddCloneReferences(
      row,
      new Dictionary<int, int> { [9] = 1 });

    Assert.Multiple(() => {
      Assert.That(RefsBlockRefcountCodec.ReadCount(changed, 3), Is.EqualTo(2));
      Assert.That(RefsBlockRefcountCodec.ReadCount(changed, 9), Is.EqualTo(2));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(changed.AsSpan(0x18, 4)), Is.EqualTo(4));
    });
  }

  [Test, Category("HappyPath")]
  public void AddCloneReferences_TrackedSlotIncrementsWithoutImplicitBaseline() {
    var row = BuildRow(0xC000);
    WriteRaw(row, 12, 2);
    RefsBlockRefcountCodec.RefreshTotal(row);

    var changed = RefsBlockRefcountCodec.AddCloneReferences(
      row,
      new Dictionary<int, int> { [12] = 1 });

    Assert.That(RefsBlockRefcountCodec.ReadCount(changed, 12), Is.EqualTo(3));
  }

  [Test, Category("HappyPath")]
  public void AddCloneReferences_FlaggedZeroSlotDoesNotAssumeOrdinaryOwner() {
    var row = BuildRow(0x10000);
    WriteRaw(row, 5, RefsBlockRefcountCodec.DedupManagedMask);
    RefsBlockRefcountCodec.RefreshTotal(row);

    var changed = RefsBlockRefcountCodec.AddCloneReferences(
      row,
      new Dictionary<int, int> { [5] = 1 });

    Assert.That(RefsBlockRefcountCodec.ReadRaw(changed, 5), Is.EqualTo(0x8001));
  }

  [Test, Category("ErrorHandling")]
  public void AddCloneReferences_RejectsNonPositiveReferenceCount() {
    var row = BuildRow(0x14000);

    Assert.Multiple(() => {
      Assert.Throws<ArgumentOutOfRangeException>(() => RefsBlockRefcountCodec.AddCloneReferences(
        row,
        new Dictionary<int, int> { [1] = 0 }));
      Assert.Throws<ArgumentOutOfRangeException>(() => RefsBlockRefcountCodec.AddCloneReferences(
        row,
        new Dictionary<int, int> { [1] = -1 }));
    });
  }

  [Test, Category("ErrorHandling")]
  public void FreshRowBuilders_RejectUnalignedRangeStart() {
    Assert.Multiple(() => {
      Assert.Throws<ArgumentOutOfRangeException>(() => RefsBlockRefcountCodec.BuildKey(0x123401UL));
      Assert.Throws<ArgumentOutOfRangeException>(() => RefsBlockRefcountCodec.BuildFreshValue(0x123401UL, 1));
    });
  }

  private static byte[] BuildRow(ulong start) {
    var row = new byte[RefsBlockRefcountCodec.NormalValueSize];
    BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(0, 8), start);
    BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(8, 8), RefsBlockRefcountCodec.EntriesPerRow);
    BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(0x10, 8), 0xE4);
    return row;
  }

  private static void WriteRaw(byte[] row, int index, ushort value)
    => BinaryPrimitives.WriteUInt16LittleEndian(
      row.AsSpan(RefsBlockRefcountCodec.EntriesOffset + index * 2, 2),
      value);
}
