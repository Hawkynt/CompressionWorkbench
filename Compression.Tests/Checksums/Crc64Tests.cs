using Compression.Core.Checksums;

namespace Compression.Tests.Checksums;

[TestFixture]
public class Crc64Tests {
  [Test]
  public void Compute_EmptyData() {
    ulong result = Crc64.Compute([]);
    Assert.That(result, Is.EqualTo(0UL));
  }

  [Test]
  public void Compute_KnownValues() {
    // "123456789" with ECMA-182 polynomial
    byte[] data = "123456789"u8.ToArray();
    ulong result = Crc64.Compute(data);
    Assert.That(result, Is.EqualTo(0x995DC9BBDF1939FAUL));
  }

  [Test]
  public void Update_Incremental_MatchesBatch() {
    byte[] data = "Hello, World!"u8.ToArray();

    ulong batchResult = Crc64.Compute(data);

    var crc = new Crc64();
    foreach (byte b in data)
      crc.Update(b);

    Assert.That(crc.Value64, Is.EqualTo(batchResult));
  }

  [Test]
  public void Reset_ClearsState() {
    var crc = new Crc64();
    crc.Update("test"u8);
    Assert.That(crc.Value64, Is.Not.EqualTo(0UL));

    crc.Reset();
    Assert.That(crc.Value64, Is.EqualTo(0UL));
  }
}
