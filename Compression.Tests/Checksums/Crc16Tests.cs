using System.Text;
using Compression.Core.Checksums;

namespace Compression.Tests.Checksums;

[TestFixture]
public class Crc16Tests {
  [Category("ThemVsUs")]
  [Test]
  public void Compute_ARC_KnownVector() {
    // CRC-16/ARC of "123456789" = 0xBB3D
    var data = Encoding.ASCII.GetBytes("123456789");
    var crc = Crc16.Compute(data);
    Assert.That(crc, Is.EqualTo((ushort)0xBB3D));
  }

  [Category("EdgeCase")]
  [Test]
  public void Compute_EmptyData_ReturnsZero() {
    var crc = Crc16.Compute([]);
    Assert.That(crc, Is.EqualTo((ushort)0));
  }

  [Category("HappyPath")]
  [Test]
  public void IncrementalUpdate_MatchesBulk() {
    var data = Encoding.ASCII.GetBytes("Hello, World!");
    var bulkCrc = Crc16.Compute(data);

    var crc = new Crc16();
    foreach (var b in data)
      crc.Update(b);

    Assert.That((ushort)crc.Value, Is.EqualTo(bulkCrc));
  }

  [Category("HappyPath")]
  [Test]
  public void Reset_ResetsToInitialState() {
    var crc = new Crc16();
    crc.Update(Encoding.ASCII.GetBytes("test"));
    crc.Reset();

    Assert.That((ushort)crc.Value, Is.EqualTo((ushort)0));
  }
}
