using System.Text;
using Compression.Core.BitIO;
using Compression.Core.Dictionary.Lzw;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class LzwTests {
  private static byte[] RoundTrip(
    byte[] data,
    LzwCompressionLevel level,
    int minBits = 9,
    int maxBits = 12,
    bool useClearCode = true,
    bool useStopCode = true,
    BitOrder bitOrder = BitOrder.LsbFirst) {
    using var ms = new MemoryStream();
    var encoder = new LzwEncoder(ms, minBits, maxBits, useClearCode, useStopCode, bitOrder, level);
    encoder.Encode(data);

    ms.Position = 0;
    var decoder = new LzwDecoder(ms, minBits, maxBits, useClearCode, useStopCode, bitOrder);
    return decoder.Decode(data.Length);
  }

  // ---- Round-trip tests for Uncompressed ----

  [Test]
  public void RoundTrip_Uncompressed_EmptyData() {
    byte[] data = [];
    var result = RoundTrip(data, LzwCompressionLevel.Uncompressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_Uncompressed_SingleByte() {
    byte[] data = [0x42];
    var result = RoundTrip(data, LzwCompressionLevel.Uncompressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_Uncompressed_TextData() {
    byte[] data = Encoding.ASCII.GetBytes("Hello, World! Hello, World!");
    var result = RoundTrip(data, LzwCompressionLevel.Uncompressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_Uncompressed_RepetitiveData() {
    var sb = new StringBuilder();
    for (int i = 0; i < 50; i++)
      sb.Append("ABCABCABCABC");

    byte[] data = Encoding.ASCII.GetBytes(sb.ToString());
    var result = RoundTrip(data, LzwCompressionLevel.Uncompressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_Uncompressed_RandomData() {
    var rng = new Random(42);
    byte[] data = new byte[1024];
    rng.NextBytes(data);

    var result = RoundTrip(data, LzwCompressionLevel.Uncompressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_Uncompressed_HighlyRepetitive() {
    byte[] data = new byte[4096];
    // All zeros.
    var result = RoundTrip(data, LzwCompressionLevel.Uncompressed);
    Assert.That(result, Is.EqualTo(data));
  }

  // ---- Round-trip tests for FirstMatch ----

  [Test]
  public void RoundTrip_FirstMatch_EmptyData() {
    byte[] data = [];
    var result = RoundTrip(data, LzwCompressionLevel.FirstMatch);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_FirstMatch_SingleByte() {
    byte[] data = [0x42];
    var result = RoundTrip(data, LzwCompressionLevel.FirstMatch);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_FirstMatch_TextData() {
    byte[] data = Encoding.ASCII.GetBytes("Hello, World! Hello, World!");
    var result = RoundTrip(data, LzwCompressionLevel.FirstMatch);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_FirstMatch_RepetitiveData() {
    var sb = new StringBuilder();
    for (int i = 0; i < 50; i++)
      sb.Append("ABCABCABCABC");

    byte[] data = Encoding.ASCII.GetBytes(sb.ToString());
    var result = RoundTrip(data, LzwCompressionLevel.FirstMatch);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_FirstMatch_RandomData() {
    var rng = new Random(42);
    byte[] data = new byte[1024];
    rng.NextBytes(data);

    var result = RoundTrip(data, LzwCompressionLevel.FirstMatch);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_FirstMatch_HighlyRepetitive() {
    byte[] data = new byte[4096];
    // All zeros.
    var result = RoundTrip(data, LzwCompressionLevel.FirstMatch);
    Assert.That(result, Is.EqualTo(data));
  }

  // ---- Feature tests ----

  [Test]
  public void Uncompressed_EmitsOnlyByteCodes() {
    byte[] data = Encoding.ASCII.GetBytes("Hello, World!");

    using var ms = new MemoryStream();
    var encoder = new LzwEncoder(ms, level: LzwCompressionLevel.Uncompressed);
    encoder.Encode(data);

    // With minBits=9, each byte code takes 9 bits. Plus clear code (9 bits) and stop code (9 bits).
    // Total bits = (data.Length + 2) * 9.
    // Compressed size in bytes = ceil(totalBits / 8).
    int totalBits = (data.Length + 2) * 9; // +2 for clear + stop
    int expectedSize = (totalBits + 7) / 8;
    Assert.That(ms.Length, Is.EqualTo(expectedSize));
  }

  [Test]
  public void FirstMatch_ProducesFewerCodes() {
    var sb = new StringBuilder();
    for (int i = 0; i < 50; i++)
      sb.Append("ABCABCABCABC");

    byte[] data = Encoding.ASCII.GetBytes(sb.ToString());

    using var msUncompressed = new MemoryStream();
    var encoderUncompressed = new LzwEncoder(msUncompressed, level: LzwCompressionLevel.Uncompressed);
    encoderUncompressed.Encode(data);

    using var msFirstMatch = new MemoryStream();
    var encoderFirstMatch = new LzwEncoder(msFirstMatch, level: LzwCompressionLevel.FirstMatch);
    encoderFirstMatch.Encode(data);

    Assert.That(msFirstMatch.Length, Is.LessThan(msUncompressed.Length),
      "FirstMatch should produce smaller output than Uncompressed for repetitive data.");
  }

  [Test]
  public void ClearCode_Value() {
    var encoder = new LzwEncoder(Stream.Null);
    Assert.That(encoder.ClearCode, Is.EqualTo(256)); // 2^(9-1) = 256

    var encoder10 = new LzwEncoder(Stream.Null, minBits: 10);
    Assert.That(encoder10.ClearCode, Is.EqualTo(512)); // 2^(10-1) = 512
  }

  [Test]
  public void StopCode_Value() {
    var encoder = new LzwEncoder(Stream.Null);
    Assert.That(encoder.StopCode, Is.EqualTo(257)); // 256 + 1

    var encoderNoStop = new LzwEncoder(Stream.Null, useStopCode: false);
    Assert.That(encoderNoStop.StopCode, Is.EqualTo(-1));
  }

  [Test]
  public void CustomBitWidths() {
    byte[] data = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog. "
      + "The quick brown fox jumps over the lazy dog.");

    var result = RoundTrip(data, LzwCompressionLevel.FirstMatch, minBits: 10, maxBits: 14);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void NoClearCode_Works() {
    byte[] data = Encoding.ASCII.GetBytes("Hello, World! Hello, World!");
    var result = RoundTrip(data, LzwCompressionLevel.FirstMatch, useClearCode: false);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void NoStopCode_Works() {
    byte[] data = Encoding.ASCII.GetBytes("Hello, World! Hello, World!");
    var result = RoundTrip(data, LzwCompressionLevel.FirstMatch, useStopCode: false);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void MsbBitOrder_RoundTrips() {
    byte[] data = Encoding.ASCII.GetBytes("Hello, World! Hello, World!");
    var result = RoundTrip(data, LzwCompressionLevel.FirstMatch, bitOrder: BitOrder.MsbFirst);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void DictionaryReset_MaxBits9() {
    // maxBits=9 means the dictionary fills up very quickly (only 512 - 258 = 254 entries),
    // forcing many dictionary resets.
    byte[] data = Encoding.ASCII.GetBytes(
      "The quick brown fox jumps over the lazy dog. " +
      "Pack my box with five dozen liquor jugs. " +
      "How vexingly quick daft zebras jump! " +
      "The five boxing wizards jump quickly. " +
      "Jackdaws love my big sphinx of quartz.");

    var result = RoundTrip(data, LzwCompressionLevel.FirstMatch, minBits: 9, maxBits: 9);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decode_InvalidCode_Throws() {
    // Craft a stream with an invalid code.
    // Write a clear code, then a valid byte code, then a code that is way beyond nextCode.
    using var ms = new MemoryStream();
    var writer = new BitWriter(ms, BitOrder.LsbFirst);

    // Clear code = 256 at 9 bits.
    writer.WriteBits(256, 9); // clear code
    writer.WriteBits(65, 9);  // 'A' (valid)
    writer.WriteBits(999, 9); // invalid code (well beyond nextCode)
    writer.FlushBits();

    ms.Position = 0;
    var decoder = new LzwDecoder(ms, minBits: 9, maxBits: 12);

    Assert.That(
      () => decoder.Decode(),
      Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void LargeData_RoundTrip() {
    // 10KB+ of mixed data: some repetitive patterns interspersed with random bytes.
    var data = new byte[12000];
    var rng = new Random(99);

    // Fill with a mix of patterns and random data.
    var pos = 0;
    while (pos < data.Length) {
      if (rng.Next(3) == 0 && pos + 100 <= data.Length) {
        // Write a repetitive pattern.
        byte[] pattern = Encoding.ASCII.GetBytes("ABCDEFGH");
        for (int j = 0; j < 100 && pos < data.Length; j++)
          data[pos++] = pattern[j % pattern.Length];
      }
      else {
        // Write a random byte.
        data[pos++] = (byte)rng.Next(256);
      }
    }

    var result = RoundTrip(data, LzwCompressionLevel.FirstMatch);
    Assert.That(result, Is.EqualTo(data));
  }

  // ---- Round-trip tests for Optimal ----

  [Test]
  public void RoundTrip_Optimal_EmptyData() {
    byte[] data = [];
    var result = RoundTrip(data, LzwCompressionLevel.Optimal);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_Optimal_SingleByte() {
    byte[] data = [0x42];
    var result = RoundTrip(data, LzwCompressionLevel.Optimal);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_Optimal_TextData() {
    byte[] data = Encoding.ASCII.GetBytes("Hello, World! Hello, World!");
    var result = RoundTrip(data, LzwCompressionLevel.Optimal);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_Optimal_RepetitiveData() {
    var sb = new StringBuilder();
    for (int i = 0; i < 50; i++)
      sb.Append("ABCABCABCABC");

    byte[] data = Encoding.ASCII.GetBytes(sb.ToString());
    var result = RoundTrip(data, LzwCompressionLevel.Optimal);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_Optimal_RandomData() {
    var rng = new Random(42);
    byte[] data = new byte[1024];
    rng.NextBytes(data);

    var result = RoundTrip(data, LzwCompressionLevel.Optimal);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_Optimal_HighlyRepetitive() {
    byte[] data = new byte[4096];
    // All zeros.
    var result = RoundTrip(data, LzwCompressionLevel.Optimal);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Optimal_NoWorseThanFirstMatch() {
    // Optimal should produce output no larger than FirstMatch on various inputs.
    var inputs = new[] {
      Encoding.ASCII.GetBytes("Hello, World! Hello, World!"),
      Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ABCABCABCABC", 50))),
      new byte[4096], // all zeros
    };

    foreach (byte[] data in inputs) {
      using var msFirstMatch = new MemoryStream();
      new LzwEncoder(msFirstMatch, level: LzwCompressionLevel.FirstMatch).Encode(data);

      using var msOptimal = new MemoryStream();
      new LzwEncoder(msOptimal, level: LzwCompressionLevel.Optimal).Encode(data);

      Assert.That(msOptimal.Length, Is.LessThanOrEqualTo(msFirstMatch.Length),
        $"Optimal should be no larger than FirstMatch for data of length {data.Length}.");
    }
  }

  [Test]
  public void Optimal_LargeData_RoundTrip() {
    // Mixed patterns and random data.
    var data = new byte[8000];
    var rng = new Random(77);
    var pos = 0;
    while (pos < data.Length) {
      if (rng.Next(3) == 0 && pos + 80 <= data.Length) {
        byte[] pattern = Encoding.ASCII.GetBytes("XYZXYZ");
        for (int j = 0; j < 80 && pos < data.Length; j++)
          data[pos++] = pattern[j % pattern.Length];
      }
      else
        data[pos++] = (byte)rng.Next(256);
    }

    var result = RoundTrip(data, LzwCompressionLevel.Optimal);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Optimal_DictionaryReset_MaxBits9() {
    byte[] data = Encoding.ASCII.GetBytes(
      "The quick brown fox jumps over the lazy dog. " +
      "Pack my box with five dozen liquor jugs. " +
      "How vexingly quick daft zebras jump! " +
      "The five boxing wizards jump quickly. " +
      "Jackdaws love my big sphinx of quartz.");

    var result = RoundTrip(data, LzwCompressionLevel.Optimal, minBits: 9, maxBits: 9);
    Assert.That(result, Is.EqualTo(data));
  }
}
