using Compression.Lib;
using Compression.Registry;
using FileFormat.Cmix;

namespace Compression.Tests.Optimizer;

/// <summary>
/// Verifies the CMIX arithmetic-finalization search axis and compatibility default.
/// </summary>
[TestFixture]
public sealed class CmixOptionsTests {

  private static byte[] Compress(CmixFormatDescriptor descriptor, byte[] data, string finalization) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Finalization"] = finalization },
    });
    return output.ToArray();
  }

  private static byte[] CompressDefault(CmixFormatDescriptor descriptor, byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output);
    return output.ToArray();
  }

  private static byte[] Decompress(CmixFormatDescriptor descriptor, byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  private static byte[] Sample() {
    var data = new byte[4096];
    var rng = new Random(0xC_1A5);
    rng.NextBytes(data);
    for (var i = 64; i < data.Length; i += 97)
      data.AsSpan(i, Math.Min(32, data.Length - i)).CopyFrom(data.AsSpan(i - 64));
    return data;
  }

  [Test, Category("Spec")]
  public void Descriptor_AdvertisesOptimizer_AndPreservesDefaultOutput() {
    var descriptor = new CmixFormatDescriptor();
    var data = Sample();

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
    Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);

    var finalization = descriptor.OptionsSchema.Single(option => option.Key == "Finalization");
    Assert.That(finalization.Default, Is.EqualTo("Legacy"));
    Assert.That(finalization.AllowedValues, Is.EqualTo(new[] { "Legacy", "Compact" }));

    Assert.That(CompressDefault(descriptor, data), Is.EqualTo(Compress(descriptor, data, "Legacy")),
      "the optimizer schema must preserve the historical default encoding");
  }

  [Test, Category("Spec")]
  public void CompactFinalization_IsThreeBytesSmaller_AndRoundTrips() {
    var descriptor = new CmixFormatDescriptor();
    var data = Sample();
    var legacy = Compress(descriptor, data, "Legacy");
    var compact = Compress(descriptor, data, "Compact");

    Assert.That(compact.Length, Is.EqualTo(legacy.Length - 3));
    Assert.That(Decompress(descriptor, legacy), Is.EqualTo(data));
    Assert.That(Decompress(descriptor, compact), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void CompactFinalization_OneByteInput_RoundTripsWithEofPadding() {
    var descriptor = new CmixFormatDescriptor();
    byte[] data = [0];
    var compact = Compress(descriptor, data, "Compact");

    Assert.That(compact.Length, Is.EqualTo(6), "5-byte header plus one arithmetic termination byte");
    Assert.That(Decompress(descriptor, compact), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void EmptyInput_IsIdenticalForBothFinalizations() {
    var descriptor = new CmixFormatDescriptor();
    var legacy = Compress(descriptor, [], "Legacy");
    var compact = Compress(descriptor, [], "Compact");

    Assert.That(compact, Is.EqualTo(legacy));
    Assert.That(compact.Length, Is.EqualTo(5));
    Assert.That(Decompress(descriptor, compact), Is.Empty);
  }

  [Test, Category("Spec")]
  public void Optimizer_SelectsCompactFinalization_AndRoundTrips() {
    var descriptor = new CmixFormatDescriptor();
    var data = Sample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.That(result.Probes, Is.EqualTo(2), "the finite finalization axis should be searched exhaustively");
    Assert.That(result.Parameters["Finalization"], Is.EqualTo("Compact"));
    Assert.That(result.CompressedSize, Is.EqualTo(Compress(descriptor, data, "Compact").LongLength));
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));
  }
}

internal static class SpanCopyExtensions {
  public static void CopyFrom(this Span<byte> destination, ReadOnlySpan<byte> source) => source.CopyTo(destination);
}
