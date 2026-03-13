using FileFormat.Zstd;
using Compression.Core.Entropy.Fse;

namespace Compression.Tests.Zstd;

[TestFixture]
public class ZstdSequenceRoundTripTests {
  [Test]
  public void SequenceEncodeDecodeRoundTrip_Simple() {
    var sequences = new ZstdSequence[] {
      new(5, 4, 3),
      new(2, 3, 5),
    };

    byte[] output = new byte[1024];
    int[] repeatOffsetsEnc = [1, 4, 8];
    int written = ZstdSequences.EncodeSequences(sequences, output, 0, repeatOffsetsEnc);

    var pos = 0;
    int[] repeatOffsetsDec = [1, 4, 8];
    var blockData = output.AsSpan(0, written).ToArray();
    var decoded = ZstdSequences.DecodeSequences(blockData, ref pos, written, repeatOffsetsDec);

    Assert.That(decoded.Length, Is.EqualTo(sequences.Length));
    for (int i = 0; i < decoded.Length; ++i) {
      Assert.That(decoded[i].LiteralLength, Is.EqualTo(sequences[i].LiteralLength), $"LL mismatch at {i}");
      Assert.That(decoded[i].MatchLength, Is.EqualTo(sequences[i].MatchLength), $"ML mismatch at {i}");
      Assert.That(decoded[i].Offset, Is.EqualTo(sequences[i].Offset), $"OF mismatch at {i}");
    }
  }

  [Test]
  public void SequenceEncodeDecodeRoundTrip_SingleSequence() {
    var sequences = new ZstdSequence[] {
      new(3, 5, 10),
    };

    byte[] output = new byte[1024];
    int[] repeatOffsetsEnc = [1, 4, 8];
    int written = ZstdSequences.EncodeSequences(sequences, output, 0, repeatOffsetsEnc);

    var pos = 0;
    int[] repeatOffsetsDec = [1, 4, 8];
    var blockData = output.AsSpan(0, written).ToArray();
    var decoded = ZstdSequences.DecodeSequences(blockData, ref pos, written, repeatOffsetsDec);

    Assert.That(decoded.Length, Is.EqualTo(1));
    Assert.That(decoded[0].LiteralLength, Is.EqualTo(3));
    Assert.That(decoded[0].MatchLength, Is.EqualTo(5));
    Assert.That(decoded[0].Offset, Is.EqualTo(10));
  }
}
