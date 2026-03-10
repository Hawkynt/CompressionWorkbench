using System.Text;
using Compression.Core.Checksums;

namespace Compression.Tests.Checksums;

[TestFixture]
public class Crc16Tests {
  [Test]
  public void Compute_ARC_KnownVector() {
    // CRC-16/ARC of "123456789" = 0xBB3D
    byte[] data = Encoding.ASCII.GetBytes("123456789");
    ushort crc = Crc16.Compute(data);
    Assert.That(crc, Is.EqualTo((ushort)0xBB3D));
  }

  [Test]
  public void Compute_EmptyData_ReturnsZero() {
    ushort crc = Crc16.Compute([]);
    Assert.That(crc, Is.EqualTo((ushort)0));
  }

  [Test]
  public void IncrementalUpdate_MatchesBulk() {
    byte[] data = Encoding.ASCII.GetBytes("Hello, World!");
    ushort bulkCrc = Crc16.Compute(data);

    var crc = new Crc16();
    foreach (byte b in data)
      crc.Update(b);

    Assert.That((ushort)crc.Value, Is.EqualTo(bulkCrc));
  }

  [Test]
  public void Reset_ResetsToInitialState() {
    var crc = new Crc16();
    crc.Update(Encoding.ASCII.GetBytes("test"));
    crc.Reset();

    Assert.That((ushort)crc.Value, Is.EqualTo((ushort)0));
  }
}
