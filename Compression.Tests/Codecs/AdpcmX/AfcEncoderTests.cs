#pragma warning disable CS1591
using Codec.AdpcmX;

namespace Compression.Tests.Codecs.AdpcmX;

[TestFixture]
public sealed class AfcEncoderTests {

  [Test]
  public void EncodeAfc_UsesNineByteFramesAndDecodesCloseToSource() {
    var source = new short[4097];
    for (var i = 0; i < source.Length; ++i)
      source[i] = (short)(Math.Sin(i * 2.0 * Math.PI / 71.0) * 14000.0);

    var encoded = Thp.EncodeAfc(source);
    var decoded = Thp.DecodeAfc(encoded, source.Length);
    var meanAbsoluteError = source.Zip(decoded, static (a, b) => Math.Abs(a - b)).Average();

    Assert.Multiple(() => {
      Assert.That(encoded.Length, Is.EqualTo(((source.Length + Thp.AfcSamplesPerFrame - 1) / Thp.AfcSamplesPerFrame) * Thp.AfcBytesPerFrame));
      Assert.That(decoded.Length, Is.EqualTo(source.Length));
      Assert.That(meanAbsoluteError, Is.LessThan(100.0));
    });
  }

  [Test]
  public void EncodeAfc_ZeroSignalIsBitExact() {
    var source = new short[33];
    var encoded = Thp.EncodeAfc(source);
    Assert.That(Thp.DecodeAfc(encoded, source.Length), Is.EqualTo(source));
  }
}
