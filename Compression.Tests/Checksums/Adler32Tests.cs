using System.Text;
using Compression.Core.Checksums;

namespace Compression.Tests.Checksums;

[TestFixture]
public class Adler32Tests {
  [Category("ThemVsUs")]
  [Test]
  public void Compute_KnownVector_Wikipedia() {
    // Adler-32 of "Wikipedia" = 0x11E60398
    var data = Encoding.ASCII.GetBytes("Wikipedia");
    var adler = Adler32.Compute(data);
    Assert.That(adler, Is.EqualTo(0x11E60398u));
  }

  [Category("EdgeCase")]
  [Test]
  public void Compute_EmptyData_ReturnsOne() {
    // Adler-32 of empty data = 1 (initial value)
    var adler = Adler32.Compute([]);
    Assert.That(adler, Is.EqualTo(1u));
  }

  [Category("HappyPath")]
  [Test]
  public void IncrementalUpdate_MatchesBulk() {
    var data = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog");
    var bulkAdler = Adler32.Compute(data);

    var adler = new Adler32();
    adler.Update(data.AsSpan(0, 10));
    adler.Update(data.AsSpan(10));

    Assert.That(adler.Value, Is.EqualTo(bulkAdler));
  }

  [Category("HappyPath")]
  [Test]
  public void IncrementalUpdate_ByteByByte_MatchesBulk() {
    var data = Encoding.ASCII.GetBytes("Hello");
    var bulkAdler = Adler32.Compute(data);

    var adler = new Adler32();
    foreach (var b in data)
      adler.Update(b);

    Assert.That(adler.Value, Is.EqualTo(bulkAdler));
  }

  [Category("HappyPath")]
  [Test]
  public void Reset_ResetsToInitialState() {
    var adler = new Adler32();
    adler.Update(Encoding.ASCII.GetBytes("test"));
    adler.Reset();

    Assert.That(adler.Value, Is.EqualTo(1u));
  }

  [Category("Boundary")]
  [Test]
  public void LargeData_NmaxOptimization_MatchesByteByByte() {
    // Test with data larger than Nmax (5552) to exercise the chunking logic
    var data = new byte[10000];
    var rng = new Random(42);
    rng.NextBytes(data);

    var byByte = new Adler32();
    foreach (var b in data)
      byByte.Update(b);

    var bulk = Adler32.Compute(data);

    Assert.That(bulk, Is.EqualTo(byByte.Value));
  }
}
