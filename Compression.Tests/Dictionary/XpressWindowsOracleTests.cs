using Compression.Core.Dictionary.Xpress;
using Compression.Tests.CodecProof;

namespace Compression.Tests.Dictionary;

/// <summary>
/// The two XPRESS codecs of [MS-XCA] against Windows itself: plain LZ77 (COMPRESSION_FORMAT_XPRESS)
/// and LZ77+Huffman (COMPRESSION_FORMAT_XPRESS_HUFF, the WIM and hibernation codec), via
/// RtlCompressBuffer / RtlDecompressBufferEx, both directions. Inputs above 64 KB cross the
/// LZ77+Huffman block boundary.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
[Category("ExternalInterop")]
public class XpressWindowsOracleTests {
  private static IEnumerable<TestCaseData> Inputs() {
    yield return new TestCaseData("text-5k", CodecProofData.Text(5_000));
    yield return new TestCaseData("text-200k", CodecProofData.Text(200_000));
    yield return new TestCaseData("mixed-150k", CodecProofData.Mixed(150_000));
    yield return new TestCaseData("random-70k", CodecProofData.Random(70_000));
    yield return new TestCaseData("zeros-tail", (byte[])[.. CodecProofData.Text(3_000), .. new byte[100_000]]);
    yield return new TestCaseData("block-edge", CodecProofData.Text(65_536));
  }

  [SetUp]
  public void RequireWindows() => WindowsCompression.Require();

  [TestCaseSource(nameof(Inputs))]
  public void Given_WindowsXpress_When_WeDecompress_Then_TheBytesMatch(string name, byte[] data)
    => Assert.That(XpressDecompressor.Decompress(WindowsCompression.Rtl.Compress(WindowsCompression.Rtl.Xpress, data)!, data.Length), Is.EqualTo(data), name);

  [TestCaseSource(nameof(Inputs))]
  public void Given_OurXpress_When_WindowsDecompresses_Then_TheBytesMatch(string name, byte[] data)
    => Assert.That(WindowsCompression.Rtl.Decompress(WindowsCompression.Rtl.Xpress, new XpressCompressor().Compress(data), data.Length), Is.EqualTo(data), name);

  [TestCaseSource(nameof(Inputs))]
  public void Given_WindowsXpressHuffman_When_WeDecompress_Then_TheBytesMatch(string name, byte[] data)
    => Assert.That(XpressHuffmanDecompressor.Decompress(WindowsCompression.Rtl.Compress(WindowsCompression.Rtl.XpressHuffman, data)!, data.Length), Is.EqualTo(data), name);

  [TestCaseSource(nameof(Inputs))]
  public void Given_OurXpressHuffman_When_WindowsDecompresses_Then_TheBytesMatch(string name, byte[] data)
    => Assert.That(WindowsCompression.Rtl.Decompress(WindowsCompression.Rtl.XpressHuffman, new XpressHuffmanCompressor().Compress(data), data.Length), Is.EqualTo(data), name);
}
