#pragma warning disable CS1591
using Compression.Core.Dictionary.Zip;

namespace Compression.Tests.Zip;

/// <summary>
/// Regression for the ZIP Shrink (method 1) codec round-trip on data large enough
/// to fill the 8192-entry LZW dictionary and trigger the partial-clear / code reuse
/// path — the case that desynced encoder and decoder on high-entropy 64 KB inputs.
/// </summary>
[TestFixture]
public class ShrinkRoundTripTests {

  private const int Size = 65536;

  private static byte[] Random42() {
    var r = new Random(42);
    var b = new byte[Size];
    r.NextBytes(b);
    return b;
  }

  private static byte[] BinaryStruct() {
    var b = new byte[Size];
    var rng = new Random(123);
    for (var i = 0; i < Size; i++)
      b[i] = (i % 16) switch {
        0 or 1 or 2 or 3 => (byte)(i / 16 & 0xFF),
        4 or 5 => 0,
        6 or 7 => (byte)(i % 3),
        _ => (byte)rng.Next(256),
      };
    return b;
  }

  private static void AssertRoundTrips(byte[] data) {
    var compressed = ShrinkEncoder.Encode(data);
    var decoded = ShrinkDecoder.Decode(compressed, data.Length);
    var firstDiff = -1;
    for (var i = 0; i < data.Length; ++i)
      if (decoded[i] != data[i]) { firstDiff = i; break; }
    Assert.That(firstDiff, Is.EqualTo(-1),
      firstDiff < 0 ? "" : $"first mismatch at {firstDiff}: expected {data[firstDiff]} got {decoded[firstDiff]}");
  }

  [Test]
  public void Random64K_RoundTrips() => AssertRoundTrips(Random42());

  [Test]
  public void BinaryStruct64K_RoundTrips() => AssertRoundTrips(BinaryStruct());
}
