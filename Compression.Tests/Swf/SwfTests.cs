using System.Buffers.Binary;
using System.IO.Compression;
using FileFormat.Swf;

namespace Compression.Tests.Swf;

[TestFixture]
public class SwfTests {
  // ── Helpers ────────────────────────────────────────────────────────────

  /// <summary>Builds a valid uncompressed FWS SWF with the given body bytes.</summary>
  private static byte[] BuildFws(byte[] body) {
    var header = new byte[8];
    header[0] = (byte)'F';
    header[1] = (byte)'W';
    header[2] = (byte)'S';
    header[3] = 10; // version
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(8 + body.Length));
    var result = new byte[8 + body.Length];
    Buffer.BlockCopy(header, 0, result, 0, 8);
    Buffer.BlockCopy(body, 0, result, 8, body.Length);
    return result;
  }

  /// <summary>Builds a CWS (zlib-compressed) SWF from an uncompressed body.</summary>
  private static byte[] BuildCws(byte[] body) {
    var header = new byte[8];
    header[0] = (byte)'C';
    header[1] = (byte)'W';
    header[2] = (byte)'S';
    header[3] = 10;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(8 + body.Length));

    using var ms = new MemoryStream();
    ms.Write(header);
    using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      zlib.Write(body);
    return ms.ToArray();
  }

  // ── FWS (uncompressed) ────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Fws_Passthrough_RoundTrips() {
    var body = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80 };
    var fws = BuildFws(body);

    using var input = new MemoryStream(fws);
    using var output = new MemoryStream();
    SwfStream.Decompress(input, output);

    Assert.That(output.ToArray(), Is.EqualTo(fws));
  }

  // ── CWS (zlib-compressed) ─────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Cws_Decompress_ProducesOriginalFws() {
    var body = new byte[256];
    new Random(42).NextBytes(body);
    var cws = BuildCws(body);

    using var input = new MemoryStream(cws);
    using var output = new MemoryStream();
    SwfStream.Decompress(input, output);

    var result = output.ToArray();
    Assert.That(result[0], Is.EqualTo((byte)'F'));
    Assert.That(result[1], Is.EqualTo((byte)'W'));
    Assert.That(result[2], Is.EqualTo((byte)'S'));
    // Body should match
    Assert.That(result.AsSpan(8).ToArray(), Is.EqualTo(body));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Cws_Compress_Decompress_RoundTrips() {
    var body = new byte[512];
    new Random(123).NextBytes(body);
    var fws = BuildFws(body);

    // Compress FWS → CWS
    using var compInput = new MemoryStream(fws);
    using var compOutput = new MemoryStream();
    SwfStream.Compress(compInput, compOutput);

    var cws = compOutput.ToArray();
    Assert.That(cws[0], Is.EqualTo((byte)'C'));
    Assert.That(cws[1], Is.EqualTo((byte)'W'));
    Assert.That(cws[2], Is.EqualTo((byte)'S'));

    // Decompress CWS → FWS
    using var decInput = new MemoryStream(cws);
    using var decOutput = new MemoryStream();
    SwfStream.Decompress(decInput, decOutput);

    Assert.That(decOutput.ToArray(), Is.EqualTo(fws));
  }

  // ── Edge cases ────────────────────────────────────────────────────────

  [Category("EdgeCase")]
  [Test]
  public void Fws_EmptyBody_Works() {
    var fws = BuildFws([]);

    using var input = new MemoryStream(fws);
    using var output = new MemoryStream();
    SwfStream.Decompress(input, output);

    Assert.That(output.ToArray(), Is.EqualTo(fws));
  }

  [Category("EdgeCase")]
  [Test]
  public void InvalidSignature_Throws() {
    var bad = new byte[] { (byte)'X', (byte)'W', (byte)'S', 10, 8, 0, 0, 0 };
    using var input = new MemoryStream(bad);
    using var output = new MemoryStream();
    Assert.Throws<InvalidDataException>(() => SwfStream.Decompress(input, output));
  }

  [Category("EdgeCase")]
  [Test]
  public void Compress_NonFws_Throws() {
    var cws = BuildCws(new byte[16]);
    using var input = new MemoryStream(cws);
    using var output = new MemoryStream();
    Assert.Throws<InvalidDataException>(() => SwfStream.Compress(input, output));
  }

  // ── Descriptor ────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Properties() {
    var desc = new SwfFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Swf"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".swf"));
    Assert.That(desc.MagicSignatures.Count, Is.GreaterThanOrEqualTo(2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Descriptor_Decompress_Compress_RoundTrips() {
    var body = new byte[200];
    new Random(99).NextBytes(body);
    var cws = BuildCws(body);

    var desc = new SwfFormatDescriptor();

    using var decInput = new MemoryStream(cws);
    using var decOutput = new MemoryStream();
    desc.Decompress(decInput, decOutput);

    var fws = decOutput.ToArray();
    Assert.That(fws[0], Is.EqualTo((byte)'F'));

    using var compInput = new MemoryStream(fws);
    using var compOutput = new MemoryStream();
    desc.Compress(compInput, compOutput);

    using var verifyInput = new MemoryStream(compOutput.ToArray());
    using var verifyOutput = new MemoryStream();
    desc.Decompress(verifyInput, verifyOutput);

    Assert.That(verifyOutput.ToArray(), Is.EqualTo(fws));
  }
}
