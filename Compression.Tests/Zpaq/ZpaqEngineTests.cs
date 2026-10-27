using System.Text;
using Compression.Tests.Csc;
using FileFormat.Zpaq;

namespace Compression.Tests.Zpaq;

/// <summary>
/// The libzpaq conversion below the archive layer: suffix arrays, the ZPAQL compiler and
/// interpreter, method expansion, and single-block streams.
/// </summary>
[TestFixture]
public class ZpaqEngineTests {

  // ── suffix array ─────────────────────────────────────────────────────────

  private static int[] NaiveSuffixArray(byte[] s) =>
    Enumerable.Range(0, s.Length).OrderBy(i => i, Comparer<int>.Create((a, b) => s.AsSpan(a).SequenceCompareTo(s.AsSpan(b)))).ToArray();

  [Test, Category("HappyPath")]
  public void SuffixArray_MatchesNaiveSort([Values(0, 1, 2, 3, 17, 100, 1000)] int length, [Values(2, 4, 256)] int alphabet) {
    var rng = new Random(length * 31 + alphabet);
    var s = new byte[length];
    for (var i = 0; i < s.Length; ++i)
      s[i] = (byte)rng.Next(alphabet);
    Assert.That(ZpaqSuffixArray.Build(s), Is.EqualTo(NaiveSuffixArray(s)));
  }

  [Test, Category("BoundaryCase")]
  public void SuffixArray_PeriodicAndConstantInputs() {
    foreach (var s in new[] { Enumerable.Repeat((byte)7, 500).ToArray(), Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcab", 97))), Encoding.ASCII.GetBytes("mississippi") })
      Assert.That(ZpaqSuffixArray.Build(s), Is.EqualTo(NaiveSuffixArray(s)));
  }

  // ── squash / stretch (specification section 7, checked by libzpaq's own self test) ──

  [Test, Category("HappyPath")]
  public void SquashAndStretch_MatchLibzpaqChecksums() {
    uint stsum = 0, sqsum = 0;
    for (var i = 32767; i >= 0; --i)
      stsum = stsum * 3 + (uint)ZpaqPredictor.Stretch(i);
    for (var i = 4095; i >= 0; --i)
      sqsum = sqsum * 3 + (uint)ZpaqPredictor.Squash(i - 2048);
    Assert.Multiple(() => {
      Assert.That(stsum, Is.EqualTo(3887533746u));
      Assert.That(sqsum, Is.EqualTo(2278286169u));
    });
  }

  // ── compiler ─────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Compiler_ProducesLibzpaqMinCfgBytecode() {
    // min.cfg from the libzpaq documentation; the expected bytes are libzpaq's built-in "Model 1".
    const string minCfg = "comp 1 2 0 0 2 0 icm 16 1 isse 19 0 hcomp *b=a a=0 d=0 hash b-- hash *d=a d++ b-- hash b-- hash *d=a halt post 0 end";
    var hz = new ZpaqlMachine();
    var pz = new ZpaqlMachine();
    _ = new ZpaqCompiler(minCfg, null, hz, pz);
    var ms = new ZpaqBuffer();
    hz.Write(ms, false);
    byte[] model1 = [26, 0, 1, 2, 0, 0, 2, 3, 16, 8, 19, 0, 0, 96, 4, 28, 59, 10, 59, 112, 25, 10, 59, 10, 59, 112, 56, 0];
    Assert.That(ms.ToArray(), Is.EqualTo(model1));
  }

  [Test, Category("Exception")]
  public void Compiler_RejectsBadConfigs([Values("comp 0 0 0 0 1 0 bogus hcomp halt end", "comp 0 0 0 0 0 hcomp endif halt end", "comp 0 0 0 0 0 hcomp halt")] string config) =>
    Assert.That(() => new ZpaqCompiler(config, null, new(), new()), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("HappyPath")]
  public void MakeConfig_ExpandsArguments() {
    var args = new int[9];
    var config = ZpaqMethods.MakeConfig("x6.3ci1", args);
    Assert.Multiple(() => {
      Assert.That(args[..2], Is.EqualTo(new[] { 6, 3 }));
      Assert.That(config, Does.StartWith("comp 9 16 $1+20 $1+20 2\n0 icm"));
      Assert.That(config, Does.Contain("pcomp bwtrle c ;"));
      Assert.That(ZpaqMethods.MakeConfig("0", args), Is.EqualTo("comp 0 0 0 0 0 hcomp end\n"));
    });
  }

  // ── single-block streams ─────────────────────────────────────────────────

  private static byte[] RoundTrip(byte[] data, string method) {
    var output = new ZpaqBuffer();
    ZpaqMethods.CompressBlock(new ZpaqBuffer(data), output, method, "name", null, storeSha1: true);
    return ZpaqStream.Decompress(output.Span);
  }

  [Test, Category("RoundTrip")]
  public void Block_EveryTransformAndModel_RoundTrips([Values("0", "x0.0c0.0.255", "x0.1.4.0.3.20", "x0.2.4.0.3.20c0.0.511", "x0.3ci1", "x0.4", "x0.5.4.0.3.20", "x0.6.4.0.3.20", "x0.7ci1",
      "x0.1.4.0.5.21", "x0.0c256.0.255.1511.255i1aw1m16s8.32.255", "x0.0c0.1010.255t", "x0.0ci1,1,1,1,2aw2mm16tst")] string method) {
    byte[] data = [.. CscSamples.Text(9000, 31), .. CscSamples.X86(7000, 32), .. CscSamples.RandomBytes(2000, 33)];
    Assert.That(RoundTrip(data, method), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Block_LevelsWithHints_RoundTrip([Values("1", "2", "3", "4", "5")] string level, [Values(",0,0", ",200,1", ",120,2", ",250,3")] string hints) {
    var data = CscSamples.Mixed();
    Assert.That(RoundTrip(data, level + "0" + hints), Is.EqualTo(data));
  }

  [Test, Category("BoundaryCase")]
  public void Block_EmptyAndOneByte_RoundTrip([Values("0", "x0.1.4.0.3.20", "x0.2.4.0.3.20", "x0.3ci1", "x0.0ci1")] string method) {
    Assert.Multiple(() => {
      Assert.That(RoundTrip([], method), Is.Empty);
      Assert.That(RoundTrip([42], method), Is.EqualTo(new byte[] { 42 }));
    });
  }

  [Test, Category("Exception")]
  public void Block_FlippedChecksum_IsDetected() {
    var output = new ZpaqBuffer();
    ZpaqMethods.CompressBlock(new ZpaqBuffer(CscSamples.Text(5000, 34)), output, "x0.0ci1", null, null, storeSha1: true);
    var bytes = output.ToArray();
    bytes[^3] ^= 1;  // inside the SHA-1 trailer, before the end-of-block byte
    Assert.That(() => ZpaqStream.Decompress(bytes), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("HappyPath")]
  public void E8E9_IsUndoneByThePostprocessor() {
    var data = CscSamples.X86(30_000, 35);
    var copy = (byte[])data.Clone();
    ZpaqMethods.E8E9(copy);
    Assert.Multiple(() => {
      Assert.That(copy, Is.Not.EqualTo(data), "the transform changes call targets");
      Assert.That(RoundTrip(data, "x0.4"), Is.EqualTo(data));
    });
  }
}
