using Compression.Core.Checksums;
using Compression.Core.Entropy.GolombRice;

namespace Compression.Tests.Ace;

[TestFixture]
public class Sha1Tests {
  [Category("ThemVsUs")]
  [Test]
  public void EmptyString() {
    var hash = Sha1.Compute([]);
    // SHA-1("") = da39a3ee 5e6b4b0d 3255bfef 95601890 afd80709
    Assert.That(hash, Is.EqualTo(new byte[] {
      0xDA, 0x39, 0xA3, 0xEE, 0x5E, 0x6B, 0x4B, 0x0D,
      0x32, 0x55, 0xBF, 0xEF, 0x95, 0x60, 0x18, 0x90,
      0xAF, 0xD8, 0x07, 0x09 }));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Abc() {
    var hash = Sha1.Compute("abc"u8);
    // SHA-1("abc") = a9993e36 4706816a ba3e2571 7850c26c 9cd0d89d
    Assert.That(hash, Is.EqualTo(new byte[] {
      0xA9, 0x99, 0x3E, 0x36, 0x47, 0x06, 0x81, 0x6A,
      0xBA, 0x3E, 0x25, 0x71, 0x78, 0x50, 0xC2, 0x6C,
      0x9C, 0xD0, 0xD8, 0x9D }));
  }

  [Category("ThemVsUs")]
  [Test]
  public void LongMessage() {
    // SHA-1("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq")
    var hash = Sha1.Compute("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"u8);
    Assert.That(hash, Is.EqualTo(new byte[] {
      0x84, 0x98, 0x3E, 0x44, 0x1C, 0x3B, 0xD2, 0x6E,
      0xBA, 0xAE, 0x4A, 0xA1, 0xF9, 0x51, 0x29, 0xE5,
      0xE5, 0x46, 0x70, 0xF1 }));
  }
}

[TestFixture]
public class GolombRiceTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_K0() {
    var encoder = new GolombRiceEncoder(0);
    for (var i = 0; i < 10; ++i)
      encoder.Encode(i);
    var encoded = encoder.ToArray();

    var decoder = new GolombRiceDecoder(encoded, 0);
    for (var i = 0; i < 10; ++i)
      Assert.That(decoder.Decode(), Is.EqualTo(i));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_K3() {
    var encoder = new GolombRiceEncoder(3);
    int[] values = [0, 1, 7, 8, 15, 16, 100];
    foreach (var v in values)
      encoder.Encode(v);
    var encoded = encoder.ToArray();

    var decoder = new GolombRiceDecoder(encoded, 3);
    foreach (var v in values)
      Assert.That(decoder.Decode(), Is.EqualTo(v));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void SignedRoundTrip() {
    var encoder = new GolombRiceEncoder(2);
    int[] values = [0, -1, 1, -2, 2, -10, 10];
    foreach (var v in values)
      encoder.EncodeSigned(v);
    var encoded = encoder.ToArray();

    var decoder = new GolombRiceDecoder(encoded, 2);
    foreach (var v in values)
      Assert.That(decoder.DecodeSigned(), Is.EqualTo(v));
  }
}
