using Compression.Registry.Cvf;

namespace Compression.Tests.Cvf;

/// <summary>
/// Self-consistency of the genuine DS/JM cluster codec: encode → decode must be
/// byte-exact across data shapes. (Byte-compatibility with the real dmsdos
/// decoder is gated separately via dcread.)
/// </summary>
[TestFixture]
public class CvfLzCodecTests {

  private static IEnumerable<(string Name, byte[] Data)> Samples() {
    yield return ("zeros", new byte[8192]);
    yield return ("text", Encoding_Repeat("The quick brown fox jumps over the lazy dog. ", 8192));
    var rnd = new byte[8192]; new Random(42).NextBytes(rnd);
    yield return ("random", rnd);
    var half = new byte[8192];
    new Random(7).NextBytes(half.AsSpan(0, 4096));   // first half random, rest zero
    yield return ("half", half);
    var hi = new byte[2048];
    for (var i = 0; i < hi.Length; i++) hi[i] = (byte)(128 + (i % 100)); // high-bit bytes
    yield return ("highbytes", hi);
  }

  private static byte[] Encoding_Repeat(string s, int n) {
    var unit = System.Text.Encoding.ASCII.GetBytes(s);
    var b = new byte[n];
    for (var i = 0; i < n; i++) b[i] = unit[i % unit.Length];
    return b;
  }

  [Test, Category("Codec")]
  public void Ds_RoundTrips([Values] bool unused) => RoundTrip(CvfLzMethod.Ds);

  [Test, Category("Codec")]
  public void Jm_RoundTrips([Values] bool unused) => RoundTrip(CvfLzMethod.Jm);

  [Test, Category("Codec")]
  public void Sq_RoundTrips([Values] bool unused) => RoundTrip(CvfLzMethod.Sq);

  [Test, Category("Codec")]
  public void Sd4_SelfRoundTrips() {
    foreach (var (name, data) in Samples()) {
      var payload = Sd4Codec.Encode(data);
      var back = Sd4Codec.Decode(payload, payload.Length, data.Length);
      Assert.That(back, Is.EqualTo(data), $"SD-4 self round-trip failed for {name}");
    }
  }

  private static void RoundTrip(CvfLzMethod method) {
    foreach (var (name, data) in Samples()) {
      var payload = CvfLzCodec.Compress(data, method, level: 1);
      if (payload is null) continue;  // incompressible → stored path (not this codec's job)
      var back = CvfLzCodec.Decompress(payload, payload.Length, data.Length);
      Assert.That(back, Is.EqualTo(data), $"{method}/{name} did not round-trip");
    }
  }
}
