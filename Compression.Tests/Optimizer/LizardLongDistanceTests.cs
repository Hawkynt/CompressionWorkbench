using System.Buffers.Binary;
using FileFormat.Lizard;

namespace Compression.Tests.Optimizer;

[TestFixture]
public sealed class LizardLongDistanceTests {
  private const int FrameHeaderSize = 15;
  private const int RawBlockSize = 128 * 1024;

  [Test, Category("Spec")]
  public void LizV1_PreservesDictionaryAcrossInternalRawBlocks_AndEmits24BitOffset() {
    var firstBlock = new byte[RawBlockSize];
    new Random(0x51A4D).NextBytes(firstBlock);
    var data = new byte[RawBlockSize * 2];
    firstBlock.CopyTo(data, 0);
    firstBlock.CopyTo(data, RawBlockSize);

    var compressed = Compress(data);

    Assert.That(ReadOuterBlockSize(compressed) & 0x80000000u, Is.Zero,
      "cross-block repetition must make the 256 KiB frame block compressible");

    var position = FrameHeaderSize + sizeof(uint);
    Assert.That(compressed[position++], Is.EqualTo(29));
    SkipInternalBlock(compressed, ref position);

    var secondHeader = compressed[position++];
    Assert.That(secondHeader, Is.Zero,
      "level 29 does not use HUF and the second 128 KiB raw block must be compressed");

    var lengths = ReadRawStream(compressed, ref position);
    _ = ReadRawStream(compressed, ref position); // 16-bit offsets
    var offsets24 = ReadRawStream(compressed, ref position);
    _ = ReadRawStream(compressed, ref position); // tokens
    _ = ReadRawStream(compressed, ref position); // literals

    Assert.That(lengths, Is.Empty);
    var longOffsets = Enumerable.Range(0, offsets24.Length / 3)
      .Select(index => ReadUInt24(offsets24, index * 3))
      .ToArray();
    Assert.That(longOffsets, Does.Contain(RawBlockSize),
      "the second raw block must match against the first at a 128 KiB distance using the 24-bit offset stream");

    Assert.That(Decompress(compressed), Is.EqualTo(data));
  }

  private static byte[] Compress(byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    LizardStream.Compress(input, output, compressionLevel: 29, blockSize: 256 * 1024);
    return output.ToArray();
  }

  private static byte[] Decompress(byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    LizardStream.Decompress(input, output);
    return output.ToArray();
  }

  private static void SkipInternalBlock(byte[] data, ref int position) {
    var header = data[position++];
    if (header == 0x80) {
      var length = ReadUInt24(data, position);
      position += 3 + length;
      return;
    }

    Assert.That(header, Is.Zero, "level 29 internal streams must be raw rather than HUF-coded");
    for (var stream = 0; stream < 5; ++stream) {
      var length = ReadUInt24(data, position);
      position += 3 + length;
    }
  }

  private static byte[] ReadRawStream(byte[] data, ref int position) {
    var length = ReadUInt24(data, position);
    position += 3;
    var result = data.AsSpan(position, length).ToArray();
    position += length;
    return result;
  }

  private static uint ReadOuterBlockSize(byte[] compressed) =>
    BinaryPrimitives.ReadUInt32LittleEndian(compressed.AsSpan(FrameHeaderSize, sizeof(uint)));

  private static int ReadUInt24(byte[] data, int offset) =>
    data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16;
}