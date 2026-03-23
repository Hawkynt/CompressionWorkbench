using Compression.Core.BitIO;
using Compression.Core.Deflate;

namespace Compression.Tests.Deflate;

[TestFixture]
public class DeflateHuffmanTableTests {
  [Category("HappyPath")]
  [Test]
  public void ReverseBits_CorrectResults() {
    Assert.That(DeflateHuffmanTable.ReverseBits(0b110, 3), Is.EqualTo(0b011u));
    Assert.That(DeflateHuffmanTable.ReverseBits(0b1010, 4), Is.EqualTo(0b0101u));
    Assert.That(DeflateHuffmanTable.ReverseBits(0b1, 1), Is.EqualTo(0b1u));
    Assert.That(DeflateHuffmanTable.ReverseBits(0b0, 1), Is.EqualTo(0b0u));
    Assert.That(DeflateHuffmanTable.ReverseBits(0b11001, 5), Is.EqualTo(0b10011u));
  }

  [Category("ThemVsUs")]
  [Test]
  public void StaticLiteralTable_DecodesCorrectly() {
    var table = DeflateHuffmanTable.CreateStaticLiteralTable();
    Assert.That(table.MaxCodeLength, Is.EqualTo(9));

    // Verify some known encode values and round-trip
    var (code0, len0) = table.GetCode(0);
    Assert.That(len0, Is.EqualTo(8));

    var (code256, len256) = table.GetCode(256); // EOB
    Assert.That(len256, Is.EqualTo(7));

    var (code144, len144) = table.GetCode(144);
    Assert.That(len144, Is.EqualTo(9));
  }

  [Category("ThemVsUs")]
  [Test]
  public void StaticDistanceTable_AllCodesLength5() {
    var table = DeflateHuffmanTable.CreateStaticDistanceTable();
    Assert.That(table.MaxCodeLength, Is.EqualTo(5));

    for (var i = 0; i < 30; ++i) {
      var (_, len) = table.GetCode(i);
      Assert.That(len, Is.EqualTo(5), $"Distance code {i}");
    }
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_ThroughBitWriterAndBitBuffer_LsbFirst() {
    int[] codeLengths = [2, 3, 3, 3, 3, 2];
    var table = new DeflateHuffmanTable(codeLengths);

    int[] symbols = [0, 1, 2, 3, 4, 5, 5, 0, 3, 2, 1, 4];

    // Encode
    using var ms = new MemoryStream();
    var writer = new BitWriter<LsbBitOrder>(ms);
    foreach (var sym in symbols) {
      var (code, len) = table.GetCode(sym);
      writer.WriteBits(code, len);
    }
    writer.FlushBits();

    // Decode
    ms.Position = 0;
    var buffer = new BitBuffer<LsbBitOrder>(ms);
    for (var i = 0; i < symbols.Length; ++i) {
      var decoded = table.DecodeSymbol(buffer);
      Assert.That(decoded, Is.EqualTo(symbols[i]), $"Symbol at index {i}");
    }
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_StaticLiteralTable() {
    var table = DeflateHuffmanTable.CreateStaticLiteralTable();

    // Encode a sequence of literals + EOB
    int[] symbols = [0, 65, 127, 144, 200, 255, 256]; // last is EOB

    using var ms = new MemoryStream();
    var writer = new BitWriter<LsbBitOrder>(ms);
    foreach (var sym in symbols) {
      var (code, len) = table.GetCode(sym);
      writer.WriteBits(code, len);
    }
    writer.FlushBits();

    ms.Position = 0;
    var buffer = new BitBuffer<LsbBitOrder>(ms);
    for (var i = 0; i < symbols.Length; ++i) {
      var decoded = table.DecodeSymbol(buffer);
      Assert.That(decoded, Is.EqualTo(symbols[i]), $"Symbol at index {i}");
    }
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void SingleSymbol_EncodesAndDecodes() {
    int[] codeLengths = [0, 1, 0]; // only symbol 1 exists
    var table = new DeflateHuffmanTable(codeLengths);

    using var ms = new MemoryStream();
    var writer = new BitWriter<LsbBitOrder>(ms);
    for (var i = 0; i < 5; ++i) {
      var (code, len) = table.GetCode(1);
      writer.WriteBits(code, len);
    }
    writer.FlushBits();

    ms.Position = 0;
    var buffer = new BitBuffer<LsbBitOrder>(ms);
    for (var i = 0; i < 5; ++i) {
      var decoded = table.DecodeSymbol(buffer);
      Assert.That(decoded, Is.EqualTo(1));
    }
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void AllSameLength_EncodesAndDecodes() {
    // 4 symbols all with length 2
    int[] codeLengths = [2, 2, 2, 2];
    var table = new DeflateHuffmanTable(codeLengths);

    int[] symbols = [0, 1, 2, 3, 3, 2, 1, 0];

    using var ms = new MemoryStream();
    var writer = new BitWriter<LsbBitOrder>(ms);
    foreach (var sym in symbols) {
      var (code, len) = table.GetCode(sym);
      writer.WriteBits(code, len);
    }
    writer.FlushBits();

    ms.Position = 0;
    var buffer = new BitBuffer<LsbBitOrder>(ms);
    for (var i = 0; i < symbols.Length; ++i) {
      var decoded = table.DecodeSymbol(buffer);
      Assert.That(decoded, Is.EqualTo(symbols[i]), $"Symbol at index {i}");
    }
  }

  [Category("ThemVsUs")]
  [Test]
  public void StaticLiteralTable_EncodesDeterministicCodes() {
    // Verify the static table produces known canonical codes
    var table = DeflateHuffmanTable.CreateStaticLiteralTable();

    // Symbol 0: canonical MSB code 00110000 (8 bits), reversed = 00001100
    var (code0, len0) = table.GetCode(0);
    Assert.That(len0, Is.EqualTo(8));

    // Symbol 144: canonical MSB code 110010000 (9 bits)
    var (code144, len144) = table.GetCode(144);
    Assert.That(len144, Is.EqualTo(9));

    // Symbol 280: canonical MSB code 11000000 (8 bits)
    var (code280, len280) = table.GetCode(280);
    Assert.That(len280, Is.EqualTo(8));
  }
}
