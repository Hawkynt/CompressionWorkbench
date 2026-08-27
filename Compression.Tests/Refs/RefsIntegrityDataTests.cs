using System.Buffers.Binary;
using FileSystem.Refs;

namespace Compression.Tests.Refs;

[TestFixture]
public sealed class RefsIntegrityDataTests {
  [Test, Category("HappyPath")]
  public void InlineIntegrity_StampsCrc32CAndClearsReservedDword() {
    var extent = IntegrityExtent(valueRelativeOffset: 40);
    var value = new byte[96];
    var cluster = Enumerable.Range(0, 4096).Select(i => unchecked((byte)(i * 17))).ToArray();
    var checksumOffset = 64;
    BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(checksumOffset + 4, 4), 0xDEADBEEF);

    RefsIntegrityDataVerifier.StampInlineChecksum(value, extent, cluster, 4096);

    Assert.Multiple(() => {
      Assert.That(
        BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(checksumOffset, 4)),
        Is.EqualTo(RefsChecksum.Crc32C(cluster)));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(checksumOffset + 4, 4)), Is.Zero);
      Assert.That(RefsIntegrityDataVerifier.GetInlineChecksumOffset(extent, value.Length), Is.EqualTo(checksumOffset));
    });
  }

  [Test, Category("HappyPath")]
  public void InlineIntegrity_BuildUpdatedValueDoesNotMutateSource() {
    var extent = IntegrityExtent(valueRelativeOffset: 8);
    var source = Enumerable.Repeat((byte)0xCC, 64).ToArray();
    var before = source.ToArray();
    var cluster = new byte[4096];
    cluster[123] = 0x42;

    var changed = RefsIntegrityDataVerifier.BuildUpdatedOwningValue(source, extent, cluster, 4096);

    Assert.Multiple(() => {
      Assert.That(source, Is.EqualTo(before));
      Assert.That(changed, Is.Not.EqualTo(before));
      Assert.That(
        BinaryPrimitives.ReadUInt32LittleEndian(changed.AsSpan(32, 4)),
        Is.EqualTo(RefsChecksum.Crc32C(cluster)));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(changed.AsSpan(36, 4)), Is.Zero);
    });
  }

  [Test, Category("ErrorHandling")]
  public void InlineIntegrity_RejectsWrongClusterGeometry() {
    var extent = IntegrityExtent(valueRelativeOffset: 0);
    var value = new byte[64];

    Assert.Throws<NotSupportedException>(() =>
      RefsIntegrityDataVerifier.StampInlineChecksum(value, extent, new byte[65536], 65536));
    Assert.Throws<ArgumentException>(() =>
      RefsIntegrityDataVerifier.StampInlineChecksum(value, extent, new byte[2048], 4096));
  }

  [Test, Category("ErrorHandling")]
  public void InlineIntegrity_RejectsMultiClusterAndOutOfRangeElements() {
    var multi = IntegrityExtent(valueRelativeOffset: 0) with { ClusterCount = 2 };
    var outside = IntegrityExtent(valueRelativeOffset: 48);

    Assert.Throws<InvalidDataException>(() =>
      RefsIntegrityDataVerifier.StampInlineChecksum(new byte[64], multi, new byte[4096], 4096));
    Assert.Throws<InvalidDataException>(() =>
      RefsIntegrityDataVerifier.StampInlineChecksum(new byte[64], outside, new byte[4096], 4096));
  }

  [Test, Category("ErrorHandling")]
  public void InlineIntegrity_RejectsOrdinaryExtent() {
    var ordinary = IntegrityExtent(valueRelativeOffset: 0) with { Flags = 0x180040 };

    Assert.Throws<ArgumentException>(() =>
      RefsIntegrityDataVerifier.ComputeInlineChecksum(ordinary, new byte[4096], 4096));
  }

  private static RefsDataExtent IntegrityExtent(int valueRelativeOffset)
    => new(
      FileVcn: 0,
      VirtualLcn: 0x1000,
      PhysicalLcn: 0x2000,
      ClusterCount: 1,
      Flags: RefsIntegrityDataVerifier.InlineIntegrityExtentFlags,
      IsSparse: false,
      ValueRelativeOffset: valueRelativeOffset);
}
