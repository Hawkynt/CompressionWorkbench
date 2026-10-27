using System.Buffers.Binary;
using Compression.Core.DiskImage;
using FileSystem.Vdfs;

namespace Compression.Tests.Vdfs;

/// <summary>
/// Union's ZippedStream, the per-file codec of a zipped VDF volume: a 12-byte stream header,
/// then per block a 12-byte header and a complete zlib stream.
/// </summary>
[TestFixture]
public class VdfsZippedStreamTests {

  private static byte[] Pattern(int length, int seed = 1) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)(seed + i * 7 + i / 256);
    return data;
  }

  private static byte[] RoundTrip(byte[] data, int blockSize) {
    var encoded = VdfsZippedStream.Encode(data, blockSize);
    var layout = VdfsZippedStream.TryParse(ImageAccessor.FromBytes(encoded), 0, data.Length);
    Assert.That(layout, Is.Not.Null, "the encoder's own stream must parse");
    Assert.That(layout!.StoredLength, Is.EqualTo(encoded.Length));
    using var output = new MemoryStream();
    VdfsZippedStream.Decode(ImageAccessor.FromBytes(encoded), layout, output);
    return output.ToArray();
  }

  [TestCase(1, 8192)]
  [TestCase(8191, 8192)]
  [TestCase(8192, 8192)]
  [TestCase(8193, 8192)]
  [TestCase(25000, 8192)]
  [TestCase(54, 13)]
  [Category("RoundTrip")]
  public void Encode_Decode_RoundTrips_AtBlockBoundaries(int length, int blockSize) {
    // Given data one byte short of, exactly at, and one past a block boundary
    var data = Pattern(length);

    // When it is encoded and decoded
    var decoded = RoundTrip(data, blockSize);

    // Then the bytes come back unchanged
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Encode_IncompressibleData_RoundTrips() {
    var data = new byte[20000];
    new Random(42).NextBytes(data);
    Assert.That(RoundTrip(data, 8192), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Encode_WritesHeaderAndOneZlibStreamPerBlock() {
    // Given 20000 bytes in 8192-byte blocks
    var encoded = VdfsZippedStream.Encode(Pattern(20000), 8192);

    // Then the stream header carries length, block size and ceil(20000/8192) = 3 blocks
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(encoded), Is.EqualTo(20000u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(4)), Is.EqualTo(8192u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(8)), Is.EqualTo(3u));

    // And the first block header gives the full block, its zlib length, and the block size,
    // followed by a zlib header (CMF 0x78)
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(12)), Is.EqualTo(8192u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(20)), Is.EqualTo(8192u));
    Assert.That(encoded[24], Is.EqualTo(0x78));
  }

  [Test, Category("ErrorHandling")]
  public void Encode_EmptyInput_Throws() {
    // An empty file has no ZippedStream: ZenKit rejects a zero-length header, so empty
    // payloads stay raw and the encoder refuses to make one.
    using var output = new MemoryStream();
    Assert.Throws<ArgumentOutOfRangeException>(() => VdfsZippedStream.Encode(new MemoryStream(), 0, output));
  }

  private static byte[] Valid() => VdfsZippedStream.Encode(Pattern(100), 40);

  [Test, Category("EdgeCase")]
  public void TryParse_LengthDisagreeingWithCatalog_IsNotAStream() {
    // Given a valid stream of 100 bytes but a catalog that says 99
    // Then it is not taken for a stream (a raw .wav that starts with plausible numbers)
    Assert.That(VdfsZippedStream.TryParse(ImageAccessor.FromBytes(Valid()), 0, 99), Is.Null);
  }

  [Test, Category("EdgeCase")]
  public void TryParse_ZeroBlockSize_IsNotAStream() {
    var bytes = Valid();
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0);
    Assert.That(VdfsZippedStream.TryParse(ImageAccessor.FromBytes(bytes), 0, 100), Is.Null);
  }

  [Test, Category("EdgeCase")]
  public void TryParse_BlockCountNotCeilingOfLength_IsNotAStream() {
    var bytes = Valid();
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 4); // ceil(100/40) is 3
    Assert.That(VdfsZippedStream.TryParse(ImageAccessor.FromBytes(bytes), 0, 100), Is.Null);
  }

  [Test, Category("EdgeCase")]
  public void TryParse_BlockRunningPastTheEnd_IsNotAStream() {
    var bytes = Valid();
    Assert.That(VdfsZippedStream.TryParse(ImageAccessor.FromBytes(bytes[..^1]), 0, 100), Is.Null);
  }

  [Test, Category("EdgeCase")]
  public void TryParse_BlockLargerThanBlockSize_IsNotAStream() {
    var bytes = Valid();
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 41);
    Assert.That(VdfsZippedStream.TryParse(ImageAccessor.FromBytes(bytes), 0, 100), Is.Null);
  }

  [Test, Category("EdgeCase")]
  public void TryParse_HeaderPastTheEnd_IsNotAStream() {
    Assert.That(VdfsZippedStream.TryParse(ImageAccessor.FromBytes(new byte[11]), 0, 100), Is.Null);
    Assert.That(VdfsZippedStream.TryParse(ImageAccessor.FromBytes(Valid()), -1, 100), Is.Null);
  }

  [Test, Category("ErrorHandling")]
  public void Decode_CorruptBlock_ThrowsInvalidData() {
    // Given a well-formed stream whose first block's zlib data is damaged
    var bytes = Valid();
    var layout = VdfsZippedStream.TryParse(ImageAccessor.FromBytes(bytes), 0, 100)!;
    var first = layout.Blocks[0];
    for (var i = 2; i < first.CompressedLength; ++i) bytes[first.DataOffset + i] ^= 0x5A;

    // When it is decoded, Then decoding refuses rather than returning wrong bytes
    using var output = new MemoryStream();
    Assert.Throws<InvalidDataException>(() => VdfsZippedStream.Decode(ImageAccessor.FromBytes(bytes), layout, output));
  }
}
