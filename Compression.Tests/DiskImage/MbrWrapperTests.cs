#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;

namespace Compression.Tests.DiskImage;

[TestFixture]
public class MbrWrapperTests {

  [Test]
  public void Wrap_PlacesFilesystemAtLba2048() {
    var payload = new byte[4096];
    for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)(i & 0xFF);
    var wrapped = MbrWrapper.Wrap(payload, MbrWrapper.PartitionType.Fat32Lba);

    // MBR sector is 512 bytes; partition starts at LBA 2048 (byte offset 0x100000).
    const int partitionOffset = 2048 * 512;
    for (var i = 0; i < payload.Length; ++i)
      Assert.That(wrapped[partitionOffset + i], Is.EqualTo(payload[i]));
  }

  [Test]
  public void Wrap_WritesValidBootSignature() {
    var wrapped = MbrWrapper.Wrap(new byte[1024], MbrWrapper.PartitionType.NtfsExfat);
    Assert.That(wrapped[510], Is.EqualTo((byte)0x55));
    Assert.That(wrapped[511], Is.EqualTo((byte)0xAA));
  }

  [Test]
  public void Wrap_PartitionEntryAdvertisesCorrectLbaAndSize() {
    var payload = new byte[4096];
    var wrapped = MbrWrapper.Wrap(payload, MbrWrapper.PartitionType.Linux);

    Assert.That(wrapped[446], Is.EqualTo((byte)0x00)); // not active
    Assert.That(wrapped[450], Is.EqualTo(MbrWrapper.PartitionType.Linux));

    var startLba = BinaryPrimitives.ReadUInt32LittleEndian(wrapped.AsSpan(454));
    var sectorCount = BinaryPrimitives.ReadUInt32LittleEndian(wrapped.AsSpan(458));
    Assert.That(startLba, Is.EqualTo(2048u));
    Assert.That(sectorCount, Is.EqualTo(8u)); // 4096 / 512
  }

  [Test]
  public void Wrap_ActiveFlag_SetsBoot0x80() {
    var wrapped = MbrWrapper.Wrap(new byte[512], MbrWrapper.PartitionType.Fat16, active: true);
    Assert.That(wrapped[446], Is.EqualTo((byte)0x80));
  }

  [Test]
  public void Wrap_OutputParsedByMbrParser() {
    var payload = new byte[4096];
    var wrapped = MbrWrapper.Wrap(payload, MbrWrapper.PartitionType.NtfsExfat);
    using var ms = new MemoryStream(wrapped);
    var partitions = MbrParser.Parse(ms);
    Assert.That(partitions, Has.Count.EqualTo(1));
    Assert.That(partitions[0].StartOffset, Is.EqualTo(2048L * 512));
    Assert.That(partitions[0].TypeCode, Is.EqualTo("0x07"));
  }
}
