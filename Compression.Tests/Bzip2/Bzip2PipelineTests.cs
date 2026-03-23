using FileFormat.Bzip2;

namespace Compression.Tests.Bzip2;

[TestFixture]
public class Bzip2PipelineTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Rle1_Encode_Decode_RoundTrip() {
    // Data with runs of identical bytes
    var data = new byte[100];
    Array.Fill<byte>(data, (byte)'A', 0, 20); // 20 A's
    Array.Fill<byte>(data, (byte)'B', 20, 5); // 5 B's
    Array.Fill<byte>(data, (byte)'C', 25, 75); // 75 C's

    var encoded = Bzip2Compressor.Rle1Encode(data);
    var decoded = Bzip2Compressor.Rle1Decode(encoded);

    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Rle1_NoRuns_Unchanged() {
    byte[] data = [1, 2, 3, 1, 2, 3];
    var encoded = Bzip2Compressor.Rle1Encode(data);
    var decoded = Bzip2Compressor.Rle1Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Rle2_RunARunB_Encode_Decode_RoundTrip() {
    // MTF data with lots of zeros
    var mtfData = new byte[50];
    // First 30 are zeros, then some non-zero values
    mtfData[30] = 5;
    mtfData[31] = 3;
    mtfData[32] = 1;

    var eobSymbol = 10;
    var encoded = Bzip2Compressor.Rle2Encode(mtfData, eobSymbol);
    var decoded = Bzip2Compressor.Rle2Decode(encoded.AsSpan(), eobSymbol);

    Assert.That(decoded, Is.EqualTo(mtfData));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Rle2_SingleZero() {
    byte[] mtfData = [0];
    var eobSymbol = 5;
    var encoded = Bzip2Compressor.Rle2Encode(mtfData, eobSymbol);
    var decoded = Bzip2Compressor.Rle2Decode(encoded.AsSpan(), eobSymbol);
    Assert.That(decoded, Is.EqualTo(mtfData));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Rle2_MultipleZeroRuns() {
    byte[] mtfData = [0, 0, 0, 5, 0, 0, 3, 0];
    var eobSymbol = 10;
    var encoded = Bzip2Compressor.Rle2Encode(mtfData, eobSymbol);
    var decoded = Bzip2Compressor.Rle2Decode(encoded.AsSpan(), eobSymbol);
    Assert.That(decoded, Is.EqualTo(mtfData));
  }
}
