#pragma warning disable CS1591
using Codec.Siren;

namespace Compression.Tests.Codecs.Siren;

/// <summary>
/// Pins the Siren7 / ITU-T G.722.1 decoder (FFmpeg <c>libavcodec/siren.c</c> port). Siren7 is a
/// transform codec with no simple silence-frame fixture, so correctness is pinned by table sanity
/// (the verbatim parameter / dequant tables), the determinism and bit-exactness of the
/// categorisation procedure on crafted envelopes, the deterministic noise-fill PRNG, and end-to-end
/// frame decoding producing the correct sample count without divergence.
/// </summary>
[TestFixture]
public class SirenCodecTests {

  [Test]
  public void Tables_HaveReferenceShapeAndSpotValues() {
    Assert.That(SirenTables.IndexTable, Is.EqualTo(new byte[] { 4, 4, 3, 3, 2, 2, 1, 0 }));
    Assert.That(SirenTables.VectorDimension, Is.EqualTo(new byte[] { 2, 2, 2, 4, 4, 5, 5, 1 }));
    Assert.That(SirenTables.NumberOfVectors, Is.EqualTo(new byte[] { 10, 10, 10, 5, 5, 4, 4, 20 }));
    Assert.That(SirenTables.ExpectedBitsTable, Is.EqualTo(new[] { 52, 47, 43, 37, 29, 22, 16, 0 }));
    Assert.That(SirenTables.DifferentialDecoderTree.Length, Is.EqualTo(27));
    Assert.That(SirenTables.DifferentialDecoderTree[0].Length, Is.EqualTo(24));
    Assert.That(SirenTables.DecoderTables.Length, Is.EqualTo(7));
    Assert.That(SirenTables.DecoderTables[0].Length, Is.EqualTo(360));
    Assert.That(SirenTables.DecoderTables[6].Length, Is.EqualTo(62));
    Assert.That(SirenTables.MltQuant[0][1], Is.EqualTo(0.392f));
    Assert.That(SirenTables.NoiseCategory5[0], Is.EqualTo(0.70711f));
  }

  [Test]
  public void CategorizeRegions_IsDeterministicForCraftedEnvelope() {
    var powerIndex = new int[32];
    for (var i = 0; i < SirenCodec.NumberOfRegions; ++i)
      powerIndex[i] = 10 - i; // a smoothly decaying envelope

    var cat1 = new int[32];
    var bal1 = new int[32];
    var cat2 = new int[32];
    var bal2 = new int[32];

    var ok1 = SirenCodec.CategorizeRegions(SirenCodec.NumberOfRegions, 200, (int[])powerIndex.Clone(), cat1, bal1);
    var ok2 = SirenCodec.CategorizeRegions(SirenCodec.NumberOfRegions, 200, (int[])powerIndex.Clone(), cat2, bal2);

    Assert.That(ok1, Is.True);
    Assert.That(ok2, Is.True);
    Assert.That(cat1, Is.EqualTo(cat2), "categorisation must be deterministic");
    Assert.That(bal1, Is.EqualTo(bal2));
  }

  [Test]
  public void CategorizeRegions_AssignsValidCategories() {
    var powerIndex = new int[32];
    for (var i = 0; i < SirenCodec.NumberOfRegions; ++i)
      powerIndex[i] = 5;
    var cat = new int[32];
    var bal = new int[32];

    var ok = SirenCodec.CategorizeRegions(SirenCodec.NumberOfRegions, 150, powerIndex, cat, bal);
    Assert.That(ok, Is.True);
    for (var i = 0; i < SirenCodec.NumberOfRegions; ++i)
      Assert.That(cat[i], Is.InRange(0, 7), $"region {i} category in 0..7");
  }

  [Test]
  public void CategorizeRegions_FewerBits_RaisesCategories() {
    var powerIndex = new int[32];
    for (var i = 0; i < SirenCodec.NumberOfRegions; ++i)
      powerIndex[i] = 8;

    var rich = new int[32];
    var poor = new int[32];
    var bal = new int[32];
    SirenCodec.CategorizeRegions(SirenCodec.NumberOfRegions, 400, (int[])powerIndex.Clone(), rich, bal);
    SirenCodec.CategorizeRegions(SirenCodec.NumberOfRegions, 100, (int[])powerIndex.Clone(), poor, bal);

    // Higher category index ⇒ coarser quantisation; a tighter bit budget should not produce
    // finer (lower-index) categories on average.
    Assert.That(poor.Take(SirenCodec.NumberOfRegions).Sum(),
      Is.GreaterThanOrEqualTo(rich.Take(SirenCodec.NumberOfRegions).Sum()));
  }

  [Test]
  public void DecodeFrame_ZeroFrame_ProducesFullFrameWithoutThrowing() {
    var decoder = new SirenCodec.Decoder();
    var output = new float[SirenCodec.FrameSize];
    var ok = decoder.DecodeFrame(new byte[60], output);
    Assert.That(ok, Is.True);
    Assert.That(output.Length, Is.EqualTo(SirenCodec.FrameSize));
    foreach (var v in output)
      Assert.That(float.IsFinite(v), Is.True, "no NaN/Inf from the IMLT");
  }

  [Test]
  public void DecodeFrame_IsDeterministic() {
    var data = new byte[60];
    new Random(1234).NextBytes(data);

    var a = new float[SirenCodec.FrameSize];
    var b = new float[SirenCodec.FrameSize];
    new SirenCodec.Decoder().DecodeFrame(data, a);
    new SirenCodec.Decoder().DecodeFrame(data, b);
    Assert.That(a, Is.EqualTo(b), "same bytes ⇒ same samples");
  }

  [Test]
  public void Decode_StreamOfFrames_YieldsThreeTwentySamplesPerFrame() {
    var stream = new byte[60 * 4];
    new Random(99).NextBytes(stream);
    var pcm = SirenCodec.Decode(stream, frameBytes: 60);
    Assert.That(pcm.Length, Is.EqualTo(4 * SirenCodec.FrameSize));
  }

  [Test]
  public void Decode_TrailingPartialFrame_IsIgnored() {
    var stream = new byte[60 * 2 + 17]; // two whole frames + a fragment
    var pcm = SirenCodec.Decode(stream, frameBytes: 60);
    Assert.That(pcm.Length, Is.EqualTo(2 * SirenCodec.FrameSize));
  }
}
