using System.Text;
using Compression.Core.Dictionary.Density;
using Compression.Tests.CodecProof;
using FileFormat.Density;

namespace Compression.Tests.Density;

/// <summary>
/// Third-party proof for density (<c>BB_Density</c> and the payload of the <c>Density</c> format)
/// against density-rs 0.16.6 (Guillaume Voirin, MIT OR Apache-2.0, https://github.com/g1mv/density,
/// revision <c>58938943bb0dfd85b186551a3d1d36cc286f9c1d</c>).
/// <list type="bullet">
/// <item>The upstream unit-test vectors in <c>src/lib.rs</c> for all three algorithms.</item>
/// <item>Streams a CLI around <c>Cheetah::encode</c>/<c>Lion::encode</c> (built with cargo) wrote,
/// both ways. On text, mixed bytes, zeros, a Windows executable and 100 KB of text, density-rs
/// writes the same bytes as ours for every algorithm and decodes all of ours.</item>
/// </list>
/// </summary>
[TestFixture]
public sealed class DensityReferenceTests {

  private const string UpstreamInput = "testtesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttesttestt";

  private static readonly byte[] UpstreamChameleon = [0xfe, 0xff, 0xff, 0x7f, 0, 0, 0, 0, 116, 101, 115, 116, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 112, 251, 116];
  private static readonly byte[] UpstreamCheetah = [244, 255, 255, 255, 255, 255, 255, 63, 116, 101, 115, 116, 112, 251, 116];
  private static readonly byte[] UpstreamLion = [112, 146, 36, 73, 146, 36, 116, 101, 115, 116, 112, 251, 73, 146, 36, 73, 146, 4, 116];

  private const string LionText =
    "AADAAAADZXNjYXBlIG9yZGVyIHRoZSByYW5nZSBkb2cgb2WGIGRvZyBzeW1ib2wgcXVpY2sgMArZ4WsganVtcHMgAAAAAAAAb3JkZXIgbGF6eS" +
    "A3OTE5Cm92ZXIgZXNjYXBlIG1vZGVsIGVzY2FwZSBqdW1wcyAxNTgzOApmb3ggY29udGV4dCBvdmVyIHF1aWNrIG9yZGVyIG1vZGVsIGRvZyBv" +
    "cmRlciBsYXp5IHF1aWNrIHN5bWJvbCBmb3ggZG9nIGJyb3cAYMABAABuIDIzNzU3CmNvbnRleHQg2CBsIGZveCBlc+uVcHMgZG9nIGZveCAzMT" +
    "Y3NgpvcmRlciBxdWljawAMAIABACBsYXp5IHF1aWNrIJx7IGJyb3duIDM5NTk1CmxhenkgbW9kZWwg2CBsIDQ3NTE0Cmp1bXBzIG92ZXIgb3bs" +
    "GAAAw2WGvAyt1bwMBZ93biBicm93biBvcmRlciBqdW1wcyBmb3gFn3duIG9lhoANwAZgwCA1NTQzMwpxY/2t1SBtb2RlbCA2MzM1Mh6uq09lci" +
    "Bjb250ZXh0IGpmDyBxdWljayBvZYYG4AB27AAg7mdlIDcxMjcxCmp1bTF1b3ggZG9nIHFj/SqhnHu8DCA3OTE5MApysAMYcAAYYW5nZac1KqFz" +
    "eW1ib2wgYx1+eHQgbW9kZWwqoTg3MTA5CnN5bWJvbBDWcHMgbHBgALABGGF6eSBe/2ZveCDYIGwgb3ZlciBzeW1ib2wgcXUJWaGociA5NTAyOA" +
    "pkb2cgh1pyYW5ngA0ANjzAZSBsYXp5IGwmBdggbCBjb250ZXh0IDI5NDQKamYPIO5nZSBxY/15IHRoZSBlc+uVBgADdgzABZ93biBkb2cgMTA4" +
    "NjMKdGhlSlx5IGRvZyBzeSTMBt0voHBlIGZveCBvdmVyIDOVABzYVgxYdGhlIDE4NzgyCmVz65V3biBxY/2nNatPd4ZlbCBmb3ggamYPAGADNm" +
    "wAZ2UgMjY3MDEKcmFuZ2Ugcu07teEgb3ZlciBzeSTMBZ93biBy7TurT2VyIGVzY2FwZSBvdjZgATZsGHQ0OAVicm93biBvdnQ0IDM0NjIwCmKl" +
    "dneGZWwgcWP9ENZwcyBvOAVsYXp5Bhw4gGEnIO5nZSBmb3ggZYOkZXIgbCYFbCA0MjUzOQrZ4WsgcXUJWfOm";

  private const string CheetahText =
    "AEAABAAAAABlc2NhcGUgb3JkZXIgdGhlIHJhbmdlIGRvZyBvZYYgZG9nIHN5bWJvbCBxdWljayAwCtnhayBqdW1wcyBvcmRlciBsYXp5IDc5MT" +
    "kKb3ZlciBlc2NhcGUgbW9kZWwgZXNjYXBlIGp1bXBzIDE1ODM4CmZveCBjb250ZXh0QEBBEwBBA0Agb3ZlciBxdWljayChqHIgbW9kZWwgZG9n" +
    "IKGo1WV6eSBxdWljayqhZm94IDOVYnJvd24gMjM3NTcKY29udGV4dCDYIGwgZm94IGVz65VwcyBkb2cgZm94IDMxNjc2Cm9yZGVyIHFj/VAAFA" +
    "B1HQBEIGxhenkgcXUJWZx7IGJyb3duIDM5NTk1CmxhenkgbW/mHNggbCA0NzUxNApqdW1wcyBvdmVyIG9lhrwMrdW8DAWfd24gYnJvd24gb3Jk" +
    "ZXIganVtcHMgZm94BZ93biBvZYZQQAFBAQ11DSA1NTQzMwpxY/2t1SBtb2RlbCA2MzM1Mh6uq09lciBjb250ZXh0IGpmDyBxdWljayBvZYYg7m" +
    "dlIDcxMjcxCmp1bTF1b3ggZG9nIHFj/SqhnHu8DCA3OTE5MApy1BA0EHQBFBRhbmdlpzUqoXN5bWJvbCBjHX54dCBtb2RlbCqhODcxMDkKc3lt" +
    "Ym9sENZwcyBsYXp5IF7/h1rYIGwgb3ZlciBzeW1ib2wgcXUJWaGociA5NTAyOAozlYdacmFuZ1AARUEBBHVAZSBsYXp5IGwmBdggbCBjb250ZX" +
    "h0IDI5NDQKamYPIO5nZSBxY/1KXHkgdGhlIGVz65UFn3duIGRvZyAxMDg2Mwp0aGVKXHkgZG9nIHN5JMwG3S+gcGUgZm94IG92ZXIgM5VAU3VQ" +
    "ABVFAXRoZSAxODc4Mgplc+uVd24gcWP9pzWrT3TGd4ZlbCBmb3ggamYPIO5nZSAyNjcwMQpyYW5nZSBy7Tu14QNKciBzeSTMBZ93biBy7TurT2" +
    "VyIGVzY2FwZSBvdhUFRRFB0xD1dDQ4BfHpbiBvdnQ0ZYYgMzQ2MjAKYqV2d4ZlbCBxY/0Q1nBzIG84BWxhenkg7mdlIGZveCBlg6RlciBsJgVs" +
    "IDQyNTM5CtnhayBxdQlZ86Y=";

  private static IEnumerable<TestCaseData> Upstream() {
    yield return new TestCaseData(DensityAlgorithm.Chameleon, UpstreamChameleon).SetName("GivenTheUpstreamChameleonVector_WhenEncodedAndDecoded_ThenBothMatch");
    yield return new TestCaseData(DensityAlgorithm.Cheetah, UpstreamCheetah).SetName("GivenTheUpstreamCheetahVector_WhenEncodedAndDecoded_ThenBothMatch");
    yield return new TestCaseData(DensityAlgorithm.Lion, UpstreamLion).SetName("GivenTheUpstreamLionVector_WhenEncodedAndDecoded_ThenBothMatch");
  }

  [TestCaseSource(nameof(Upstream)), Category("HappyPath")]
  public void UpstreamVector(DensityAlgorithm algorithm, byte[] expected) {
    var input = Encoding.ASCII.GetBytes(UpstreamInput);
    Assert.Multiple(() => {
      Assert.That(DensityCodec.Encode(input, algorithm), Is.EqualTo(expected));
      Assert.That(DensityCodec.Decode(expected, algorithm), Is.EqualTo(input));
    });
  }

  [TestCase(DensityAlgorithm.Cheetah, CheetahText, TestName = "GivenADensityRsCheetahStream_WhenDecoded_ThenItIsTheSample")]
  [TestCase(DensityAlgorithm.Lion, LionText, TestName = "GivenADensityRsLionStream_WhenDecoded_ThenItIsTheSample")]
  [Category("HappyPath")]
  public void ReferenceStreamDecodes(DensityAlgorithm algorithm, string stream)
    => Assert.That(DensityCodec.Decode(Convert.FromBase64String(stream), algorithm), Is.EqualTo(CodecProofData.Text(1024)));

  [TestCase(DensityAlgorithm.Cheetah, CheetahText, TestName = "GivenTheSampleText_WhenEncodedWithCheetah_ThenItIsWhatDensityRsWrites")]
  [TestCase(DensityAlgorithm.Lion, LionText, TestName = "GivenTheSampleText_WhenEncodedWithLion_ThenItIsWhatDensityRsWrites")]
  [Category("HappyPath")]
  public void EncoderMatchesTheReference(DensityAlgorithm algorithm, string stream)
    => Assert.That(DensityCodec.Encode(CodecProofData.Text(1024), algorithm), Is.EqualTo(Convert.FromBase64String(stream)));

  [TestCase(DensityAlgorithm.Chameleon)]
  [TestCase(DensityAlgorithm.Cheetah)]
  [TestCase(DensityAlgorithm.Lion)]
  [Category("BoundaryCase")]
  public void GivenIncompressibleBytes_WhenRoundTripped_ThenTheCopyProtectionKeepsThem(DensityAlgorithm algorithm) {
    var data = CodecProofData.Random(20_000);
    var stream = DensityCodec.Encode(data, algorithm);
    Assert.Multiple(() => {
      Assert.That(DensityCodec.Decode(stream, algorithm), Is.EqualTo(data));
      Assert.That(stream.Length, Is.LessThan(data.Length + data.Length / 16));
    });
  }

  [TestCase(0)]
  [TestCase(3)]
  [TestCase(65)]
  [Category("BoundaryCase")]
  public void GivenShortInputs_WhenRoundTrippedWithEveryAlgorithm_ThenTheyAreRestored(int length) {
    var data = CodecProofData.Text(length);
    foreach (var algorithm in Enum.GetValues<DensityAlgorithm>())
      Assert.That(DensityCodec.Decode(DensityCodec.Encode(data, algorithm), algorithm), Is.EqualTo(data), algorithm.ToString());
  }

  [Test, Category("HappyPath")]
  public void GivenTheContainer_WhenWritten_ThenItCarriesVersion2TheAlgorithmAndTheSize() {
    using var output = new MemoryStream();
    DensityStream.Compress(new MemoryStream(Encoding.ASCII.GetBytes(UpstreamInput)), output, DensityStream.Algorithm.Lion);
    var file = output.ToArray();
    Assert.Multiple(() => {
      Assert.That(file[..10], Is.EqualTo(new byte[] { (byte)'D', (byte)'E', (byte)'N', (byte)'S', 2, 3, (byte)UpstreamInput.Length, 0, 0, 0 }));
      Assert.That(file[10..], Is.EqualTo(UpstreamLion));
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAVersion1Container_WhenDecoded_ThenItIsRefused() {
    byte[] file = [(byte)'D', (byte)'E', (byte)'N', (byte)'S', 1, 1, 4, 0, 0, 0, 0, 0, 0, 0];
    Assert.Throws<InvalidDataException>(() => DensityStream.Decompress(new MemoryStream(file), new MemoryStream()));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAContainerWhoseSizeDisagrees_WhenDecoded_ThenItIsRefused() {
    using var output = new MemoryStream();
    DensityStream.Compress(new MemoryStream(Encoding.ASCII.GetBytes(UpstreamInput)), output, DensityStream.Algorithm.Cheetah);
    var file = output.ToArray();
    file[6] ^= 1;
    Assert.Throws<InvalidDataException>(() => DensityStream.Decompress(new MemoryStream(file), new MemoryStream()));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenATruncatedChameleonStream_WhenDecoded_ThenItIsRefused()
    => Assert.Throws<InvalidDataException>(() => DensityCodec.Decode(UpstreamChameleon.AsSpan(0, 5), DensityAlgorithm.Chameleon));
}
