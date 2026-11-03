using Compression.Core.Dictionary.Zling;
using Compression.Tests.CodecProof;
using FileFormat.Zling;

namespace Compression.Tests.Zling;

/// <summary>
/// Third-party proof for zling (<c>BB_Zling</c> and the <c>Zling</c> stream format). The streams
/// below were written by libzling's demo (https://github.com/richox/libzling, revision
/// <c>6828e31517bf94fda8e1e8e15a583ab58acde637</c>, BSD-3-Clause) built with g++ 16.2 and
/// libstdc++, as <c>zling e</c>, <c>zling e4</c> and <c>zling e2</c>. Our encoder reproduces
/// them byte for byte and our decoder restores them; <c>zling d</c> restored every stream we wrote
/// at levels 0 and 4, from 3 bytes to 17 MB (two input blocks).
/// </summary>
[TestFixture]
public sealed class ZlingReferenceTests {

  private const string ZlingE0Text =
    "AQAABAAAAAI0AAAChzVWZmZmdneXd2VXeZkJgIBpBgmZCYB4BggJkIcHYAZnBoYAgACAAAAAAAAAAAAAAAAACQAAAAAAAAkAAAAAAAAAAAAAAA" +
    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAANVZFYImIkAAAAAAAAAAAAAAAAAAAAAAA" +
    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA0QzNDRFUAAAAAAAAAAAAH/+rqO9QpKVKoNeKWRRWimUSaWEfOWH3TEpcL+J11/R2PsZL5vdPodIET0f" +
    "MLV1a5Mvijqfe5eUKBBKtL70XW9N8oB1kmn1oFXsSSbxYl7DeRIOoSE8H7PkU4pTN10muw4Q5/k98uK7q8dptPHsZPg3zqH4c3tpH23hS7xgmB" +
    "7rwb4IfMtiWfrzpcQJROTIR2906Yeus6LeyTh25761Xv6PYmQhLa+vdTvdWull7yESTuf1Evu3jcnm8lnggPIdocbppmlNIS3RfLmwd7KZx2De" +
    "Fu2vpu3N2sGLbGabv2LxP9thCC0sbmZ/GD6QY39x+7Ufz0YXnMV3yIIGVET0GSlKIsp5M17Hk9FZqXe0PPKOxJ7MVkjwTIetQ44d8USHVDuF7+" +
    "O1qU2qSEeu4r2KPlLlZmsdjLNMFJ1OCzidrhygD1QkKh5IQhH8PYynpjrS8INz+EE6Ojgi10+MleOW0ElBhwNJbhzRncbhVN2R+EYip3Eo40wp" +
    "AA==";

  private const string ZlingE4Text =
    "AQAABAAAAAIsAAAChjVWZmZmdniXd2VXeZkJgIB4BgmZCYB4BggJkIcHYAZnBoYAgACAAAAAAAAAAAAAAAAACQAAAAAAAAkAAAAAAAAAAAAAAA" +
    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAANVVGYIeHkAAAAAAAAAAAAAAAAAAAAAAA" +
    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA0QzNDRFUAAAAAAAAAAAAH//LaONQpKVKoNeKGRRWimUSaWEfOXb9S4p8HgTL+9R28eAt5v1JodIEb1u" +
    "MTb7xibfFHUeep2UKDCV6P7UtZ01yROWSabVk1axJ5nEi3kPwyQ8hYbwvMuSLykO7Xga7XKCOM/XyIvvdj+Mg42Dk+EyhlD8sTk1z7bwJV7Qj8" +
    "/73r4JfMtiXvrzucQJROTIRx906vq2taLeyTi0x66xXv6PYj4u5fF534x3Vnpee1wvw3IO/cvaZKF8Ml1PKK+Yahhv68YU0hKEu5m9kzyM189k" +
    "0fyum84sHbzIItj8HbP/1fT91MKXd/5qei2Xfn933w1HowsGe/l+kgkVEX1FipKIct4O5+FgdFbqNS13fCCxIbMFErzSYeWQY0280CHVTuHHcK" +
    "73JlWkIxfxRkWfqXKzsg7GWSaKTqcFnE4XDtBbKhIVtyShCP7ph0Nd7Wj4yTn8JB0dHJHrF8bKcUXopKDDliRvHNEehv5QPZD4TiKncSjjTCkA";

  private const string ZlingE2Zeros =
    "AQAAB9AAAABtAAABITAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAACAQAAAAAAAAAAAAAAAAAAAJskSQIAAAAAAAAAAAAAgAMA";

  [TestCase(0, ZlingE0Text, TestName = "GivenTheSampleText_WhenCompressedAtLevel0_ThenItIsWhatZlingEWrites")]
  [TestCase(4, ZlingE4Text, TestName = "GivenTheSampleText_WhenCompressedAtLevel4_ThenItIsWhatZlingE4Writes")]
  [Category("HappyPath")]
  public void EncoderMatchesTheReference(int level, string expected)
    => Assert.That(ZlingCodec.Compress(CodecProofData.Text(1024), level), Is.EqualTo(Convert.FromBase64String(expected)));

  [TestCase(ZlingE0Text, TestName = "GivenAZlingEStream_WhenDecoded_ThenItIsTheSample")]
  [TestCase(ZlingE4Text, TestName = "GivenAZlingE4Stream_WhenDecoded_ThenItIsTheSample")]
  [Category("HappyPath")]
  public void DecoderRestoresTheReference(string stream)
    => Assert.That(ZlingCodec.Decompress(Convert.FromBase64String(stream)), Is.EqualTo(CodecProofData.Text(1024)));

  [Test, Category("HappyPath")]
  public void GivenZeros_WhenCompressedAtLevel2_ThenItIsWhatZlingE2Writes() {
    var zeros = new byte[2000];
    Assert.Multiple(() => {
      Assert.That(ZlingCodec.Compress(zeros, 2), Is.EqualTo(Convert.FromBase64String(ZlingE2Zeros)));
      Assert.That(ZlingCodec.Decompress(Convert.FromBase64String(ZlingE2Zeros)), Is.EqualTo(zeros));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheStreamFormat_WhenDecodingAZlingFile_ThenItIsTheSample() {
    using var output = new MemoryStream();
    ZlingStream.Decompress(new MemoryStream(Convert.FromBase64String(ZlingE4Text)), output);
    Assert.That(output.ToArray(), Is.EqualTo(CodecProofData.Text(1024)));
  }

  [Test, Category("HappyPath")]
  public void GivenTheBuildingBlock_WhenCompressing_ThenItWritesZlingE()
    => Assert.That(new ZlingBuildingBlock().Compress(CodecProofData.Text(1024)), Is.EqualTo(Convert.FromBase64String(ZlingE0Text)));

  [TestCase(0, TestName = "GivenNoBytes_WhenCompressed_ThenTheStreamIsEmptyLikeTheReference")]
  [TestCase(1, TestName = "GivenOneByte_WhenRoundTripped_ThenItIsStoredRaw")]
  [TestCase(2, TestName = "GivenTwoBytes_WhenRoundTripped_ThenBothAreStoredRaw")]
  [TestCase(700_000, TestName = "GivenMoreSymbolsThanOneSubBlock_WhenRoundTripped_ThenItIsRestored")]
  [Category("BoundaryCase")]
  public void RoundTrips(int length) {
    var data = CodecProofData.Text(length);
    var stream = ZlingCodec.Compress(data, 4);
    Assert.Multiple(() => {
      if (length == 0)
        Assert.That(stream, Is.Empty);
      Assert.That(ZlingCodec.Decompress(stream), Is.EqualTo(data));
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAnOutOfRangeLevel_WhenCompressing_ThenItIsRejected()
    => Assert.Throws<ArgumentOutOfRangeException>(() => ZlingCodec.Compress("x"u8, 5));

  [Test, Category("ExceptionalCase")]
  public void GivenAnUnknownBlockFlag_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => ZlingCodec.Decompress([2]));

  [Test, Category("ExceptionalCase")]
  public void GivenATruncatedStream_WhenDecoded_ThenItIsRefused() {
    var stream = Convert.FromBase64String(ZlingE0Text);
    Assert.Throws<InvalidDataException>(() => ZlingCodec.Decompress(stream.AsSpan(0, stream.Length - 30)));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAWrongDeclaredPosition_WhenDecoded_ThenItIsRefused() {
    var stream = Convert.FromBase64String(ZlingE0Text);
    stream[4] ^= 1; // encpos (big-endian, bytes 1..4) no longer matches the symbols
    Assert.Throws<InvalidDataException>(() => ZlingCodec.Decompress(stream));
  }
}
