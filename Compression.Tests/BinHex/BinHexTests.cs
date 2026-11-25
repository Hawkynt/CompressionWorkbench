namespace Compression.Tests.BinHex;

[TestFixture]
public class BinHexTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleFile() {
    var data = "Hello, BinHex!"u8.ToArray();
    using var encoded = new MemoryStream();
    FileFormat.BinHex.BinHexWriter.Write(encoded, "test.txt", data);

    encoded.Position = 0;
    var result = FileFormat.BinHex.BinHexReader.Decode(encoded);
    Assert.That(result.FileName, Is.EqualTo("test.txt"));
    Assert.That(result.DataFork, Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_WithResourceFork() {
    var data = "data fork"u8.ToArray();
    var rsrc = "resource fork"u8.ToArray();

    using var encoded = new MemoryStream();
    FileFormat.BinHex.BinHexWriter.Write(encoded, "macfile", data, rsrc);

    encoded.Position = 0;
    var result = FileFormat.BinHex.BinHexReader.Decode(encoded);
    Assert.That(result.DataFork, Is.EqualTo(data));
    Assert.That(result.ResourceFork, Is.EqualTo(rsrc));
  }

  [Test, Category("HappyPath")]
  public void Header_ContainsBinHexMarker() {
    using var encoded = new MemoryStream();
    FileFormat.BinHex.BinHexWriter.Write(encoded, "test", "hi"u8.ToArray());

    encoded.Position = 0;
    using var reader = new StreamReader(encoded);
    var firstLine = reader.ReadLine();
    Assert.That(firstLine, Does.Contain("BinHex"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_DataWithEscapeByte() {
    // Data containing 0x90 (the RLE escape byte) should round-trip correctly
    var data = new byte[] { 0x90, 0x90, 0x00, 0x90, 0x41, 0x41, 0x41, 0x90 };
    using var encoded = new MemoryStream();
    FileFormat.BinHex.BinHexWriter.Write(encoded, "escape", data);

    encoded.Position = 0;
    var result = FileFormat.BinHex.BinHexReader.Decode(encoded);
    Assert.That(result.DataFork, Is.EqualTo(data));
  }

  // ── fork lengths are unsigned 32-bit ──────────────────────────────────

  /// <summary>
  /// A BinHex file whose header declares the given fork lengths (with a valid header CRC) and
  /// carries <paramref name="actualData"/> bytes of data fork followed by an empty resource fork.
  /// </summary>
  private static MemoryStream Hqx(uint dataLength, uint resourceLength, byte[] actualData) {
    var raw = new List<byte> { 4 };
    raw.AddRange("name"u8.ToArray());
    raw.Add(0);
    raw.AddRange("TEXTttxt"u8.ToArray());
    raw.AddRange([0, 0]);
    raw.AddRange(BigEndian(dataLength));
    raw.AddRange(BigEndian(resourceLength));
    var headerCrc = FileFormat.BinHex.BinHexReader.ComputeCrc(raw.ToArray());
    raw.AddRange([(byte)(headerCrc >> 8), (byte)headerCrc]);
    raw.AddRange(actualData);
    var dataCrc = FileFormat.BinHex.BinHexReader.ComputeCrc(actualData);
    raw.AddRange([(byte)(dataCrc >> 8), (byte)dataCrc, 0, 0]);

    // RLE: the marker byte 0x90 stands for itself as 0x90 0x00.
    var rle = new List<byte>();
    foreach (var b in raw) {
      rle.Add(b);
      if (b == 0x90) rle.Add(0);
    }
    while (rle.Count % 3 != 0) rle.Add(0);

    var text = new System.Text.StringBuilder("(This file must be converted with BinHex 4.0)\n:");
    const string alphabet = "!\"#$%&'()*+,-012345689@ABCDEFGHIJKLMNPQRSTUVXYZ[`abcdefhijklmpqr";
    for (var i = 0; i < rle.Count; i += 3) {
      var bits = (rle[i] << 16) | (rle[i + 1] << 8) | rle[i + 2];
      for (var shift = 18; shift >= 0; shift -= 6)
        text.Append(alphabet[(bits >> shift) & 0x3F]);
    }
    text.Append(":\n");
    return new MemoryStream(System.Text.Encoding.ASCII.GetBytes(text.ToString()));
  }

  private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

  [Test, Category("Boundary")]
  public void Given_ForkLengthsExactlyFittingTheData_When_Decoded_Then_ForksAreReturned() {
    var result = FileFormat.BinHex.BinHexReader.Decode(Hqx(3, 0, [1, 2, 3]));
    Assert.That(result.DataFork, Is.EqualTo(new byte[] { 1, 2, 3 }));
    Assert.That(result.ResourceFork, Is.Empty);
  }

  [Test, Category("Exception")]
  [TestCase(4u)]
  [TestCase(0x7FFFFFFFu)]
  [TestCase(0x80000000u)]
  [TestCase(0xFFFFFFFFu)]
  public void Given_DataForkLengthBeyondTheData_When_Decoded_Then_InvalidData(uint length)
    => Assert.Throws<InvalidDataException>(() => FileFormat.BinHex.BinHexReader.Decode(Hqx(length, 0, [1, 2, 3])));

  [Test, Category("Exception")]
  [TestCase(0x100u)]
  [TestCase(0x7FFFFFFFu)]
  [TestCase(0x80000000u)]
  [TestCase(0xFFFFFFFFu)]
  public void Given_ResourceForkLengthBeyondTheData_When_Decoded_Then_InvalidData(uint length)
    => Assert.Throws<InvalidDataException>(() => FileFormat.BinHex.BinHexReader.Decode(Hqx(3, length, [1, 2, 3])));
}
