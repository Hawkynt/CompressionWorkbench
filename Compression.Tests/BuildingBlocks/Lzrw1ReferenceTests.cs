using Compression.Core.Dictionary.Lzrw1;
using Compression.Tests.CodecProof;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// Third-party proof for <c>BB_Lzrw1</c>. The streams below were written by Ross N. Williams'
/// public-domain <c>old_lzrw1.c</c> (http://www.ross.net/compression/download/original/old_lzrw1.c),
/// compiled unmodified with gcc 16.2 (MinGW-w64, <c>-std=gnu89 -ftrivial-auto-var-init=zero</c>
/// so its uninitialised hash table starts empty) and called on whole buffers. Our encoder
/// reproduces them byte for byte and our decoder restores them; the reference
/// <c>lzrw1_decompress</c> also restored every stream we wrote for the same inputs.
/// </summary>
[TestFixture]
public sealed class Lzrw1ReferenceTests {

  private const string Lzrw1Text =
    "AAAAAAAAZXNjYXBlIG9yZGVyIHRoZQAMIHJhbmdlIGRvZwYUAwpzeW1iAAhvbCBxdWljayAwCgUIanVtcALAcwYlbGF6eSA3OTE5Cm92AkgGVc" +
    "AAbW9kZWwgBg0FLzE1ODM4CmZvAPx4IGNvbnRleHQgBDEFUgWFBTYDdwUQHwAEXAUhBogDPwMgYnJvd24gMjM3NTd+4AoHTwU+AyIMeAMvAxUz" +
    "MTY3NgoFWQVUCl8DBwSLBVUzOTU5NQoEHAVSBQY0NzUxNN4/CgXLBC4FSgd9dBJABwwFSAUGBSYFNwOGCxY1NTj4NDMzFlMDQAVpNjMzNTIUKA" +
    "U8B1YFRAW6g38FGhWwNzEyNzEGmQfsAwgFKhY4BLIHShOuMP7ACgU+A5QGJAYHByYFjgYVODcxMDkKBg0Fiv/AFCIHLQNHBTEEagYpBYMFrTk1" +
    "MDI4CgOZAywf/wV9BEMEBQU8B04yOTQXawUpBUkEKhMpFuAVTANTwP8xMDg2MwoDGwQkAxMGgwYrBgcDewShAyIDL8D/MTg3ODIKBh4FUAVsBC" +
    "o0GgXuBaEDFgUQBZjg/zI2NzAxFkoFEgNZBGIGgAVUBRwVCQZtBCUEBQf+BSMECwUiMzQ2MjAKBRcFeAWUBXoEKQTnBVYPBgOUC1QEGwU3NDI1" +
    "MzkmYQVDc3ltYm8AAGwgY29udGU=";

  private const string Lzrw1Zeros =
    "AAAAAP7/AA8BDxAPEA8QDxAPEA8QDxAPEA8QDxAPEA8QDxAPEP//DxAPEA8QDxAPEA8QDxAPEA8QDxAPEA8QDxAPEA8QDxD//w8QDxAPEA8QDx" +
    "APEA8QDxAPEA8QDxAPEA8QDxAPEA8Q//8PEA8QDxAPEA8QDxAPEA8QDxAPEA8QDxAPEA8QDxAPEP//DxAPEA8QDxAPEA8QDxAPEA8QDxAPEA8Q" +
    "DxAPEA8QDxD//w8QDxAPEA8QDxAPEA8QDxAPEA8QDxAPEA8QDxAPEA8Q//8PEA8QDxAPEA8QDxAPEA8QDxAPEA8QDxAPEA8QDxAPEP8fDxAPEA" +
    "8QDxAPEA8QDxAPEA8QDxAPEA8QDxAAAAAAAAAAAAAAAAAAAAAAAA==";

  [Test, Category("HappyPath")]
  public void GivenTheReferenceText_WhenDecoded_ThenItIsTheSample()
    => Assert.That(Lzrw1Decompressor.Decompress(Convert.FromBase64String(Lzrw1Text)), Is.EqualTo(CodecProofData.Text(1024)));

  [Test, Category("HappyPath")]
  public void GivenTheSampleText_WhenCompressed_ThenItIsTheReferenceStream()
    => Assert.That(Lzrw1Compressor.Compress(CodecProofData.Text(1024)), Is.EqualTo(Convert.FromBase64String(Lzrw1Text)));

  [Test, Category("HappyPath")]
  public void GivenZeros_WhenCompressed_ThenItIsTheReferenceStreamOfLongestCopies() {
    var zeros = new byte[2000];
    Assert.Multiple(() => {
      Assert.That(Lzrw1Compressor.Compress(zeros), Is.EqualTo(Convert.FromBase64String(Lzrw1Zeros)));
      Assert.That(Lzrw1Decompressor.Decompress(Convert.FromBase64String(Lzrw1Zeros)), Is.EqualTo(zeros));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenNoBytes_WhenCompressed_ThenTheReferenceStoresThem()
    => Assert.That(Lzrw1Compressor.Compress([]), Is.EqualTo(new byte[] { 1, 0, 0, 0 }));

  [Test, Category("BoundaryCase")]
  public void GivenFiveBytes_WhenCompressed_ThenTheyAreStoredBecauseTheHeaderAloneIsLonger()
    => Assert.That(Lzrw1Compressor.Compress("abcde"u8), Is.EqualTo(new byte[] { 1, 0, 0, 0, (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e' }));

  [Test, Category("BoundaryCase")]
  public void GivenIncompressibleBytes_WhenCompressed_ThenTheyAreStoredAndRestored() {
    var data = CodecProofData.Random(3000);
    var stream = Lzrw1Compressor.Compress(data);
    Assert.Multiple(() => {
      Assert.That(stream[0], Is.EqualTo(1));
      Assert.That(stream.Length, Is.EqualTo(data.Length + 4));
      Assert.That(Lzrw1Decompressor.Decompress(stream), Is.EqualTo(data));
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAnUnknownFlag_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => Lzrw1Decompressor.Decompress([2, 0, 0, 0, 0, 0, 0x41]));

  [Test, Category("ExceptionalCase")]
  public void GivenFewerThanFourBytes_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => Lzrw1Decompressor.Decompress([0, 0]));

  [Test, Category("ExceptionalCase")]
  public void GivenACopyBeforeTheStart_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => Lzrw1Decompressor.Decompress([0, 0, 0, 0, 0x01, 0x00, 0x02, 0x05]));

  [Test, Category("ExceptionalCase")]
  public void GivenTheReferenceTextCutInsideAnItem_WhenDecoded_ThenItIsRefused() {
    var stream = Convert.FromBase64String(Lzrw1Text);
    var cut = Enumerable.Range(stream.Length - 40, 40).First(n => {
      try {
        Lzrw1Decompressor.Decompress(stream.AsSpan(0, n));
        return false;
      } catch (InvalidDataException) {
        return true;
      }
    });
    Assert.That(cut, Is.LessThan(stream.Length));
  }
}
