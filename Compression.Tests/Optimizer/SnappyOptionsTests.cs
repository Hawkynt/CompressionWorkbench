using Compression.Lib;
using Compression.Registry;
using FileFormat.Snappy;

namespace Compression.Tests.Optimizer;

/// <summary>
/// Snappy exposes two encoder-only search axes: framing chunk size and hash-table width.
/// Every candidate must remain a conforming, self-contained framed stream, while the generic
/// optimizer must return the smallest candidate it actually searches.
/// </summary>
[TestFixture]
public sealed class SnappyOptionsTests {
  private static byte[] SamplePayload() {
    const int segmentSize = 32 * 1024;
    var data = new byte[3 * segmentSize];

    for (var i = 0; i < segmentSize; ++i)
      data[i] = (byte)((i * 17 + (i >> 5)) % 251);

    new Random(0x51A770).NextBytes(data.AsSpan(segmentSize, segmentSize));
    data.AsSpan(0, segmentSize).CopyTo(data.AsSpan(2 * segmentSize));
    return data;
  }

  private static byte[] CompressAt(SnappyFormatDescriptor descriptor, byte[] data, string blockSize, string hashTableBits) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["BlockSize"] = blockSize,
        ["HashTableBits"] = hashTableBits,
      },
    });
    return output.ToArray();
  }

  private static byte[] CompressDefault(SnappyFormatDescriptor descriptor, byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output);
    return output.ToArray();
  }

  private static byte[] Decompress(SnappyFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  [Test, Category("Spec")]
  public void ExplicitDefaults_PreserveHistoricalOutput_AndAllCandidatesRoundTrip() {
    var descriptor = new SnappyFormatDescriptor();
    var data = SamplePayload();
    var blockSizes = descriptor.OptionsSchema.Single(o => o.Key == "BlockSize").AllowedValues!;
    var hashTableBits = descriptor.OptionsSchema.Single(o => o.Key == "HashTableBits").AllowedValues!;

    var historical = CompressDefault(descriptor, data);
    var explicitDefaults = CompressAt(descriptor, data, "64 KB", "14");
    Assert.That(explicitDefaults, Is.EqualTo(historical), "explicit defaults must preserve the old encoder output");

    foreach (var blockSize in blockSizes)
      foreach (var bits in hashTableBits) {
        var compressed = CompressAt(descriptor, data, blockSize, bits);
        Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data),
          $"candidate BlockSize={blockSize}, HashTableBits={bits} must round-trip");
      }
  }

  [Test, Category("Spec")]
  public void Optimizer_ReturnsGlobalMinimumAcrossDeclaredSearchSpace() {
    var descriptor = new SnappyFormatDescriptor();
    var data = SamplePayload();
    var blockSizes = descriptor.OptionsSchema.Single(o => o.Key == "BlockSize").AllowedValues!;
    var hashTableBits = descriptor.OptionsSchema.Single(o => o.Key == "HashTableBits").AllowedValues!;

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.That(result.Parameters, Does.ContainKey("BlockSize"));
    Assert.That(result.Parameters, Does.ContainKey("HashTableBits"));
    Assert.That(result.Probes, Is.EqualTo(blockSizes.Count * hashTableBits.Count), "the 40-candidate space should be searched exhaustively");
    Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data), "winning candidate must round-trip");

    var expectedMinimum = long.MaxValue;
    foreach (var blockSize in blockSizes)
      foreach (var bits in hashTableBits)
        expectedMinimum = Math.Min(expectedMinimum, CompressAt(descriptor, data, blockSize, bits).LongLength);

    Assert.That(result.CompressedSize, Is.EqualTo(expectedMinimum), "optimizer must return the smallest declared Snappy encoding");
  }

  [Test, Category("EdgeCase")]
  public void EncoderParameters_RejectOutOfRangeValues() {
    Assert.That(
      () => Compression.Core.Dictionary.Snappy.SnappyCompressor.Compress("snappy"u8, 7),
      Throws.TypeOf<ArgumentOutOfRangeException>());
    Assert.That(
      () => new SnappyFrameWriter(Stream.Null, SnappyFrameWriter.MaxBlockSize + 1, 14),
      Throws.TypeOf<ArgumentOutOfRangeException>());
  }
}
