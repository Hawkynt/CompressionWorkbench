using Compression.Core.Deflate;

namespace Compression.Tests.Deflate;

[TestFixture]
public sealed class MsZipTests {
  // -------------------------------------------------------------------------
  // Compressor
  // -------------------------------------------------------------------------

  [Test]
  public void Compress_EmptyInput_ProducesSingleBlock() {
    var compressed = MsZipCompressor.Compress([]);

    // Should start with "CK".
    Assert.That(compressed.Length, Is.GreaterThanOrEqualTo(2));
    Assert.That(compressed[0], Is.EqualTo(MsZipCompressor.SignatureByte0));
    Assert.That(compressed[1], Is.EqualTo(MsZipCompressor.SignatureByte1));
  }

  [Test]
  public void Compress_SmallInput_StartsWithCkSignature() {
    var data       = "Hello, MSZIP!"u8.ToArray();
    var compressed = MsZipCompressor.Compress(data);

    Assert.That(compressed[0], Is.EqualTo(0x43), "Expected 'C' signature byte");
    Assert.That(compressed[1], Is.EqualTo(0x4B), "Expected 'K' signature byte");
  }

  [Test]
  public void CompressBlocks_SmallInput_OneBlock() {
    var data   = "Hello, world!"u8.ToArray();
    var blocks = MsZipCompressor.CompressBlocks(data);

    Assert.That(blocks.Count, Is.EqualTo(1));
    Assert.That(blocks[0].UncompressedSize, Is.EqualTo(data.Length));
    Assert.That(blocks[0].CompressedData[0], Is.EqualTo(0x43));
    Assert.That(blocks[0].CompressedData[1], Is.EqualTo(0x4B));
  }

  [Test]
  public void CompressBlocks_ExactBlockBoundary_TwoBlocks() {
    // Exactly two 32 KB chunks.
    var data   = new byte[MsZipCompressor.BlockSize * 2];
    new Random(42).NextBytes(data);
    var blocks = MsZipCompressor.CompressBlocks(data);

    Assert.That(blocks.Count, Is.EqualTo(2));
    Assert.That(blocks[0].UncompressedSize, Is.EqualTo(MsZipCompressor.BlockSize));
    Assert.That(blocks[1].UncompressedSize, Is.EqualTo(MsZipCompressor.BlockSize));
  }

  [Test]
  public void CompressBlocks_OverBlockBoundary_MultipleBlocks() {
    var data   = new byte[MsZipCompressor.BlockSize + 100];
    new Random(7).NextBytes(data);
    var blocks = MsZipCompressor.CompressBlocks(data);

    Assert.That(blocks.Count, Is.EqualTo(2));
    Assert.That(blocks[0].UncompressedSize, Is.EqualTo(MsZipCompressor.BlockSize));
    Assert.That(blocks[1].UncompressedSize, Is.EqualTo(100));
  }

  // -------------------------------------------------------------------------
  // Decompressor – DecompressBlock
  // -------------------------------------------------------------------------

  [Test]
  public void DecompressBlock_InvalidSignature_Throws() {
    var bad = new byte[] { 0x00, 0x00, 0xDE, 0xAD };
    Assert.Throws<InvalidDataException>(() => MsZipDecompressor.DecompressBlock(bad));
  }

  [Test]
  public void DecompressBlock_TooShort_Throws() {
    Assert.Throws<InvalidDataException>(() => MsZipDecompressor.DecompressBlock([0x43]));
  }

  // -------------------------------------------------------------------------
  // Round-trip: data smaller than 32 KB
  // -------------------------------------------------------------------------

  [Test]
  public void RoundTrip_SmallData() {
    var original   = "The quick brown fox jumps over the lazy dog."u8.ToArray();
    var compressed = MsZipCompressor.Compress(original);
    var recovered  = MsZipDecompressor.Decompress(compressed, original.Length);

    Assert.That(recovered, Is.EqualTo(original));
  }

  [Test]
  public void RoundTrip_EmptyData() {
    var compressed = MsZipCompressor.Compress([]);
    var recovered  = MsZipDecompressor.Decompress(compressed, 0);

    Assert.That(recovered, Is.Empty);
  }

  [Test]
  public void RoundTrip_BinaryData() {
    var original = new byte[1024];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i & 0xFF);

    var compressed = MsZipCompressor.Compress(original);
    var recovered  = MsZipDecompressor.Decompress(compressed, original.Length);

    Assert.That(recovered, Is.EqualTo(original));
  }

  // -------------------------------------------------------------------------
  // Round-trip: data larger than 32 KB (multiple blocks)
  // -------------------------------------------------------------------------

  [Test]
  public void RoundTrip_MultiBlock() {
    // 80 KB of compressible data.
    var original = new byte[80 * 1024];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i % 251);

    var compressed = MsZipCompressor.Compress(original);
    var recovered  = MsZipDecompressor.Decompress(compressed, original.Length);

    Assert.That(recovered, Is.EqualTo(original));
  }

  [Test]
  public void RoundTrip_ExactBlockBoundary() {
    // Exactly 32 KB — should produce one block.
    var original = new byte[MsZipCompressor.BlockSize];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i % 127);

    var compressed = MsZipCompressor.Compress(original);
    var recovered  = MsZipDecompressor.Decompress(compressed, original.Length);

    Assert.That(recovered, Is.EqualTo(original));
  }

  [Test]
  public void RoundTrip_TwoExactBlocks() {
    // Exactly 64 KB — should produce two blocks.
    var original = new byte[MsZipCompressor.BlockSize * 2];
    new Random(123).NextBytes(original);

    var compressed = MsZipCompressor.Compress(original);
    var recovered  = MsZipDecompressor.Decompress(compressed, original.Length);

    Assert.That(recovered, Is.EqualTo(original));
  }

  // -------------------------------------------------------------------------
  // Round-trip via block API (as used by CabWriter/CabReader)
  // -------------------------------------------------------------------------

  [Test]
  public void RoundTrip_ViaCompressBlocksAndDecompressBlock() {
    var original = "MSZIP block API round-trip test."u8.ToArray();
    var blocks   = MsZipCompressor.CompressBlocks(original);

    Assert.That(blocks.Count, Is.EqualTo(1));

    var recovered = MsZipDecompressor.DecompressBlock(blocks[0].CompressedData);
    Assert.That(recovered, Is.EqualTo(original));
  }

  [Test]
  public void RoundTrip_HighlyCompressible() {
    // All-zeros — very high compression ratio.
    var original   = new byte[MsZipCompressor.BlockSize];
    var compressed = MsZipCompressor.Compress(original);
    var recovered  = MsZipDecompressor.Decompress(compressed, original.Length);

    Assert.That(recovered, Is.EqualTo(original));
  }
}
