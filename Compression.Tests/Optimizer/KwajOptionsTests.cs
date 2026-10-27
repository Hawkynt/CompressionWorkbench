using System.Buffers.Binary;
using Compression.Core.Deflate;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Kwaj;

namespace Compression.Tests.Optimizer;

/// <summary>
/// KWAJ exposes its writable method set and MSZIP Deflate effort to the generic
/// compression optimizer. The optimizer can therefore choose stored data when
/// MSZIP framing would expand it, while still searching all Deflate effort tiers
/// for compressible payloads.
/// </summary>
[TestFixture]
[Category("Slow")]
public sealed class KwajOptionsTests {

  private static byte[] CompressibleSample() {
    var phrases = new[] {
      "the quick brown fox jumps over the lazy dog ",
      "compression workbench KWAJ optimizer ",
      "MSZIP chooses matches and Huffman codes ",
    };
    using var output = new MemoryStream();
    var rng = new Random(20260907);
    for (var i = 0; i < 256; ++i)
      output.Write(System.Text.Encoding.ASCII.GetBytes(phrases[rng.Next(phrases.Length)]));
    return output.ToArray();
  }

  private static byte[] CompressAt(IStreamFormatOperations ops, byte[] data, string method,
    string level = "Default") {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    ops.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["Method"] = method,
        ["Level"] = level,
      },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(IStreamFormatOperations ops, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    ops.Decompress(input, output);
    return output.ToArray();
  }

  private static ushort MethodOf(ReadOnlySpan<byte> kwaj) =>
    BinaryPrimitives.ReadUInt16LittleEndian(kwaj[KwajConstants.MethodOffset..]);

  private static ReadOnlySpan<byte> PayloadOf(ReadOnlySpan<byte> kwaj) {
    var offset = BinaryPrimitives.ReadUInt16LittleEndian(kwaj[KwajConstants.DataOffsetOffset..]);
    return kwaj[offset..];
  }

  [Test, Category("Spec")]
  public void Kwaj_Options_SelectEachWritableMethod_AndRoundTrip() {
    var descriptor = new KwajFormatDescriptor();
    var data = "KWAJ method selection"u8.ToArray();

    foreach (var (method, expected) in new[] {
      ("Store", KwajConstants.MethodStore),
      ("XOR", KwajConstants.MethodXor),
      ("MSZIP", KwajConstants.MethodMsZip),
    }) {
      var compressed = CompressAt(descriptor, data, method);

      Assert.That(MethodOf(compressed), Is.EqualTo(expected),
        $"{method} must be written to the KWAJ method field");
      Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data),
        $"{method} output must round-trip");
    }
  }

  [TestCase("Fast", DeflateCompressionLevel.Fast)]
  [TestCase("Maximum", DeflateCompressionLevel.Maximum)]
  [Category("Spec")]
  public void Kwaj_MszipLevel_IsForwardedToDeflateCore(string level,
    DeflateCompressionLevel expectedLevel) {
    var descriptor = new KwajFormatDescriptor();
    var data = CompressibleSample();
    var compressed = CompressAt(descriptor, data, "MSZIP", level);

    Assert.That(MethodOf(compressed), Is.EqualTo(KwajConstants.MethodMsZip));
    Assert.That(PayloadOf(compressed).ToArray(),
      Is.EqualTo(MsZipCompressor.Compress(data, expectedLevel)));
    Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void Kwaj_Optimizer_FindsSmallest_AndResultRoundTrips() {
    var descriptor = new KwajFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.That(result.Parameters, Does.ContainKey("Method"));
    Assert.That(result.Parameters, Does.ContainKey("Level"));
    Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
    Assert.That(result.Ratio, Is.LessThan(1.0), "compressible data should shrink");
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data),
      "optimized KWAJ output must round-trip");

    foreach (var method in new[] { "Store", "XOR" }) {
      var candidate = CompressAt(descriptor, data, method);
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(candidate.Length),
        $"optimizer result must be no larger than {method}");
    }

    foreach (var level in new[] { "None", "Fast", "Default", "Best", "Maximum" }) {
      var candidate = CompressAt(descriptor, data, "MSZIP", level);
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(candidate.Length),
        $"optimizer result must be no larger than MSZIP/{level}");
    }
  }

  [Test, Category("Spec")]
  public void Kwaj_Optimizer_UsesStore_WhenMszipFramingWouldExpandTinyInput() {
    var descriptor = new KwajFormatDescriptor();
    byte[] data = [0x42];

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.That(result.Parameters["Method"], Is.EqualTo("Store"));
    Assert.That(MethodOf(result.Bytes), Is.EqualTo(KwajConstants.MethodStore));
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));
  }
}
