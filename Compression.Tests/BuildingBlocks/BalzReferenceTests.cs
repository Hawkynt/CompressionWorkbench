using Compression.Core.Dictionary.Balz;
using Compression.Tests.CodecProof;
using FileFormat.Balz;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// Third-party proof for <c>BB_Balz</c> and the <c>Balz</c> stream format. The files below were
/// written by Ilya Muravyov's BALZ 1.20 (<c>balz120_win64.zip</c> from
/// http://sourceforge.net/projects/balz/, the author's build) with <c>c</c> and <c>cx</c>; the
/// public-domain <c>balz.cpp</c> from <c>balz120src.zip</c>, built with g++ 16.2, writes the same
/// bytes. Our encoder reproduces both modes byte for byte, our decoder restores them, and
/// <c>balz d</c> restored every file we wrote for the same inputs, executables included.
/// </summary>
[TestFixture]
public sealed class BalzReferenceTests {

  private const string BalzNormal =
    "ugAEAAAAAAAAzV8/Zsvp5BSf2j9bN4WTvdCST2YETvJkvZ9O4dTkkA3jvIYKr0MXYS54L+NtgQiXhG74lZJg7XSy1e0+X33Hqtuq43E8XA9P5t" +
    "qRMsCV9f4zSCiNh3fGUJur4LRr9MP1+/B7vywfq2pINKRnuwmy4LRXCcS/8Bq/rWQwtyxM8ChGXoXMfg62980Cjd2jo3YCH/wQjw/KJfrojPyG" +
    "PJC/2v5yIysQLpjSO6ysxrSiV4mkkFiyLbZEE0jKoLZTgCH6afj3nzZLqMAGRF2f1HfaCYp8B0en1/eysw19VaU0blQkh/Whg2UvCbREvox+uV" +
    "FA8Xb5mYuHYCufGz2TU2khc473vmw4FAeYybvB2KE6bV3GkKbZ8kWe/lwy2trP6krnOOnz2tEPhlRpsaWCDPPo4UpOuK6C+bHrEHxVLQvOxRU5" +
    "NPP6d9frdqRu3IM+X26CuSKmIq9xDFH+HsxizaoRdfdThg5M9k7qknIt6EwBHoODrcGM14720+AaOwGcJlkp52RUJXsqF0NsOE2ARjwi/UeON8" +
    "QxyEVXE2df9849rXDFsENR3lzavpBXQBNF3zZYoLMEZsujuTiRCVJPZyHucP8Ydx2a+2aK1hRvtWa0MjfEEn8AqA==";

  private const string BalzMaximum =
    "ugAEAAAAAAAAzV8/Zsvp5BSf2j9bN4WTvdCST2YETvJkvZ9O4dTkkA3jvIYKr0MXYS54L+NtgQiXhG74lZJg7XSy1e0+X33Hqtuq43E8XA9P5t" +
    "qRMsCV9f4zSCiNh3fGUJur4LRr9MP1+/B7vywfq2pINKRnuwmy4LRXCcS/8Bq/rWQwtyxM8IrI0BW4id5MeHQu4xODADUHx8rtaXZv3QaPTDun" +
    "ybyD6oOH81PB49n741Cp5jKZYdf4XrdOvrTrWgzBqzIRi7L43jo8H6v0QRhcxiYgS6pMrpXcI+HgZlflHoQ+WNG0Ce7mFvBmMly49UNNDt2xa/" +
    "NMrGp37GwRURmcbywwXXtjH7wbSaNU6irUD0+Pu1PwaKaxdxQY+R/zls3EFzY4wlICogJtpQiz6CT7e00uQX0nxniJcpnDNRUPCDZOQhjBVyNI" +
    "jJ1A9tj0CdDEzbkaSWpXfi5QshwqktpJZ6x1y4N6HkgSzYEDbWB5vc0rtR0k9z6+JP0Fbj82N51dBC+HtQ7poDMcOsA1HcpPGPdmabbLxA5Sz2" +
    "uD1yTHqwy1Xf7VIsL47fnfaKju7q7xW+S/RiijaTVv9ZqXRzk/XyvMURouFSznLnYuuqDnRMZcH5e35l2fWeMA";

  // Built by Executable() below; exercises the E8/E9 filter.
  private const string BalzExecutable =
    "uooFAAAAAAAA2WZKcoHwleD6b0ZMwZn4LNz+z+ROKgIBufHONQeLa9hqnUSMaMX9ZMpl3s/Cyxi29TmknKU0JpYYZiUwBxGdASNPf+Ajql1enQ" +
    "FsHzzauwVBKyZzj0BN/602yNexdDguaGL3CsIpfCBECpXK6DpFYpPzze5Jk0IWX71wNm9/HUzLlAFlFmZ6hmodz1fOHPDqtT+lxAC6GQx6dBTF" +
    "hsSDsUuNnbT4JWA70vT1UX3jnmabOD23woH7kxXl4n+aXJJaG/TQw8d3Is7sq4G2dATw3QExY3gQU0RuUmXN/Ywfe0zS3LeEe7yrVXXX4PLavF" +
    "Cdsdqratcua12o142pQL/zq8ahABU8Wuyu3fAAR6a2qsBkDQNNKN8vcmO8FPYxbvi+2lSmdsqx9XSUvT+8EIBb67IZn0MEA5dKux4jCpL+1Nxu" +
    "Yy6Q90/39hjUFlSM/mICexNp5KrL7QmnDA1J/KzmKx8MVQIGU2NILj1To/1pHzb1bprqU7fudOhaD6svgfcyNWahPUYBjgbT72FwadVbwlqm5P" +
    "2d4M/KLLWaOFp3/zB+XpXKHjRDhKjHkbLaPi/KFc5FjgX/4FZT2GOzApeMR7VePFsQG3mgceDBBTsJ9T+EKQcvB6L76wpjPdYwKJB5ZvpGEkfd" +
    "u16zvcGkpCBkQveVMVer5fjRBd9t7j9Gev66GXqXIIzwNcKDFDD2Sxm7vtu58CJMBwDPVa2us7KpYwdxiYuI0/mskE1AOKUuLZwZq70BtD4EAq" +
    "MX+ZXgC2R1fhQsy1gwTCz5XoHqc8tEaYf8wEH5hfFuBTY30gZSq+6vBXpzatiI5pBEdgCL8wA=";

  [Test, Category("HappyPath")]
  public void GivenTheSampleText_WhenCompressed_ThenItIsWhatBalzCWrites()
    => Assert.That(BalzCodec.Compress(CodecProofData.Text(1024)), Is.EqualTo(Convert.FromBase64String(BalzNormal)));

  [Test, Category("HappyPath")]
  public void GivenTheSampleText_WhenCompressedInMaximumMode_ThenItIsWhatBalzCxWrites()
    => Assert.That(BalzCodec.Compress(CodecProofData.Text(1024), maximum: true), Is.EqualTo(Convert.FromBase64String(BalzMaximum)));

  [Test, Category("HappyPath")]
  public void GivenAnExecutable_WhenCompressed_ThenItIsWhatBalzCWrites()
    => Assert.That(BalzCodec.Compress(Executable()), Is.EqualTo(Convert.FromBase64String(BalzExecutable)));

  [TestCase(BalzNormal, TestName = "GivenABalzCFile_WhenDecoded_ThenItIsTheSample")]
  [TestCase(BalzMaximum, TestName = "GivenABalzCxFile_WhenDecoded_ThenItIsTheSample")]
  [Category("HappyPath")]
  public void DecoderRestoresTheReference(string file)
    => Assert.That(BalzCodec.Decompress(Convert.FromBase64String(file)), Is.EqualTo(CodecProofData.Text(1024)));

  [Test, Category("HappyPath")]
  public void GivenABalzCFileOfAnExecutable_WhenDecoded_ThenTheCallAddressesAreRestored()
    => Assert.That(BalzCodec.Decompress(Convert.FromBase64String(BalzExecutable)), Is.EqualTo(Executable()));

  [Test, Category("HappyPath")]
  public void GivenTheStreamFormat_WhenDecodingABalzFile_ThenItIsTheSample() {
    using var output = new MemoryStream();
    BalzStream.Decompress(new MemoryStream(Convert.FromBase64String(BalzNormal)), output);
    Assert.That(output.ToArray(), Is.EqualTo(CodecProofData.Text(1024)));
  }

  [Test, Category("HappyPath")]
  public void GivenTheBuildingBlock_WhenCompressing_ThenItWritesBalzC()
    => Assert.That(new BalzBuildingBlock().Compress(CodecProofData.Text(1024)), Is.EqualTo(Convert.FromBase64String(BalzNormal)));

  [Test, Category("BoundaryCase")]
  public void GivenNoBytes_WhenCompressed_ThenTheFileIsMagicLengthAndFlush() {
    var file = BalzCodec.Compress([]);
    Assert.Multiple(() => {
      Assert.That(file, Is.EqualTo(new byte[] { 0xBA, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
      Assert.That(BalzCodec.Decompress(file), Is.Empty);
    });
  }

  [TestCase(1, TestName = "GivenOneByte_WhenRoundTripped_ThenItIsRestored")]
  [TestCase(2, TestName = "GivenTwoBytes_WhenRoundTripped_ThenOnlyTheOrderZeroPrefixIsUsed")]
  [TestCase(3, TestName = "GivenThreeBytes_WhenRoundTripped_ThenTheFilterIsSkipped")]
  [Category("BoundaryCase")]
  public void TinyInputs(int length) {
    var data = CodecProofData.Text(length);
    Assert.That(BalzCodec.Decompress(BalzCodec.Compress(data)), Is.EqualTo(data));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenTheWrongMagic_WhenDecoded_ThenItIsRefused() {
    var file = Convert.FromBase64String(BalzNormal);
    file[0] = 0xBB;
    Assert.Throws<InvalidDataException>(() => BalzCodec.Decompress(file));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenANegativeLength_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => BalzCodec.Decompress([0xBA, 0, 0, 0, 0, 0, 0, 0, 0x80, 0, 0, 0, 0]));

  [Test, Category("ExceptionalCase")]
  public void GivenATruncatedFile_WhenDecoded_ThenItIsRefused() {
    var file = Convert.FromBase64String(BalzNormal);
    Assert.Throws<InvalidDataException>(() => BalzCodec.Decompress(file.AsSpan(0, file.Length - 40)));
  }

  private static byte[] Executable() {
    var data = new List<byte> { (byte)'M', (byte)'Z' };
    data.AddRange(new byte[62]);
    data.AddRange("PE\0\0"u8.ToArray());
    for (var i = 0; i < 150; ++i) {
      data.Add(0xE8);
      data.AddRange(BitConverter.GetBytes(i * 37 % 4000 - 2000));
      data.AddRange(new byte[] { 0x90, 0x90, 0x8B, 0xC3 });
    }

    return [.. data];
  }
}
