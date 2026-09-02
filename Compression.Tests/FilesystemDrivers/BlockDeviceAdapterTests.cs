using Compression.Registry;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class BlockDeviceAdapterTests {
  [Test, Category("Driver")]
  public void StreamBlockDevice_ProvidesAlignedPositionalBlocks() {
    var bytes = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
    using var image = new MemoryStream(bytes.ToArray(), writable: true);
    using var device = new StreamBlockDevice(image, 512, writable: true);

    Span<byte> block = stackalloc byte[512];
    Assert.That(device.ReadBlocks(2, block), Is.EqualTo(1));
    Assert.That(block[0], Is.EqualTo(bytes[1024]));
    Assert.That(block[511], Is.EqualTo(bytes[1535]));

    block.Fill(0xA5);
    device.WriteBlocks(1, block);
    Assert.That(image.ToArray().AsSpan(512, 512).ToArray(), Is.All.EqualTo((byte)0xA5));
    Assert.That(image.ToArray().AsSpan(0, 512).ToArray(), Is.EqualTo(bytes.AsSpan(0, 512).ToArray()));
  }

  [Test, Category("Driver")]
  public void BlockDeviceStream_UnalignedWriteTouchesOnlyNecessaryBlocks() {
    var original = Enumerable.Range(0, 2048).Select(i => (byte)(i * 17)).ToArray();
    using var image = new MemoryStream(original.ToArray(), writable: true);
    using var device = new StreamBlockDevice(image, 512, writable: true);
    using var stream = new BlockDeviceStream(device);

    stream.Position = 510;
    stream.Write(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 });
    stream.Flush();

    var actual = image.ToArray();
    Assert.That(actual.AsSpan(0, 510).ToArray(), Is.EqualTo(original.AsSpan(0, 510).ToArray()));
    Assert.That(actual.AsSpan(510, 5).ToArray(), Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 }));
    Assert.That(actual.AsSpan(515).ToArray(), Is.EqualTo(original.AsSpan(515).ToArray()));
  }

  [Test, Category("Driver")]
  public void BlockDeviceStream_ProvidesSeekableByteReadsAcrossBlockBoundaries() {
    var bytes = Enumerable.Range(0, 1536).Select(i => (byte)(i ^ 0x5A)).ToArray();
    using var image = new MemoryStream(bytes, writable: false);
    using var device = new StreamBlockDevice(image, 512, writable: false);
    using var stream = new BlockDeviceStream(device);

    stream.Position = 509;
    var read = new byte[11];
    Assert.That(stream.Read(read), Is.EqualTo(read.Length));
    Assert.That(read, Is.EqualTo(bytes.AsSpan(509, 11).ToArray()));
    Assert.That(stream.CanWrite, Is.False);
    Assert.That(() => stream.WriteByte(1), Throws.InstanceOf<NotSupportedException>());
  }
}
