using Compression.Core.FileSystems.Cvf;

namespace Compression.Tests.Cvf;

/// <summary>
/// Stacker SD-3 / SD-4 codecs and the Stacker per-cluster dispatch: round trips across data shapes and
/// match-geometry boundaries, and rejection of malformed payloads.
/// </summary>
[TestFixture]
public sealed class StackerCodecTests {

  private static IEnumerable<TestCaseData> Shapes() {
    yield return new TestCaseData(Array.Empty<byte>()).SetName("Sd3_{m}(empty)");
    yield return new TestCaseData(new byte[] { 0x42 }).SetName("Sd3_{m}(one byte)");
    yield return new TestCaseData(new byte[8192]).SetName("Sd3_{m}(zeros)");
    var random = new byte[8192];
    new Random(11).NextBytes(random);
    yield return new TestCaseData(random).SetName("Sd3_{m}(random)");
    foreach (var name in CvfSamples.Names)
      yield return new TestCaseData(CvfSamples.Get(name)).SetName($"Sd3_{{m}}({name})");
  }

  [Test, Category("Codec"), TestCaseSource(nameof(Shapes))]
  public void RoundTrips(byte[] data) {
    var payload = CvfLzCodec.Encode(data, CvfLzMethod.Sd3, level: 1)!;

    Assert.That(CvfLzCodec.DecompressStacker(payload, payload.Length, data.Length), Is.EqualTo(data));
  }

  // Window edge (2047 reachable, 2048 not), short/long offset split (127/128) and every length-code
  // boundary: 2..4 two bits, 5..7 four bits, 8..22 one nibble, 23 the first continued nibble, 37/38 the next.
  [Test, Category("Codec")]
  public void MatchGeometryBoundaries_RoundTrip(
      [Values(1, 127, 128, 2047, 2048)] int distance,
      [Values(2, 4, 5, 7, 8, 22, 23, 37, 38)] int length) {
    var random = new Random(distance * 100 + length);
    var data = new byte[distance + length + 64];
    random.NextBytes(data);
    data.AsSpan(0, length).CopyTo(data.AsSpan(distance));

    var payload = Sd3Codec.Encode(data);

    Assert.That(Sd3Codec.Decode(payload, data.Length), Is.EqualTo(data));
  }

  [Test, Category("Codec")]
  public void Sd3_ShortStream_LeavesTheClusterTailZero() {
    var data = "STACKER last cluster"u8.ToArray();
    var payload = Sd3Codec.Encode(data);

    var cluster = CvfLzCodec.DecompressStacker(payload, payload.Length, 8192);

    Assert.Multiple(() => {
      Assert.That(cluster.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data));
      Assert.That(cluster.AsSpan(data.Length).ToArray(), Is.All.EqualTo((byte)0));
    });
  }

  [Test, Category("Codec")]
  public void Sd3_PayloadPaddedToWholeSectors_StillDecodes() {
    var data = CvfSamples.Get("text-8k");
    var payload = Sd3Codec.Encode(data);
    var padded = new byte[(payload.Length + 511) / 512 * 512];
    payload.CopyTo(padded, 0);

    Assert.That(CvfLzCodec.DecompressStacker(padded, padded.Length, data.Length), Is.EqualTo(data));
  }

  [Test, Category("Codec")]
  public void Sd3_WrongCheckByte_IsRejected() {
    var data = CvfSamples.Get("dir-8k");
    var payload = Sd3Codec.Encode(data);
    payload[^1] ^= 0x80;

    Assert.That(() => Sd3Codec.Decode(payload, data.Length), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Codec")]
  public void Sd3_TruncatedStream_IsRejected() {
    var payload = Sd3Codec.Encode(CvfSamples.Get("text-8k"));

    Assert.That(() => Sd3Codec.Decode(payload.AsSpan(0, payload.Length / 2), 8192), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Codec")]
  public void Sd3_MoreDataThanTheCluster_IsRejected() {
    var payload = Sd3Codec.Encode(CvfSamples.Get("text-8k"));

    Assert.That(() => Sd3Codec.Decode(payload, 4096), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Codec")]
  public void Sd3_FirstTokenAMatch_IsRejected() {
    // 1 1 0000001 = a short match at distance 1 with nothing decoded yet.
    byte[] payload = [0b1100_0000, 0b1000_0000, 0x00, 0x00];

    Assert.That(() => Sd3Codec.Decode(payload, 512), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Codec")]
  public void Stacker_TooShortPayload_IsRejected() =>
    Assert.That(() => CvfLzCodec.DecompressStacker([0x00], 1, 512), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Codec")]
  public void Stacker_DsPayload_IsDispatchedToTheDoubleSpaceCodec() {
    var data = CvfSamples.Get("text-8k");
    var payload = CvfLzCodec.Encode(data, CvfLzMethod.Ds, level: 1)!;

    Assert.That(CvfLzCodec.DecompressStacker(payload, payload.Length, data.Length), Is.EqualTo(data));
  }

  [Test, Category("Codec")]
  public void Sd4_BadMagic_IsRejected() {
    var payload = Sd4Codec.Encode(CvfSamples.Get("text-8k"));
    payload[0] = 0x82;

    Assert.That(() => Sd4Codec.Decode(payload, payload.Length, 8192), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Codec")]
  public void Sd4_InconsistentHelperTable_IsRejected() {
    // Magic, then first=1, last=1 and helper lengths that over-subscribe the code (three 1-bit codes).
    var bits = new List<int>();
    void Put(int value, int n) { for (var i = n - 1; i >= 0; --i) bits.Add((value >> i) & 1); }
    Put(0x81, 16); Put(0, 3); Put(1, 5); Put(1, 4); Put(1, 4);
    Put(1, 4); for (var i = 1; i < 6; ++i) Put(0, 4);
    var payload = PackMsbFirstWords(bits);

    Assert.That(() => Sd4Codec.Decode(payload, payload.Length, 512), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Codec")]
  public void Sd4_EmptyHelperRange_IsRejected() {
    var bits = new List<int>();
    void Put(int value, int n) { for (var i = n - 1; i >= 0; --i) bits.Add((value >> i) & 1); }
    Put(0x81, 16); Put(4, 3); Put(2, 5);   // first = 5 > last = 2
    var payload = PackMsbFirstWords(bits);

    Assert.That(() => Sd4Codec.Decode(payload, payload.Length, 512), Throws.InstanceOf<InvalidDataException>());
  }

  private static byte[] PackMsbFirstWords(List<int> bits) {
    var words = (bits.Count + 15) / 16 + 4;
    var result = new byte[words * 2];
    for (var i = 0; i < bits.Count; ++i) {
      if (bits[i] == 0) continue;
      var word = i / 16;
      var bit = 15 - i % 16;
      var value = 1 << bit;
      result[word * 2] |= (byte)value;
      result[word * 2 + 1] |= (byte)(value >> 8);
    }
    return result;
  }
}
