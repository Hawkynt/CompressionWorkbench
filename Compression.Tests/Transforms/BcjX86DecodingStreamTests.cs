using Compression.Core.Transforms;

namespace Compression.Tests.Transforms;

/// <summary>
/// The write-side x86 decoder must give exactly the one-shot <see cref="BcjFilter.DecodeX86"/>
/// result (itself pinned to liblzma by BcjLiblzmaVectorTests), however the input is split.
/// </summary>
[TestFixture]
public class BcjX86DecodingStreamTests {

  /// <summary>Code-like bytes: dense E8/E9 opcodes with 00/FF operand tops, plus random filler.</summary>
  private static byte[] CallHeavy(int length, int seed) {
    var r = new Random(seed);
    var data = new byte[length];
    r.NextBytes(data);
    for (var i = 0; i + 5 <= length; i += r.Next(1, 9)) {
      data[i] = r.Next(2) == 0 ? (byte)0xE8 : (byte)0xE9;
      data[i + 4] = r.Next(2) == 0 ? (byte)0x00 : (byte)0xFF;
    }
    return data;
  }

  private static byte[] DecodeInPieces(byte[] filtered, int piece, uint startOffset = 0) {
    using var output = new MemoryStream();
    var stream = new BcjX86DecodingStream(output, leaveOpen: true, startOffset);
    for (var at = 0; at < filtered.Length; at += piece)
      stream.Write(filtered, at, Math.Min(piece, filtered.Length - at));
    stream.Finish();
    return output.ToArray();
  }

  [TestCase(1)]
  [TestCase(4)]
  [TestCase(5)]
  [TestCase(7)]
  [TestCase(4096)]
  [TestCase(65536)]
  [TestCase(200_000)]
  public void GivenFilteredCode_WhenWrittenInPieces_ThenTheResultEqualsTheOneShotDecode(int piece) {
    var original = CallHeavy(150_000, piece);
    var filtered = BcjFilter.EncodeX86(original);

    var decoded = DecodeInPieces(filtered, piece);

    Assert.That(decoded, Is.EqualTo(BcjFilter.DecodeX86(filtered)));
    Assert.That(decoded, Is.EqualTo(original));
  }

  [Test]
  public void GivenAStartAddress_WhenDecodedInPieces_ThenItMatchesTheOneShotDecodeAtThatAddress() {
    var original = CallHeavy(70_000, 3);
    var filtered = BcjFilter.EncodeX86(original, startOffset: 0x401000);

    Assert.That(DecodeInPieces(filtered, 333, 0x401000), Is.EqualTo(original));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(4)]
  public void GivenFewerBytesThanAnInstruction_WhenFinished_ThenTheyPassUnchanged(int length) {
    var data = Enumerable.Repeat((byte)0xE8, length).ToArray();

    Assert.That(DecodeInPieces(data, 1), Is.EqualTo(data));
  }

  [Test]
  public void GivenAFinishedStream_WhenWrittenAgain_ThenInvalidOperation() {
    using var stream = new BcjX86DecodingStream(new MemoryStream());
    stream.Finish();

    Assert.Throws<InvalidOperationException>(() => stream.Write([1, 2, 3]));
  }

  [Test]
  public void GivenAnUnfinishedStream_WhenDisposed_ThenThePendingTailIsWritten() {
    using var output = new MemoryStream();
    using (var stream = new BcjX86DecodingStream(output, leaveOpen: true))
      stream.Write([0x90, 0x90, 0x90]);

    Assert.That(output.ToArray(), Is.EqualTo(new byte[] { 0x90, 0x90, 0x90 }));
  }

  [Test]
  public void GivenNoInnerStream_WhenConstructed_ThenArgumentNull()
    => Assert.Throws<ArgumentNullException>(() => _ = new BcjX86DecodingStream(null!));
}
