using System.Buffers.Binary;
using FileFormat.Lrzip;

namespace Compression.Tests.Lrzip;

[TestFixture]
public class LrzipTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_Small() {
    var data = new byte[1024];
    Array.Fill(data, (byte)0xCC);

    using var ms = new MemoryStream();
    new LrzipWriter().Write(data, ms);
    ms.Position = 0;

    using var r = new LrzipReader(ms);
    Assert.That(r.Extract(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_Large() {
    var rand = new Random(0xDEAD);
    var data = new byte[64 * 1024];
    rand.NextBytes(data);

    using var ms = new MemoryStream();
    new LrzipWriter().Write(data, ms);
    ms.Position = 0;

    using var r = new LrzipReader(ms);
    Assert.That(r.Extract(), Is.EqualTo(data));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[64];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new LrzipReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsLzo() {
    using var ms = new MemoryStream(BuildHeaderWithMethod(0x02));
    using var r = new LrzipReader(ms);
    var ex = Assert.Throws<NotSupportedException>(() => r.Extract());
    Assert.That(ex!.Message, Does.Contain("LZO"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBzip2() {
    using var ms = new MemoryStream(BuildHeaderWithMethod(0x03));
    using var r = new LrzipReader(ms);
    var ex = Assert.Throws<NotSupportedException>(() => r.Extract());
    Assert.That(ex!.Message, Does.Contain("BZIP2"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Reader_ExpandedSize() {
    var data = new byte[1234];
    Array.Fill(data, (byte)0x42);

    using var ms = new MemoryStream();
    new LrzipWriter().Write(data, ms);
    ms.Position = 0;

    using var r = new LrzipReader(ms);
    Assert.That(r.ExpandedSize, Is.EqualTo(1234UL));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsLrzi() {
    var d = new LrzipFormatDescriptor();
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x4C, 0x52, 0x5A, 0x49 }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new LrzipFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Lrzip"));
    Assert.That(d.DisplayName, Is.EqualTo("Long Range Zip"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.Extensions, Contains.Item(".lrz"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".lrz"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("lrzip-lzma"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
  }

  // Builds a minimal but well-formed lrzip header (magic + version + sizes + chosen method)
  // with no body, so reader header parsing succeeds and Extract() can probe the method gate.
  private static byte[] BuildHeaderWithMethod(byte method) {
    var buf = new byte[38];
    buf[0] = 0x4C; buf[1] = 0x52; buf[2] = 0x5A; buf[3] = 0x49; // "LRZI"
    buf[4] = 0x00; // major
    buf[5] = 0x06; // minor
    BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(6, 8), 0UL); // expandedSize
    buf[14] = method;
    // flags=0, hashType=0, reserved=0, hash=0 — already zeroed
    return buf;
  }
}
