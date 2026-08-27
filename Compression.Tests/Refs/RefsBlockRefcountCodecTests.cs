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
