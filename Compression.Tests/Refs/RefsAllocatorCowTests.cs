using System.Buffers.Binary;
using FileSystem.Refs;

namespace Compression.Tests.Refs;

[TestFixture]
public sealed class RefsAllocatorCowTests {
  [Test, Category("HappyPath")]
  public void RowCodec_ExpandsCompactFreeRowToBitmap() {
    var row = CompactRow(start: 0x1000, length: 16, allocated: false, headerTag: 0x0200);

    var changed = RefsAllocatorRowCodec.SetAllocated(
      row,
      16,
      new ulong[] { 3, 7 },
      allocated: true,
      RefsAllocatorTier.Medium,
      refsMinorVersion: 14);

    Assert.Multiple(() => {
      Assert.That(changed, Has.Length.EqualTo(0x18 + 2048));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(0x10, 2)), Is.EqualTo(14));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(0x12, 2)), Is.EqualTo(0x01));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(0x14, 2)), Is.EqualTo(0x0218));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(0x16, 2)), Is.EqualTo(2));
      Assert.That(RefsAllocatorRowCodec.ReadAllocated(changed, 16, 3), Is.True);
      Assert.That(RefsAllocatorRowCodec.ReadAllocated(changed, 16, 7), Is.True);
      Assert.That(RefsAllocatorRowCodec.ReadAllocated(changed, 16, 6), Is.False);
      Assert.That(RefsAllocatorRowCodec.IsStructurallyValid(changed, 16), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void RowCodec_CollapsesFullyAllocatedBitmapToCompactRow() {
    var row = CompactRow(start: 0x2000, length: 16, allocated: false, headerTag: 0x0100);

    var changed = RefsAllocatorRowCodec.SetAllocated(
      row,
      16,
      Enumerable.Range(0, 16).Select(i => (ulong)i).ToArray(),
      allocated: true,
      RefsAllocatorTier.Container,
      refsMinorVersion: 14);

    Assert.Multiple(() => {
      Assert.That(changed, Has.Length.EqualTo(24));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(0x10, 2)), Is.Zero);
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(0x12, 2)), Is.EqualTo(0x02));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(0x14, 2)), Is.EqualTo(0x0100));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(changed.AsSpan(0x16, 2)), Is.EqualTo(16));
      Assert.That(RefsAllocatorRowCodec.ReadAllocated(changed, 16, 0), Is.True);
      Assert.That(RefsAllocatorRowCodec.ReadAllocated(changed, 16, 15), Is.True);
      Assert.That(RefsAllocatorRowCodec.IsStructurallyValid(changed, 16), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void RowCodec_RoundTripsBitmapBackToCompactFree() {
    var row = CompactRow(start: 0x3000, length: 8, allocated: false, headerTag: 0x0200);
    var partial = RefsAllocatorRowCodec.SetAllocated(
      row,
      8,
      new ulong[] { 1, 2 },
      allocated: true,
      RefsAllocatorTier.Medium,
      refsMinorVersion: 14);

    var free = RefsAllocatorRowCodec.SetAllocated(
      partial,
      8,
      new ulong[] { 1, 2 },
      allocated: false,
      RefsAllocatorTier.Medium,
      refsMinorVersion: 14);

    Assert.Multiple(() => {
      Assert.That(free, Has.Length.EqualTo(24));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(free.AsSpan(0x10, 2)), Is.EqualTo(8));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(free.AsSpan(0x12, 2)), Is.EqualTo(0x05));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(free.AsSpan(0x14, 2)), Is.EqualTo(0x0200));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(free.AsSpan(0x16, 2)), Is.Zero);
      Assert.That(RefsAllocatorRowCodec.IsStructurallyValid(free, 8), Is.True);
    });
  }

  [Test, Category("ErrorHandling")]
  public void RowCodec_RejectsInconsistentCounts() {
    var row = CompactRow(start: 0x4000, length: 16, allocated: false, headerTag: 0x0100);
    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(0x10, 2), 15);

    Assert.That(RefsAllocatorRowCodec.IsStructurallyValid(row, 16), Is.False);
    Assert.Throws<InvalidDataException>(() => RefsAllocatorRowCodec.SetAllocated(
      row,
      16,
      new ulong[] { 1 },
      allocated: true,
      RefsAllocatorTier.Small,
      refsMinorVersion: 14));
  }

  private static byte[] CompactRow(ulong start, ushort length, bool allocated, ushort headerTag) {
    var row = new byte[24];
    BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(0x00, 8), start);
    BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(0x08, 8), length);
    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(0x10, 2), allocated ? (ushort)0 : length);
    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(0x12, 2), allocated ? (ushort)0x02 : (ushort)0x05);
    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(0x14, 2), headerTag);
    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(0x16, 2), allocated ? length : (ushort)0);
    return row;
  }
}
