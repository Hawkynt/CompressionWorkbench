using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Ext;
using FileSystem.Lustre;

namespace Compression.Tests.Lustre;

[TestFixture]
public sealed class LustreMaintenanceSafetyTests {

  [Test, Category("ExceptionalCase")]
  public void Wipe_RefusesCorruptedBlockBitmapBeforeWritingAnything() {
    var data = BuildChecksummedLdiskfsImage();
    CorruptFirstBlockBitmap(data);
    var expected = (byte[])data.Clone();

    using var image = new MemoryStream(data, writable: true);
    var descriptor = new LustreFormatDescriptor();

    var ex = Assert.Throws<InvalidDataException>(() => ((IWipeEmpty)descriptor).WipeUnusedSpace(image));
    Assert.That(ex!.Message, Does.Contain("bitmap checksum mismatch").IgnoreCase);
    Assert.That(image.ToArray(), Is.EqualTo(expected),
      "Checksum failure must happen before the wipe overwrites any block.");
  }

  [Test, Category("ExceptionalCase")]
  public void Shrink_RefusesCorruptedBlockBitmapWithoutTouchingOutput() {
    var data = BuildChecksummedLdiskfsImage();
    CorruptFirstBlockBitmap(data);

    using var source = new MemoryStream(data, writable: false);
    using var target = new MemoryStream([0x51, 0x52, 0x53, 0x54], writable: true);
    var descriptor = new LustreFormatDescriptor();

    var ex = Assert.Throws<InvalidDataException>(() => ((IArchiveShrinkable)descriptor).Shrink(source, target));
    Assert.That(ex!.Message, Does.Contain("bitmap checksum mismatch").IgnoreCase);
    Assert.That(target.ToArray(), Is.EqualTo(new byte[] { 0x51, 0x52, 0x53, 0x54 }),
      "Shrink preflight must validate source allocation metadata before replacing output.");
  }

  private static byte[] BuildChecksummedLdiskfsImage() {
    var writer = new ExtWriter();
    writer.AddFile("OBJECTS/0_1", Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
    return writer.Build(
      blockSize: 4096,
      totalBlocks: 4096,
      version: ExtWriter.ExtVersion.Ext4,
      journal: true,
      volumeLabel: "lustre-OST0000",
      inodeSize: 256);
  }

  private static void CorruptFirstBlockBitmap(byte[] image) {
    var superblock = image.AsSpan(1024, 1024);
    var blockSize = 1024 << (int)BinaryPrimitives.ReadUInt32LittleEndian(superblock.Slice(24, 4));
    var firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(superblock.Slice(20, 4));
    var featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(superblock.Slice(96, 4));
    var descriptorSize = (featureIncompat & 0x0080) != 0
      ? BinaryPrimitives.ReadUInt16LittleEndian(superblock.Slice(0xFE, 2))
      : 32;
    var descriptorOffset = checked((int)((firstDataBlock + 1L) * blockSize));
    var descriptor = image.AsSpan(descriptorOffset, descriptorSize);

    ulong bitmapBlock = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[..4]);
    if (descriptorSize >= 64)
      bitmapBlock |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(0x20, 4)) << 32;

    var bitmapOffset = checked((int)(bitmapBlock * (ulong)blockSize));
    image[bitmapOffset] ^= 0x01;
  }
}
