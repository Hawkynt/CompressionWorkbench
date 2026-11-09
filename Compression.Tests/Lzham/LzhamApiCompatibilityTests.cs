using FileFormat.Lzham;

namespace Compression.Tests.Lzham;

[TestFixture]
public sealed class LzhamApiCompatibilityTests {
  [Test]
  public void OriginalFourArgumentEntryPointRemainsCallable() {
    // Recompiled calls can bind to optional parameters and hide a binary compatibility break.
    var method = typeof(LzhamStream).GetMethod(nameof(LzhamStream.Compress),
      [typeof(Stream), typeof(Stream), typeof(int), typeof(int)]);
    Assert.That(method, Is.Not.Null);
    var compress = method!.CreateDelegate<Action<Stream, Stream, int, int>>();
    byte[] original = "The original LZHAM entry point remains callable."u8.ToArray();
    using var input = new MemoryStream(original);
    using var packed = new MemoryStream();
    compress(input, packed, 15, 8);
    packed.Position = 0;
    using var restored = new MemoryStream();
    LzhamStream.Decompress(packed, restored);
    Assert.That(restored.ToArray(), Is.EqualTo(original));
  }
}
