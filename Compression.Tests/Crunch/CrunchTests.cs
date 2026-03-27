using FileFormat.Crunch;
using Compression.Core.Streams;

namespace Compression.Tests.Crunch;

[TestFixture]
public class CrunchTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Compress_Decompress_RoundTrips() {
    var original = "Hello, CP/M Crunch world!"u8.ToArray();

    using var compressed = new MemoryStream();
    using (var cs = new CrunchStream(compressed, CompressionStreamMode.Compress, "HELLO.TXT", leaveOpen: true))
      cs.Write(original);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    using (var ds = new CrunchStream(compressed, CompressionStreamMode.Decompress, leaveOpen: true))
      ds.CopyTo(decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void Compress_WritesCorrectMagic() {
    using var compressed = new MemoryStream();
    using (var cs = new CrunchStream(compressed, CompressionStreamMode.Compress, "TEST.TXT", leaveOpen: true))
      cs.Write([0x41]);

    var data = compressed.ToArray();
    Assert.That(data[0], Is.EqualTo(0x76));
    Assert.That(data[1], Is.EqualTo(0xFE));
  }

  [Test, Category("HappyPath")]
  public void Decompress_ReadsOriginalName() {
    using var compressed = new MemoryStream();
    using (var cs = new CrunchStream(compressed, CompressionStreamMode.Compress, "MYFILE.DOC", leaveOpen: true))
      cs.Write("data"u8);

    compressed.Position = 0;
    using var ds = new CrunchStream(compressed, CompressionStreamMode.Decompress, leaveOpen: true);
    ds.CopyTo(Stream.Null);
    Assert.That(ds.OriginalName, Is.EqualTo("MYFILE.DOC"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_BinaryData() {
    var original = new byte[1024];
    for (var i = 0; i < original.Length; i++)
      original[i] = (byte)(i & 0xFF);

    using var compressed = new MemoryStream();
    using (var cs = new CrunchStream(compressed, CompressionStreamMode.Compress, leaveOpen: true))
      cs.Write(original);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    using (var ds = new CrunchStream(compressed, CompressionStreamMode.Decompress, leaveOpen: true))
      ds.CopyTo(decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    // Highly compressible data
    var original = new byte[2048];
    Array.Fill(original, (byte)'A');

    using var compressed = new MemoryStream();
    using (var cs = new CrunchStream(compressed, CompressionStreamMode.Compress, leaveOpen: true))
      cs.Write(original);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    using (var ds = new CrunchStream(compressed, CompressionStreamMode.Decompress, leaveOpen: true))
      ds.CopyTo(decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_SingleByte() {
    byte[] original = [0x42];

    using var compressed = new MemoryStream();
    using (var cs = new CrunchStream(compressed, CompressionStreamMode.Compress, leaveOpen: true))
      cs.Write(original);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    using (var ds = new CrunchStream(compressed, CompressionStreamMode.Decompress, leaveOpen: true))
      ds.CopyTo(decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("ErrorHandling")]
  public void Decompress_BadMagic_Throws() {
    byte[] data = [0x00, 0x00, 0x00, 0x00, 0x00];
    using var ms = new MemoryStream(data);
    using var ds = new CrunchStream(ms, CompressionStreamMode.Decompress);
    Assert.Throws<InvalidDataException>(() => ds.CopyTo(Stream.Null));
  }

  [Test, Category("ErrorHandling")]
  public void Decompress_TruncatedHeader_Throws() {
    byte[] data = [0x76];
    using var ms = new MemoryStream(data);
    using var ds = new CrunchStream(ms, CompressionStreamMode.Decompress);
    Assert.Throws<InvalidDataException>(() => ds.CopyTo(Stream.Null));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new CrunchFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Crunch"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x76, 0xFE }));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Stream));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Decompress() {
    var original = "Descriptor test"u8.ToArray();

    using var compressed = new MemoryStream();
    using (var cs = new CrunchStream(compressed, CompressionStreamMode.Compress, leaveOpen: true))
      cs.Write(original);

    compressed.Position = 0;
    using var output = new MemoryStream();
    new CrunchFormatDescriptor().Decompress(compressed, output);
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void Detect_ByMagic() {
    using var compressed = new MemoryStream();
    using (var cs = new CrunchStream(compressed, CompressionStreamMode.Compress, "TEST", leaveOpen: true))
      cs.Write("test"u8);

    var format = Compression.Lib.FormatDetector.DetectByMagic(compressed.ToArray());
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Crunch));
  }
}
