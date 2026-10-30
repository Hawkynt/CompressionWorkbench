using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lzms;

namespace Compression.Tests.CodecProof;

/// <summary>
/// MSZIP and LZMS against Windows' Compression API (cabinet.dll, raw mode), both directions. Raw
/// mode handles one block of up to 32 KB and keeps a block that does not shrink as plain bytes,
/// so the inputs here are compressible blocks.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
[Category("ExternalInterop")]
public class CompressionApiOracleTests {
  private static IEnumerable<TestCaseData> Blocks() {
    yield return new TestCaseData("text-5k", CodecProofData.Text(5_000));
    yield return new TestCaseData("text-32k", CodecProofData.Text(32_768));
    yield return new TestCaseData("zeros-32k", new byte[32_768]);
    yield return new TestCaseData("text-then-zeros", (byte[])[.. CodecProofData.Text(1_000), .. new byte[20_000]]);
  }

  [SetUp]
  public void RequireWindows() => WindowsCompression.Require();

  [TestCaseSource(nameof(Blocks))]
  public void Given_WindowsMsZip_When_WeDecompress_Then_TheBytesMatch(string name, byte[] data)
    => Assert.That(MsZipDecompressor.Decompress(WindowsCompression.CompressionApi.Compress(WindowsCompression.CompressionApi.MsZip, data), data.Length), Is.EqualTo(data), name);

  [TestCaseSource(nameof(Blocks))]
  public void Given_OurMsZip_When_WindowsDecompresses_Then_TheBytesMatch(string name, byte[] data)
    => Assert.That(WindowsCompression.CompressionApi.Decompress(WindowsCompression.CompressionApi.MsZip, MsZipCompressor.Compress(data), data.Length), Is.EqualTo(data), name);

  [TestCaseSource(nameof(Blocks))]
  public void Given_WindowsLzms_When_WeDecompress_Then_TheBytesMatch(string name, byte[] data)
    => Assert.That(new LzmsDecompressor().Decompress(WindowsCompression.CompressionApi.Compress(WindowsCompression.CompressionApi.Lzms, data), data.Length), Is.EqualTo(data), name);

  [TestCaseSource(nameof(Blocks))]
  public void Given_OurLzms_When_WindowsDecompresses_Then_TheBytesMatch(string name, byte[] data)
    => Assert.That(WindowsCompression.CompressionApi.Decompress(WindowsCompression.CompressionApi.Lzms, new LzmsCompressor().Compress(data), data.Length), Is.EqualTo(data), name);
}
