namespace Compression.Tests.MacBinary;

[TestFixture]
public class MacBinaryTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_DataForkOnly() {
    var data = "Hello, MacBinary!"u8.ToArray();
    using var encoded = new MemoryStream();
    FileFormat.MacBinary.MacBinaryWriter.Write(encoded, "Test File", data);

    encoded.Position = 0;
    var header = FileFormat.MacBinary.MacBinaryReader.ReadHeader(encoded);
    Assert.That(header.FileName, Is.EqualTo("Test File"));
    Assert.That(header.DataForkLength, Is.EqualTo(data.Length));
    Assert.That(header.ResourceForkLength, Is.EqualTo(0));

    encoded.Position = 0;
    var extracted = FileFormat.MacBinary.MacBinaryReader.ReadDataFork(encoded);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_WithResourceFork() {
    var data = "data fork content"u8.ToArray();
    var rsrc = "resource fork content"u8.ToArray();

    using var encoded = new MemoryStream();
    FileFormat.MacBinary.MacBinaryWriter.Write(encoded, "MacFile", data, rsrc);

    encoded.Position = 0;
    var header = FileFormat.MacBinary.MacBinaryReader.ReadHeader(encoded);
    Assert.That(header.DataForkLength, Is.EqualTo(data.Length));
    Assert.That(header.ResourceForkLength, Is.EqualTo(rsrc.Length));

    encoded.Position = 0;
    Assert.That(FileFormat.MacBinary.MacBinaryReader.ReadDataFork(encoded), Is.EqualTo(data));

    encoded.Position = 0;
    Assert.That(FileFormat.MacBinary.MacBinaryReader.ReadResourceFork(encoded), Is.EqualTo(rsrc));
  }

  [Test, Category("HappyPath")]
  public void MacBinaryIII_HasSignature() {
    var data = "test"u8.ToArray();
    using var encoded = new MemoryStream();
    FileFormat.MacBinary.MacBinaryWriter.Write(encoded, "test", data, version: 130);

    // Check for "mBIN" signature at offset 102
    encoded.Position = 102;
    Assert.That(encoded.ReadByte(), Is.EqualTo((byte)'m'));
    Assert.That(encoded.ReadByte(), Is.EqualTo((byte)'B'));
    Assert.That(encoded.ReadByte(), Is.EqualTo((byte)'I'));
    Assert.That(encoded.ReadByte(), Is.EqualTo((byte)'N'));
  }

  [Test, Category("HappyPath")]
  public void IsMacBinary_ValidFile_ReturnsTrue() {
    using var encoded = new MemoryStream();
    FileFormat.MacBinary.MacBinaryWriter.Write(encoded, "valid", "test"u8.ToArray());
    encoded.Position = 0;
    Assert.That(FileFormat.MacBinary.MacBinaryReader.IsMacBinary(encoded), Is.True);
  }

  [Test, Category("HappyPath")]
  public void IsMacBinary_InvalidFile_ReturnsFalse() {
    using var ms = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
    Assert.That(FileFormat.MacBinary.MacBinaryReader.IsMacBinary(ms), Is.False);
  }
}
