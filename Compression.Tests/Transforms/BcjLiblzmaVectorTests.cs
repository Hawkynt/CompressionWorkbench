using System.Security.Cryptography;
using Compression.Core.Transforms;

namespace Compression.Tests.Transforms;

/// <summary>
/// The branch converters against liblzma: the SHA-256 of what liblzma's BCJ filters (CPython
/// 3.13.1's lzma module, raw filter chain [filter, LZMA2] decoded back through LZMA2 alone) make of
/// a 64 KiB input full of E8/E9, CALL and branch-bundle patterns. pybcj 1.0.8, built from 7-Zip's
/// C filters, produces the same bytes for every filter at start offset 0.
/// </summary>
[TestFixture]
[Category("ThirdPartyProof")]
public class BcjLiblzmaVectorTests {
  private static readonly byte[] Input = Mixed(65536);

  [TestCase("X86", 0x0, "1c44540e594f5bad68b48c7cf0eb2ea7d7089180d4d0444b0bae61cdf81fd69e")]
  [TestCase("X86", 0x1000, "24486f51f3020d0fd021a0cd664dec133e9fd3ee0fb28f6a879da888023def6e")]
  [TestCase("Sparc", 0x0, "a29632b3528639ce447a770b2114b8b9bdd88f15f1f6e02ca75c9311f025ffb0")]
  [TestCase("Sparc", 0x1000, "14e3128c7b09ce7c8a4c1dd27b49853921b74af9bf02ae5c235f42daaeb1b30b")]
  [TestCase("IA64", 0x0, "df50a60fda9046404c03b1a455b65a1efd6a5d72407ec401c5c7176f99de8883")]
  [TestCase("IA64", 0x1000, "c7443b9351aaa8b3600456ffbe46aa06049b3d73a391a871e517d3a8b817fa1a")]
  [TestCase("Arm", 0x0, "298864dbfef637566f621bae0dfa1bfd0cc64cb61e9a3f579db24660da6c6aac")]
  [TestCase("Arm", 0x1000, "0cbce61025c5b04a1e63c2114e3f8dd81faf3116a0f19cc7dba113ce2a01b581")]
  [TestCase("ArmThumb", 0x0, "c9cb2176428816f2610819bf2d24c4da3a6f0b9ceccbe31d1fbb6536ab7a3312")]
  [TestCase("ArmThumb", 0x1000, "7c4e07de8f941284fdd062f9048ff05f1e7a96f7633cef487614dec5fc91ba47")]
  [TestCase("PowerPC", 0x0, "fa3a2e0f183c9f15a78ad65e54adbbc639ea49bb8ee11e85e65538f7e8997533")]
  [TestCase("PowerPC", 0x1000, "12bf1863a47d11f1d82eaaffd1511c7c57ea3dba28f267136130e04b35c7bedf")]
  public void Given_TheReferenceInput_When_Encoded_Then_LiblzmasBytesComeOut_And_DecodingRestoresTheInput(string filter, int startOffset, string sha256) {
    var (encode, decode) = Filters(filter);

    var encoded = encode(Input, startOffset);

    Assert.That(Convert.ToHexStringLower(SHA256.HashData(encoded)), Is.EqualTo(sha256));
    Assert.That(decode(encoded, startOffset), Is.EqualTo(Input));
  }

  [Test]
  public void Given_TheInput_When_Hashed_Then_ItIsTheOneTheVectorsWereMadeFrom()
    => Assert.That(Convert.ToHexStringLower(SHA256.HashData(Input)), Is.EqualTo("c92747ed8ac944b82c72ef43ab2898e10fa3ea38efe0f77c06a5ed6ae074bb0c"));

  [TestCase("X86")]
  [TestCase("Sparc")]
  [TestCase("IA64")]
  public void Given_InputShorterThanOneUnit_When_Filtered_Then_ItPassesThroughUnchanged(string filter) {
    var (encode, decode) = Filters(filter);
    byte[] tiny = [0xE8, 0x40, 0x00, 0x7F];
    Assert.That(encode(tiny, 0), Is.EqualTo(tiny));
    Assert.That(decode(tiny, 0), Is.EqualTo(tiny));
  }

  private static (Func<byte[], int, byte[]> Encode, Func<byte[], int, byte[]> Decode) Filters(string filter) => filter switch {
    "X86" => ((d, o) => BcjFilter.EncodeX86(d, o), (d, o) => BcjFilter.DecodeX86(d, o)),
    "Sparc" => ((d, o) => BcjFilter.EncodeSparc(d, o), (d, o) => BcjFilter.DecodeSparc(d, o)),
    "IA64" => ((d, o) => BcjFilter.EncodeIA64(d, o), (d, o) => BcjFilter.DecodeIA64(d, o)),
    "Arm" => ((d, o) => BcjFilter.EncodeArm(d, o), (d, o) => BcjFilter.DecodeArm(d, o)),
    "ArmThumb" => ((d, o) => BcjFilter.EncodeArmThumb(d, o), (d, o) => BcjFilter.DecodeArmThumb(d, o)),
    "PowerPC" => ((d, o) => BcjFilter.EncodePowerPC(d, o), (d, o) => BcjFilter.DecodePowerPC(d, o)),
    _ => throw new ArgumentOutOfRangeException(nameof(filter)),
  };

  // Random bytes, runs and long-distance repeats: the same generator CodecProofData.Mixed uses.
  private static byte[] Mixed(int size) {
    var state = 0x9E3779B9u;
    var b = new byte[size];
    for (var i = 0; i < size; ++i)
      b[i] = (i % 7) switch {
        < 3 => (byte)Next(ref state),
        < 5 => (byte)(i * 31),
        _ => i == 0 ? (byte)0 : b[i - 1 - (int)(Next(ref state) % (uint)Math.Min(i, 5000))],
      };

    return b;
  }

  private static uint Next(ref uint state) {
    state ^= state << 13;
    state ^= state >> 17;
    state ^= state << 5;
    return state;
  }
}
