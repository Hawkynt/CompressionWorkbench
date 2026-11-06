using Compression.Core.Transforms;

namespace Compression.Tests.BuildingBlocks;

/// <summary>The exact minimum-size PackBits packetizer, <see cref="PackBitsEncoding.EncodeOptimal"/>.</summary>
[TestFixture]
public sealed class PackBitsOptimalTests {

  [Test, Category("Optimization"), Category("RoundTrip")]
  public void GivenLiteralsThenAShortRun_WhenEncodedOptimally_ThenItBeatsTheGreedyPacketizer() {
    // 127 literal bytes followed by a two-byte run: the greedy packetizer fills a 128-byte
    // literal with the first repeated byte and leaves the second in a separate literal.
    var data = new byte[129];
    for (var i = 0; i < 127; ++i)
      data[i] = (byte)i;
    data[127] = data[128] = 0xFE;

    var greedy = PackBitsEncoding.Encode(data);
    var optimal = PackBitsEncoding.EncodeOptimal(data);
    Assert.Multiple(() => {
      Assert.That(greedy, Has.Length.EqualTo(131));
      Assert.That(optimal, Has.Length.EqualTo(130));
      Assert.That(PackBitsEncoding.Decode(optimal), Is.EqualTo(data));
    });
  }

  [Test, Category("Optimization")]
  public void GivenEveryShortBinaryInput_WhenEncodedOptimally_ThenTheSizeIsTheBruteForceMinimum() {
    for (var length = 0; length <= 10; ++length) {
      var variants = 1 << length;
      for (var mask = 0; mask < variants; ++mask) {
        var data = new byte[length];
        for (var bit = 0; bit < length; ++bit)
          data[bit] = (byte)((mask >> bit) & 1);

        var encoded = PackBitsEncoding.EncodeOptimal(data);
        Assert.That(encoded, Has.Length.EqualTo(MinimumPayloadSize(data)), $"length={length}, mask=0x{mask:X}");
        Assert.That(PackBitsEncoding.Decode(encoded), Is.EqualTo(data), $"length={length}, mask=0x{mask:X}");
      }
    }
  }

  [Test, Category("Boundary")]
  public void GivenALongRun_WhenEncodedOptimally_ThenItIsSplitIntoLegalRepeats() {
    var data = new byte[1000];
    Array.Fill(data, (byte)0x41);
    var encoded = PackBitsEncoding.EncodeOptimal(data);
    Assert.Multiple(() => {
      Assert.That(encoded, Has.Length.EqualTo(16));
      Assert.That(PackBitsEncoding.Decode(encoded), Is.EqualTo(data));
    });
  }

  private static int MinimumPayloadSize(ReadOnlySpan<byte> data) {
    var costs = new int[data.Length + 1];
    for (var i = data.Length - 1; i >= 0; --i) {
      var maxLength = Math.Min(128, data.Length - i);
      var best = int.MaxValue;

      for (var length = 1; length <= maxLength; ++length)
        best = Math.Min(best, 1 + length + costs[i + length]);

      var repeatLength = 1;
      while (repeatLength < maxLength && data[i + repeatLength] == data[i])
        ++repeatLength;
      for (var length = 2; length <= repeatLength; ++length)
        best = Math.Min(best, 2 + costs[i + length]);

      costs[i] = best;
    }

    return costs[0];
  }
}
