using Compression.Core.Dictionary.Lzrw3;
using Compression.Tests.CodecProof;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// Third-party proof for <c>BB_Lzrw3</c>. The streams below were written by Ross N. Williams'
/// public-domain <c>old_lzrw3.c</c> (http://www.ross.net/compression/download/original/old_lzrw3.c),
/// compiled unmodified with gcc 16.2 (MinGW-w64, <c>-std=gnu89</c>, plus three small headers
/// for its types, <c>compress.h</c> constants and <c>fast_copy</c> as <c>memmove</c>) and called
/// through its <c>compress</c> entry point on whole buffers. Our encoder reproduces them byte for
/// byte and our decoder restores them; the reference decompressor also restored every stream we
/// wrote for the same inputs.
/// </summary>
[TestFixture]
public sealed class Lzrw3ReferenceTests {

  private const string Lzrw3Text =
    "AAAAAAAAZXNjYXBlIG9yZGVyIHRoZQAMIHJhbmdlIGRvZ4RjwYBzeW1iAAhvbCBxdWljayAwCgPSanVtcALAc4RjbGF6eSA3OTE5Cm92UOC0bM" +
    "AAbW9kZWwgtGwj+jE1ODM4CmZvAPx4IGNvbnRleHQgov0D0qOTQz7BgKOTHwDilQPShP7BJ8GAYnJvd24gMjM3NTd+4Ao1mkM+wSe6bMGAwScz" +
    "MTY3NgqjkwPS6JUDB6L9U6UzOTU5NQrilUM+Qz40NzUxNN4/CiP6ov2jkzWadBC5NZpTpVOlo5Mj+sEnWaU1NTj4NDMzROfxj0M+NjMzNTKyua" +
    "OTNZoj+gPSg3+jk/PsNzEyNzE0kMWAwYAD0oT+ov01muFbMP7ACvPswSeE/oT+NZpDPoT+ODcxMDkKhP4j+v/A4pU1msEnQz6i/YT+A9Kjkzk1" +
    "MDI4CsGAwScf//Ps4pXilUM+NZoyOTRFL/PsA9LilfGPtGxTpcGAwP8xMDg2Mwrxj+KVwYCE/rRstGzBJ6L9wYDxj8D/MTg3ODIKtGxTpQPSwi" +
    "fi+yP6Qz7BJyP68+zg/zI2NzAxBJHz7MGAov2E/lOl8+yjk7Rsov2i/Qf+U6Wi/aOTMzQ2MjAKU6VDPgPSI/qi/eKV8+wPBsEnuWzilUM+NDI1" +
    "MzlE5wPSc3ltYm+A/2wgY29udGU=";

  // Built by Digits() below.
  private const string Lzrw3Digits =
    "AAAAALZtME/w/1k4/1k/Ujc/Ui/VNi/VP1s1P1vfxDTbP9/Ejy0zjy0/FjI/Fq+HRfD/WT9SL9U/W9HEAAEAAAIDBAUGBwgJCgsMDQ4PEBEAAB" +
    "ITFBUWFxgZGhscHR4fICEAQCIjJCUmJygpKissLS4v91k6AAA7PD0+P0BBQkNERUZHSElKAABLTE1OT1BRUlNUVVZXWFlaAABbXF1eX2BhYmNk" +
    "ZWZnaGlqAABrbG1ub3BxcnN0dXZ3eHl6AAB7fH1+f4CBgoOEhYaHiImKAACLjI2Oj5CRkpOUlZaXmJmaAACbnJ2en6ChoqOkpaanqKmqAACrrK" +
    "2ur7CxsrO0tba3uLm6AAC7vL2+v8DBwsPExcbHyMnKAADLzM3Oz9DR0tPU1dbX2NnaAADb3N3e3+Dh4uPk5ebn6OnqAADr7O3u7/Dx8vP09fb3" +
    "+Pn64H37/P3+/y8qXyUfnDBbOc8vf2rfp8+VnwyU//m/Gp8DzyDvPS+WP3YPX084b1U3OG+Yj7XfDn/+r9e/tu+wD80Pck/LX6yPSf9F8Dn3WU" +
    "/wOU/wj+k4j+k//H/rsOpP8I/pf+u/6jY3ODk=";

  [Test, Category("HappyPath")]
  public void GivenTheReferenceText_WhenDecoded_ThenItIsTheSample()
    => Assert.That(Lzrw3Decompressor.Decompress(Convert.FromBase64String(Lzrw3Text)), Is.EqualTo(CodecProofData.Text(1024)));

  [Test, Category("HappyPath")]
  public void GivenTheSampleText_WhenCompressed_ThenItIsTheReferenceStream()
    => Assert.That(Lzrw3Compressor.Compress(CodecProofData.Text(1024)), Is.EqualTo(Convert.FromBase64String(Lzrw3Text)));

  /// <summary>Digits match the table's initial string "123456789012345678" before any history exists.</summary>
  [Test, Category("HappyPath")]
  public void GivenDigits_WhenCompressed_ThenItIsTheReferenceStream() {
    var data = Digits();
    Assert.Multiple(() => {
      Assert.That(Lzrw3Compressor.Compress(data), Is.EqualTo(Convert.FromBase64String(Lzrw3Digits)));
      Assert.That(Lzrw3Decompressor.Decompress(Convert.FromBase64String(Lzrw3Digits)), Is.EqualTo(data));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenACopyFromAnUntouchedSlot_WhenDecoded_ThenItYieldsTheStartString()
    // One copy item of length 18 from slot 0, which still points at the start string.
    => Assert.That(Lzrw3Decompressor.Decompress([0, 0, 0, 0, 0x01, 0x00, 0x0F, 0x00]), Is.EqualTo("123456789012345678"u8.ToArray()));

  [Test, Category("BoundaryCase")]
  public void GivenNoBytes_WhenCompressed_ThenTheReferenceStoresThem()
    => Assert.That(Lzrw3Compressor.Compress([]), Is.EqualTo(new byte[] { 1, 0, 0, 0 }));

  [Test, Category("BoundaryCase")]
  public void GivenFiveBytes_WhenCompressed_ThenTheyAreStoredBecauseTheHeaderAloneIsLonger()
    => Assert.That(Lzrw3Compressor.Compress("abcde"u8), Is.EqualTo(new byte[] { 1, 0, 0, 0, (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e' }));

  [Test, Category("BoundaryCase")]
  public void GivenIncompressibleBytes_WhenCompressed_ThenTheyAreStoredAndRestored() {
    var data = CodecProofData.Random(3000);
    var stream = Lzrw3Compressor.Compress(data);
    Assert.Multiple(() => {
      Assert.That(stream[0], Is.EqualTo(1));
      Assert.That(stream.Length, Is.EqualTo(data.Length + 4));
      Assert.That(Lzrw3Decompressor.Decompress(stream), Is.EqualTo(data));
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAnUnknownFlag_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => Lzrw3Decompressor.Decompress([2, 0, 0, 0, 0, 0, 0x41]));

  [Test, Category("ExceptionalCase")]
  public void GivenFewerThanFourBytes_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => Lzrw3Decompressor.Decompress([0, 0]));

  [Test, Category("ExceptionalCase")]
  public void GivenACopyItemCutInHalf_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => Lzrw3Decompressor.Decompress([0, 0, 0, 0, 0x01, 0x00, 0x0F]));

  [Test, Category("ExceptionalCase")]
  public void GivenAControlWordWithNoItem_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => Lzrw3Decompressor.Decompress([0, 0, 0, 0, 0x00, 0x00]));

  private static byte[] Digits() {
    var data = new List<byte>();
    for (var i = 0; i < 20; ++i) data.AddRange("0123456789012345678"u8.ToArray());
    for (var i = 0; i < 3; ++i) data.AddRange(Enumerable.Range(0, 256).Select(b => (byte)b));
    for (var i = 0; i < 10; ++i) data.AddRange("1234567890123456789"u8.ToArray());
    return [.. data];
  }
}
