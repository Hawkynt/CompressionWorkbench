#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Qcow2;

namespace Compression.Tests.Qcow2;

[TestFixture]
public sealed class Qcow2MaintenanceTests {
  private const int ClusterSize = 65_536;
  private const ulong OffsetMask = 0x00FF_FFFF_FFFF_FE00UL;

  [Test, Category("RoundTrip")]
  public void Writer_LeavesZeroGuestClustersUnallocated() {
    var disk = new byte[4 * ClusterSize];
    new Random(1234).NextBytes(disk.AsSpan(2 * ClusterSize, ClusterSize));

    var image = Write(disk);
    using var stream = new MemoryStream(image, writable: false);
    var reader = new Qcow2Reader(stream);

    Assert.That(reader.ExtractDisk(), Is.EqualTo(disk));
    Assert.That(image.LongLength, Is.EqualTo(6L * ClusterSize),
      "five structural clusters plus the single non-zero guest cluster should be allocated");

    var l1Offset = checked((int)BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(40)));
    var l2Offset = checked((int)(BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(l1Offset)) & OffsetMask));
    var l2Zero = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(l2Offset));
    var l2Data = BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(l2Offset + 2 * 8));
    Assert.That(l2Zero, Is.Zero, "an all-zero guest cluster must stay unallocated");
    Assert.That(l2Data, Is.Not.Zero, "the non-zero guest cluster must have physical storage");
  }

  [Test, Category("RoundTrip")]
  public void Shrink_ReclaimsAllocatedZeroClustersAndPreservesGuestDisk() {
    var disk = new byte[3 * ClusterSize];
    new Random(77).NextBytes(disk);
    var dense = Write(disk);

    // Turn each allocated guest cluster into zero bytes without changing the
    // allocation tables. This models a fully allocated image whose guest freed
    // or zeroed its data, leaving physical clusters shrink can discard.
    var l1Offset = checked((int)BinaryPrimitives.ReadUInt64BigEndian(dense.AsSpan(40)));
    var l2Offset = checked((int)(BinaryPrimitives.ReadUInt64BigEndian(dense.AsSpan(l1Offset)) & OffsetMask));
    for (var cluster = 0; cluster < 3; ++cluster) {
      var entry = BinaryPrimitives.ReadUInt64BigEndian(dense.AsSpan(l2Offset + cluster * 8));
      var hostOffset = checked((int)(entry & OffsetMask));
      dense.AsSpan(hostOffset, ClusterSize).Clear();
    }

    using var beforeStream = new MemoryStream(dense, writable: false);
    var beforeReader = new Qcow2Reader(beforeStream);
    var guestBefore = beforeReader.ExtractDisk();
    Assert.That(guestBefore, Is.EqualTo(new byte[disk.Length]));

    using var source = new MemoryStream(dense, writable: false);
    using var target = new MemoryStream();
    ((IArchiveShrinkable)new Qcow2FormatDescriptor()).Shrink(source, target);

    var compact = target.ToArray();
    Assert.That(compact.LongLength, Is.LessThan(dense.LongLength));
    using var afterStream = new MemoryStream(compact, writable: false);
    var afterReader = new Qcow2Reader(afterStream);
    Assert.That(afterReader.ExtractDisk(), Is.EqualTo(guestBefore));
  }

  [Test, Category("RoundTrip")]
  public void Shrink_UnsupportedV3ProfileCopiesThroughUnchanged() {
    var disk = new byte[ClusterSize];
    disk[17] = 0xA5;
    var image = Write(disk);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4), 3);

    using var source = new MemoryStream(image, writable: false);
    using var target = new MemoryStream();
    ((IArchiveShrinkable)new Qcow2FormatDescriptor()).Shrink(source, target);

    Assert.That(target.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesShrinkInterface() {
    Assert.That(new Qcow2FormatDescriptor(), Is.InstanceOf<IArchiveShrinkable>());
  }

  private static byte[] Write(byte[] disk) {
    var writer = new Qcow2Writer();
    writer.SetDiskImage(disk);
    using var stream = new MemoryStream();
    writer.WriteTo(stream);
    return stream.ToArray();
  }
}
