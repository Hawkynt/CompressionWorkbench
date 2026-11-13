using System.Buffers.Binary;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lz5;

namespace Compression.Tests.Lz5;

/// <summary>
/// The LZ5 1.5 block and frame codec: every token form decodes as specified, the encoder's
/// blocks round-trip across sizes and offset widths, and corrupt input is refused.
/// </summary>
[TestFixture]
public sealed class Lz5BlockTests {
  private static byte[] Decode(byte[] block, int maxSize = 1 << 16) {
    var window = new byte[maxSize];
    var n = Lz5Block.Decode(block, window, 0);
    return window[..n];
  }

  [Test]
  [Category("HappyPath")]
  public void Given_AShortOffsetMatch_When_Decoded_Then_TheMatchCopiesOverlapping() {
    // 1 00 11 011: 10-bit offset (high bits 0), 3 literals (field at its maximum, one 0 continuation), match 3+3.
    byte[] block = [0x9B, 0x00, (byte)'a', (byte)'b', (byte)'c', 0x03, 0x00];
    Assert.That(Decode(block), Is.EqualTo("abcabcabc"u8.ToArray()));
  }

  [Test]
  [Category("HappyPath")]
  public void Given_ARepeatOffset_When_Decoded_Then_ThePreviousOffsetIsUsedAgain() {
    // ...then 011 01 000: one literal 'x', the previous offset 3, match 3.
    byte[] block = [0x9B, 0x00, (byte)'a', (byte)'b', (byte)'c', 0x03, 0x68, (byte)'x', 0x00];
    Assert.That(Decode(block), Is.EqualTo("abcabcabcxbcx"u8.ToArray()));
  }

  [Test]
  [Category("HappyPath")]
  public void Given_SixteenAndTwentyFourBitOffsets_When_Decoded_Then_TheyReachBack() {
    // 00 001 000: 1 literal, 16-bit offset 1, match 3; 010 01 001: 1 literal, 24-bit offset 2, match 4.
    byte[] block = [0x08, (byte)'q', 0x01, 0x00, 0x49, (byte)'r', 0x02, 0x00, 0x00, 0x00];
    Assert.That(Decode(block), Is.EqualTo("qqqqrqrqr"u8.ToArray()));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AnOffsetBeforeTheStart_When_Decoded_Then_InvalidData() {
    byte[] block = [0x9B, 0x00, (byte)'a', (byte)'b', (byte)'c', 0x09, 0x00];
    Assert.Throws<InvalidDataException>(() => Decode(block));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_ATruncatedBlock_When_Decoded_Then_InvalidData() {
    byte[] block = [0x08, (byte)'q', 0x01];
    Assert.Throws<InvalidDataException>(() => Decode(block));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_ABlockLargerThanTheWindow_When_Decoded_Then_InvalidData() {
    byte[] block = [0x9B, 0x00, (byte)'a', (byte)'b', (byte)'c', 0x03, 0x00];
    Assert.Throws<InvalidDataException>(() => Decode(block, 5));
  }

  [Test]
  [Category("Boundary")]
  public void Given_AnEmptyBlock_When_Decoded_Then_Empty()
    => Assert.That(Decode([0x00]), Is.Empty);

  private static byte[] Data(string kind, int size) {
    var random = new Random(size);
    var data = new byte[size];
    switch (kind) {
      case "noise":
        random.NextBytes(data);
        break;
      case "zeros":
        break;
      case "text":
        return SevenZip.SevenZipOracle.Text(size, size);
      default: {
        // Repeats at every offset width: under 1 KiB, under 64 KiB, beyond.
        random.NextBytes(data);
        foreach (var distance in new[] { 500, 30_000, 200_000, 3_000_000 })
          for (var at = distance; at + 64 <= size; at += distance * 2 + 977)
            data.AsSpan(at - distance, 64).CopyTo(data.AsSpan(at));
        break;
      }
    }
    return data;
  }

  [Test]
  [Category("RoundTrip")]
  public void Given_AnyInput_When_EncodedAndDecoded_Then_TheBytesComeBack(
      [Values("text", "noise", "zeros", "distances")] string kind,
      [Values(0, 1, 12, 13, 64, 70_000, 4 << 20)] int size) {
    var data = Data(kind, size);
    var encoded = new byte[Lz5Block.MaxEncodedLength(size)];
    var length = Lz5Block.Encode(data, encoded);
    Assert.That(Decode(encoded[..length], Math.Max(size, 1)), Is.EqualTo(data));
    if (size >= 70_000 && kind == "zeros")
      Assert.That(length, Is.LessThan(size / 100), "a run compresses to a few repeat codes");
    if (size >= 70_000 && kind == "text")
      Assert.That(length, Is.LessThan(size * 3 / 4), "text compresses");
  }

  [Test]
  [Category("RoundTrip")]
  public void Given_AnyInput_When_WrittenAsAFrameAndDecoded_Then_TheBytesComeBack(
      [Values(0, 1, 70_000, (4 << 20) + 5)] int size) {
    var data = Data("distances", size);
    using var frame = new MemoryStream();
    Lz5Frame.Write(data, frame);
    using var output = new MemoryStream();
    Lz5Frame.Decode(frame.ToArray(), output);
    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  [Test]
  [Category("HappyPath")]
  public void Given_LinkedBlocks_When_Decoded_Then_AMatchReachesIntoTheEarlierBlock() {
    // FLG 0x40: version 1, linked blocks, nothing optional. Block 1 stores "abcdefgh"; block 2 is
    // one 8-byte match at offset 8 (00 000 101 + LE16 8) and the empty last literals.
    var frame = new List<byte>();
    frame.AddRange(BitConverter.GetBytes(Lz5Frame.Magic));
    frame.AddRange([0x40, 0x10, (byte)(XxHash32.Compute([0x40, 0x10]) >> 8)]);
    frame.AddRange(BitConverter.GetBytes(0x80000008u));
    frame.AddRange("abcdefgh"u8.ToArray());
    frame.AddRange(BitConverter.GetBytes(4u));
    frame.AddRange([0x05, 0x08, 0x00, 0x00]);
    frame.AddRange(BitConverter.GetBytes(0u));
    using var output = new MemoryStream();
    Lz5Frame.Decode(frame.ToArray(), output);
    Assert.That(output.ToArray(), Is.EqualTo("abcdefghabcdefgh"u8.ToArray()));
  }

  [Test]
  [Category("HappyPath")]
  public void Given_ASkippableFrameBetweenFrames_When_Decoded_Then_ItIsPassedOver() {
    using var stream = new MemoryStream();
    Lz5Frame.Write("one "u8, stream);
    stream.Write(BitConverter.GetBytes(0x184D2A50u));
    stream.Write(BitConverter.GetBytes(3u));
    stream.Write([1, 2, 3]);
    Lz5Frame.Write("two"u8, stream);
    using var output = new MemoryStream();
    Lz5Frame.Decode(stream.ToArray(), output);
    Assert.That(output.ToArray(), Is.EqualTo("one two"u8.ToArray()));
  }

  [TestCase(5, "header checksum")]
  [TestCase(-1, "content checksum")]
  [Category("EdgeCase")]
  public void Given_ACorruptChecksum_When_Decoded_Then_InvalidData(int index, string what) {
    using var stream = new MemoryStream();
    Lz5Frame.Write(Data("text", 5000), stream);
    var bytes = stream.ToArray();
    bytes[index < 0 ? bytes.Length + index : index + 9] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => Lz5Frame.Decode(bytes, Stream.Null), what);
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_NoLz5Magic_When_Decoded_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => Lz5Frame.Decode([0x04, 0x22, 0x4D, 0x18, 0, 0, 0], Stream.Null));

  [Test]
  [Category("Unit")]
  public void Given_TheWrittenHeader_When_Read_Then_ItDeclaresIndependent4MiBBlocksAndChecksums() {
    using var stream = new MemoryStream();
    Lz5Frame.Write("x"u8, stream);
    var bytes = stream.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes), Is.EqualTo(0x184D2205u));
    Assert.That(bytes[4], Is.EqualTo(0x6C));
    Assert.That(bytes[5], Is.EqualTo(0x40));
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(6)), Is.EqualTo(1UL));
  }
}
