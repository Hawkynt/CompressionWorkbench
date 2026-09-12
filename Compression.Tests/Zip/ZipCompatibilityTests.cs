using System.Buffers.Binary;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

[TestFixture]
public class ZipCompatibilityTests {
  [TestCase(ZipCompressionMethod.Store, 10)]
  [TestCase(ZipCompressionMethod.Shrink, 10)]
  [TestCase(ZipCompressionMethod.Reduce1, 10)]
  [TestCase(ZipCompressionMethod.Implode, 10)]
  [TestCase(ZipCompressionMethod.Deflate, 20)]
  [TestCase(ZipCompressionMethod.Deflate64, 21)]
  [TestCase(ZipCompressionMethod.BZip2, 46)]
  [TestCase(ZipCompressionMethod.Lzma, 63)]
  [TestCase(ZipCompressionMethod.Zstd, 63)]
  [TestCase(ZipCompressionMethod.Ppmd, 63)]
  public void VersionNeeded_UsesMethodMinimum(ZipCompressionMethod method, int expected)
    => Assert.That(ZipCompatibility.GetVersionNeeded(method), Is.EqualTo(expected));

  [Test]
  public void VersionNeeded_UsesMaximumOfCombinedFeatures() {
    Assert.Multiple(() => {
      Assert.That(ZipCompatibility.GetVersionNeeded(ZipCompressionMethod.Store, isDirectory: true), Is.EqualTo(20));
      Assert.That(ZipCompatibility.GetVersionNeeded(ZipCompressionMethod.Store, ZipEncryptionMethod.PkzipTraditional), Is.EqualTo(20));
      Assert.That(ZipCompatibility.GetVersionNeeded(ZipCompressionMethod.Store, ZipEncryptionMethod.Aes256), Is.EqualTo(51));
      Assert.That(ZipCompatibility.GetVersionNeeded(ZipCompressionMethod.Deflate, zip64: true), Is.EqualTo(45));
      Assert.That(ZipCompatibility.GetVersionNeeded(ZipCompressionMethod.Lzma, ZipEncryptionMethod.Aes256), Is.EqualTo(63));
    });
  }

  [Test]
  public void Writer_EmitsMinimumVersionInLocalAndCentralHeaders() {
    using var stream = new MemoryStream();
    using (var writer = new ZipWriter(stream, leaveOpen: true)) {
      writer.AddEntry("plain.txt", new byte[4096], ZipCompressionMethod.Deflate);
      writer.Finish();
    }

    var bytes = stream.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)), Is.EqualTo(20));

    var centralOffset = FindSignature(bytes, 0x02014B50u);
    Assert.That(centralOffset, Is.GreaterThanOrEqualTo(0));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(centralOffset + 6, 2)), Is.EqualTo(20));
  }

  [Test]
  public void StoreEntry_UsesZip10_ButDirectoryUsesZip20() {
    using var fileStream = new MemoryStream();
    using (var writer = new ZipWriter(fileStream, leaveOpen: true)) {
      writer.AddEntry("plain.bin", [1, 2, 3], ZipCompressionMethod.Store);
      writer.Finish();
    }

    using var directoryStream = new MemoryStream();
    using (var writer = new ZipWriter(directoryStream, leaveOpen: true)) {
      writer.AddDirectory("folder/");
      writer.Finish();
    }

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(fileStream.ToArray().AsSpan(4, 2)), Is.EqualTo(10));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(directoryStream.ToArray().AsSpan(4, 2)), Is.EqualTo(20));
    });
  }

  [Test]
  public void Profile_RejectsFeatureAboveCeiling() {
    using var stream = new MemoryStream();
    using var writer = new ZipWriter(stream, leaveOpen: true, compatibilityProfile: ZipCompatibilityProfile.Zip10);

    Assert.That(
      () => writer.AddEntry("deflated.bin", new byte[4096], ZipCompressionMethod.Deflate),
      Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void Profile_AllowsCompressionFallbackThatNoLongerNeedsNewerFeature() {
    using var stream = new MemoryStream();
    using var writer = new ZipWriter(stream, leaveOpen: true, compatibilityProfile: ZipCompatibilityProfile.Zip10);

    writer.AddEntry("tiny.bin", [0x42], ZipCompressionMethod.Deflate);
    writer.Finish();

    var bytes = stream.ToArray();
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)), Is.EqualTo(10));
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2)), Is.EqualTo((ushort)ZipCompressionMethod.Store));
    });
  }

  private static int FindSignature(byte[] data, uint signature) {
    for (var i = 0; i <= data.Length - sizeof(uint); ++i)
      if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, sizeof(uint))) == signature)
        return i;
    return -1;
  }
}
