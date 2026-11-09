using Compression.Core.Transforms;

namespace Compression.Tests.Transforms;

/// <summary>
/// The streaming BCJ2 encoder: the four streams equal the in-memory encoder's for the same input,
/// however the input and the main stream are cut into reads.
/// </summary>
[TestFixture]
public class Bcj2EncodeStreamTests {
  /// <summary>Hands out at most <paramref name="step"/> bytes per read.</summary>
  private sealed class Trickle(byte[] data, int step) : MemoryStream(data) {
    public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, step));
    public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, step)]);
  }

  private static byte[] X86Like(int size, int seed) {
    var random = new Random(seed);
    var data = new byte[size];
    random.NextBytes(data);
    for (var i = 0; i + 6 < size; i += random.Next(1, 12)) {
      var kind = random.Next(4);
      if (kind == 0) { data[i] = 0xE8; data[i + 4] = 0x00; }
      else if (kind == 1) { data[i] = 0xE9; data[i + 4] = 0xFF; }
      else if (kind == 2) { data[i] = 0x0F; data[i + 1] = 0x84; data[i + 5] = 0x00; }
    }
    return data;
  }

  [TestCase(0, 1, 1)]
  [TestCase(1, 1, 1)]
  [TestCase(4, 1, 3)]
  [TestCase(5, 2, 1)]
  [TestCase(9, 3, 7)]
  [TestCase(200_000, 7, 13)]
  [TestCase(300_000, 65_536, 1 << 16)]
  [TestCase(300_000, 1_000_000, 1_000_000)]
  public void Given_AnyInput_When_SplitAsAStream_Then_TheFourStreamsEqualTheInMemoryEncoder(int size, int inputStep, int readStep) {
    var data = X86Like(size, size + inputStep);
    var (main, call, jump, rangeCoded) = Bcj2Filter.Encode(data);

    using var callOut = new MemoryStream();
    using var jumpOut = new MemoryStream();
    using var rcOut = new MemoryStream();
    var encoder = new Bcj2Filter.EncodeStream(new Trickle(data, inputStep), callOut, jumpOut, rcOut);
    using var mainOut = new MemoryStream();
    var buffer = new byte[readStep];
    for (int n; (n = encoder.Read(buffer, 0, buffer.Length)) > 0;)
      mainOut.Write(buffer, 0, n);

    Assert.That(mainOut.ToArray(), Is.EqualTo(main), "main");
    Assert.That(callOut.ToArray(), Is.EqualTo(call), "call");
    Assert.That(jumpOut.ToArray(), Is.EqualTo(jump), "jump");
    Assert.That(rcOut.ToArray(), Is.EqualTo(rangeCoded), "range coder");
    Assert.That(Bcj2Filter.Decode(main, call, jump, rangeCoded, size), Is.EqualTo(data));
  }
}
