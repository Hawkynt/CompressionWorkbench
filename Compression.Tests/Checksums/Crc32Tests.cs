using System.Text;
using Compression.Core.Checksums;

namespace Compression.Tests.Checksums;

[TestFixture]
public class Crc32Tests {
  [Category("ThemVsUs")]
  [Test]
  public void Compute_RfcTestVector_123456789() {
    // CRC-32 of "123456789" = 0xCBF43926 (well-known test vector)
    var data = Encoding.ASCII.GetBytes("123456789");
    var crc = Crc32.Compute(data);
    Assert.That(crc, Is.EqualTo(0xCBF43926u));
  }

  [Category("EdgeCase")]
  [Test]
  public void Compute_EmptyData_ReturnsZero() {
    var crc = Crc32.Compute([]);
    Assert.That(crc, Is.EqualTo(0u));
  }

  [Category("HappyPath")]
  [Test]
  public void IncrementalUpdate_MatchesBulk() {
    var data = Encoding.ASCII.GetBytes("Hello, World!");

    var bulkCrc = Crc32.Compute(data);

    var crc = new Crc32();
    foreach (var b in data)
      crc.Update(b);

    Assert.That(crc.Value, Is.EqualTo(bulkCrc));
  }

  [Category("HappyPath")]
  [Test]
  public void IncrementalUpdate_SpanChunks_MatchesBulk() {
    var data = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog");
    var bulkCrc = Crc32.Compute(data);

    var crc = new Crc32();
    crc.Update(data.AsSpan(0, 10));
    crc.Update(data.AsSpan(10, 15));
    crc.Update(data.AsSpan(25));

    Assert.That(crc.Value, Is.EqualTo(bulkCrc));
  }

  [Category("HappyPath")]
  [Test]
  public void Reset_ResetsToInitialState() {
    var crc = new Crc32();
    crc.Update(Encoding.ASCII.GetBytes("test"));
    crc.Reset();

    Assert.That(crc.Value, Is.EqualTo(0u));
  }

  [Category("ThemVsUs")]
  [Test]
  public void CastagnoliPolynomial_DifferentFromIeee() {
    var data = Encoding.ASCII.GetBytes("123456789");
    var ieee = Crc32.Compute(data);
    var castagnoli = Crc32.Compute(data, Crc32.Castagnoli);

    Assert.That(ieee, Is.Not.EqualTo(castagnoli));
    // CRC-32C of "123456789" = 0xE3069283
    Assert.That(castagnoli, Is.EqualTo(0xE3069283u));
  }
}
