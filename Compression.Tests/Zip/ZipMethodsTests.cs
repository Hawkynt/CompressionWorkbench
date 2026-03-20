using FileFormat.Zip;

namespace Compression.Tests.Zip;

[TestFixture]
public class ZipMethodsTests {
  [Category("HappyPath")]
  [Test]
  public void CompressionMethod_Enum_HasAllValues() {
    Assert.That(Enum.IsDefined(typeof(ZipCompressionMethod), (ushort)0));  // Store
    Assert.That(Enum.IsDefined(typeof(ZipCompressionMethod), (ushort)8));  // Deflate
    Assert.That(Enum.IsDefined(typeof(ZipCompressionMethod), (ushort)9));  // Deflate64
    Assert.That(Enum.IsDefined(typeof(ZipCompressionMethod), (ushort)12)); // BZip2
    Assert.That(Enum.IsDefined(typeof(ZipCompressionMethod), (ushort)14)); // LZMA
    Assert.That(Enum.IsDefined(typeof(ZipCompressionMethod), (ushort)98)); // PPMd
  }
}
