using Compression.Tests.CodecProof;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// LZNT1 against Windows itself (RtlCompressBuffer / RtlDecompressBufferEx with
/// COMPRESSION_FORMAT_LZNT1, the codec NTFS compresses files with), both directions.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
[Category("ExternalInterop")]
public class Lznt1WindowsOracleTests {
  private static IEnumerable<TestCaseData> Inputs() {
    yield return new TestCaseData("one-byte", new byte[] { 0x41 });
    yield return new TestCaseData("text-5k", CodecProofData.Text(5_000));
    yield return new TestCaseData("text-200k", CodecProofData.Text(200_000));
    yield return new TestCaseData("mixed-150k", CodecProofData.Mixed(150_000));
    yield return new TestCaseData("random-70k", CodecProofData.Random(70_000));
    yield return new TestCaseData("zeros-tail", (byte[])[.. CodecProofData.Text(3_000), .. new byte[70_000]]);
    yield return new TestCaseData("partial-chunk", CodecProofData.Text(4_097));
  }

  [SetUp]
  public void RequireWindows() => WindowsCompression.Require();

  [TestCaseSource(nameof(Inputs))]
  public void Given_WindowsLznt1_When_WeDecompress_Then_TheBytesMatch(string name, byte[] data) {
    foreach (var format in new[] { WindowsCompression.Rtl.Lznt1, (ushort)(WindowsCompression.Rtl.Lznt1 | WindowsCompression.Rtl.EngineMaximum) }) {
      var packed = WindowsCompression.Rtl.Compress(format, data);
      Assert.That(packed, Is.Not.Null, name);
      Assert.That(Lznt1.Decompress(packed!, data.Length), Is.EqualTo(data), $"{name} format 0x{format:X}");
    }
  }

  [TestCaseSource(nameof(Inputs))]
  public void Given_OurLznt1_When_WindowsDecompresses_Then_TheBytesMatch(string name, byte[] data)
    => Assert.That(WindowsCompression.Rtl.Decompress(WindowsCompression.Rtl.Lznt1, Lznt1.Compress(data), data.Length), Is.EqualTo(data), name);

  [Test]
  public void Given_ABackReferenceBeforeTheChunkStart_When_Decompressed_Then_ItThrows() {
    // A compressed chunk whose first item is a match: nothing precedes it to copy from.
    byte[] packed = [0x03, 0xB0, 0x01, 0x00, 0x00, 0x41];
    Assert.That(() => Lznt1.Decompress(packed, 16), Throws.InstanceOf<InvalidDataException>());
  }
}
