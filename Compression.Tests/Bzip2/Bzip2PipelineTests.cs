using FileFormat.Bzip2;

namespace Compression.Tests.Bzip2;

[TestFixture]
public class Bzip2PipelineTests {
  [Test]
  public void Rle1_Encode_Decode_RoundTrip() {
    // Data with runs of identical bytes
    byte[] data = new byte[100];
    Array.Fill<byte>(data, (byte)'A', 0, 20); // 20 A's
    Array.Fill<byte>(data, (byte)'B', 20, 5); // 5 B's
    Array.Fill<byte>(data, (byte)'C', 25, 75); // 75 C's

    byte[] encoded = Bzip2Compressor.Rle1Encode(data);
    byte[] decoded = Bzip2Compressor.Rle1Decode(encoded);

    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void Rle1_NoRuns_Unchanged() {
    byte[] data = [1, 2, 3, 1, 2, 3];
    byte[] encoded = Bzip2Compressor.Rle1Encode(data);
    byte[] decoded = Bzip2Compressor.Rle1Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test]
  public void Rle2_RunARunB_Encode_Decode_RoundTrip() {
    // MTF data with lots of zeros
    byte[] mtfData = new byte[50];
    // First 30 are zeros, then some non-zero values
    mtfData[30] = 5;
    mtfData[31] = 3;
    mtfData[32] = 1;

    var eobSymbol = 10;
    int[] encoded = Bzip2Compressor.Rle2Encode(mtfData, eobSymbol);
    byte[] decoded = Bzip2Compressor.Rle2Decode(encoded.AsSpan(), eobSymbol);

    Assert.That(decoded, Is.EqualTo(mtfData));
  }

  [Test]
  public void Rle2_SingleZero() {
    byte[] mtfData = [0];
    var eobSymbol = 5;
    int[] encoded = Bzip2Compressor.Rle2Encode(mtfData, eobSymbol);
    byte[] decoded = Bzip2Compressor.Rle2Decode(encoded.AsSpan(), eobSymbol);
    Assert.That(decoded, Is.EqualTo(mtfData));
  }

  [Test]
  public void Rle2_MultipleZeroRuns() {
    byte[] mtfData = [0, 0, 0, 5, 0, 0, 3, 0];
    var eobSymbol = 10;
    int[] encoded = Bzip2Compressor.Rle2Encode(mtfData, eobSymbol);
    byte[] decoded = Bzip2Compressor.Rle2Decode(encoded.AsSpan(), eobSymbol);
    Assert.That(decoded, Is.EqualTo(mtfData));
  }
}
