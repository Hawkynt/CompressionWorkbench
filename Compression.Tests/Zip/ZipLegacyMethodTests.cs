using Compression.Core.Dictionary.Zip;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

[TestFixture]
public class ZipLegacyMethodTests {
  // ── Shrink (method 1) ────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Shrink_RoundTrip_Core() {
    var data = "Hello, Shrink!"u8.ToArray();
    var compressed = ShrinkEncoder.Encode(data);
    var decompressed = ShrinkDecoder.Decode(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Shrink_RoundTrip_Repetitive() {
    var data = new byte[2000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);
    var compressed = ShrinkEncoder.Encode(data);
    var decompressed = ShrinkDecoder.Decode(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Shrink_RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    var compressed = ShrinkEncoder.Encode(data);
    var decompressed = ShrinkDecoder.Decode(compressed, data.Length);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Shrink_ZipRoundTrip() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 13);
    ZipRoundTrip(data, ZipCompressionMethod.Shrink);
  }

  // ── Reduce (methods 2-5) ────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Reduce_Core_Factor4_RoundTrip() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);
    var compressed = ReduceEncoder.Encode(data, factor: 4);
    var decompressed = ReduceDecoder.Decode(compressed, data.Length, factor: 4);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Reduce_ZipRoundTrip_Factor4() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);
    ZipRoundTrip(data, ZipCompressionMethod.Reduce4);
  }

  // ── Implode (method 6) ────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Implode_Core_RoundTrip() {
    var data = new byte[1000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);
    var compressed = ImplodeEncoder.Encode(data, useLiteralTree: true, use8kDictionary: true);
    var decompressed = ImplodeDecoder.Decode(compressed, data.Length,
      hasLiteralTree: true, is8kDictionary: true);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Implode_ZipRoundTrip() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 7);
    ZipRoundTrip(data, ZipCompressionMethod.Implode);
  }

  // ── Shared helper ────────────────────────────────────────────────────

  private static void ZipRoundTrip(byte[] data, ZipCompressionMethod method) {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true))
        writer.AddEntry("test.dat", data, method);
      archive = ms.ToArray();
    }

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }
}
