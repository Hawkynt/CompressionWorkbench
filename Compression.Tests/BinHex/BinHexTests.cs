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
}
