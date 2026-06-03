#pragma warning disable CS1591
using FileFormat.Sds;

namespace Compression.Tests.Sds;

[TestFixture]
public class SdsTests {

  private static void Write21(List<byte> b, int value) {
    b.Add((byte)(value & 0x7F));
    b.Add((byte)((value >> 7) & 0x7F));
    b.Add((byte)((value >> 14) & 0x7F));
  }

  private static byte[] DumpHeader(int sampleNumber, int bits, int rate, int lengthWords) {
    var period = (int)Math.Round(1_000_000_000.0 / rate);
    var b = new List<byte> { 0xF0, 0x7E, 0x00, 0x01 };
    b.Add((byte)(sampleNumber & 0x7F));
    b.Add((byte)((sampleNumber >> 7) & 0x7F));
    b.Add((byte)(bits & 0x7F));
    Write21(b, period);
    Write21(b, lengthWords);
    Write21(b, 0);        // loop start
    Write21(b, 0);        // loop end
    b.Add(0);             // loop type
    b.Add(0xF7);
    return b.ToArray();
  }

  /// <summary>Packs sample words into one data packet (MSB-first septets, left-justified).</summary>
  private static byte[] DataPacket(int[] words, int bits, byte packetIndex, bool pad = true) {
    var septets = (bits + 6) / 7;
    var fieldBits = septets * 7;
    var payload = new List<byte>();
    foreach (var w in words) {
      // Left-justify the `bits`-wide value into the `fieldBits` field.
      var field = fieldBits >= bits ? w << (fieldBits - bits) : w >> (bits - fieldBits);
      for (var s = septets - 1; s >= 0; --s)
        payload.Add((byte)((field >> (s * 7)) & 0x7F));
    }
    if (pad)
      while (payload.Count < 120) payload.Add(0);

    var b = new List<byte> { 0xF0, 0x7E, 0x00, 0x02, packetIndex };
    b.AddRange(payload);
    byte checksum = 0;
    b.Add(checksum);   // checksum (not validated by the reader)
    b.Add(0xF7);
    return b.ToArray();
  }

  private static byte[] Concat(params byte[][] parts) {
    using var ms = new MemoryStream();
    foreach (var p in parts) ms.Write(p);
    return ms.ToArray();
  }

  [Test]
  public void Lists_FullMetadataAndSampleWav() {
    // 16-bit, 4 sample words. Stored values are left-justified unsigned (0x8000 = silence).
    var words = new[] { 0x8000, 0x9000, 0x7000, 0xFFFF };
    var sds = Concat(DumpHeader(0, 16, 40000, words.Length), DataPacket(words, 16, 0));

    using var ms = new MemoryStream(sds);
    var entries = new SdsFormatDescriptor().List(ms, null);

    Assert.That(entries.First(e => e.Name == "FULL.sds").Kind, Is.EqualTo("Container"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.First(e => e.Name == "samples/000.wav").Kind, Is.EqualTo("Sample"));
  }

  [Test]
  public void Septet16Bit_DecodesToExpectedSignedPcm() {
    var words = new[] { 0x8000, 0x9000, 0x7000, 0xFFFF };
    var sds = Concat(DumpHeader(0, 16, 40000, words.Length), DataPacket(words, 16, 0));

    using var output = new MemoryStream();
    new SdsFormatDescriptor().ExtractEntry(new MemoryStream(sds), "samples/000.wav", output, null);
    var wav = output.ToArray();

    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(40000u));

    var pcm = wav.AsSpan(44);
    var decoded = new short[words.Length];
    for (var i = 0; i < words.Length; ++i)
      decoded[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2));

    // value - 0x8000, clamped to signed range.
    Assert.That(decoded, Is.EqualTo(new short[] { 0, 0x1000, unchecked((short)-0x1000), 0x7FFF }));
  }

  [Test]
  public void Septet8Bit_DecodesUsingCeilDiv7Septets() {
    // 8-bit → 2 septets per word. Values left-justified into 14 bits, top 8 kept.
    var words = new[] { 0x80, 0xC0, 0x40, 0xFF };
    var sds = Concat(DumpHeader(0, 8, 22050, words.Length), DataPacket(words, 8, 0));

    using var output = new MemoryStream();
    new SdsFormatDescriptor().ExtractEntry(new MemoryStream(sds), "samples/000.wav", output, null);
    var wav = output.ToArray();
    var pcm = wav.AsSpan(44);
    var decoded = new short[words.Length];
    for (var i = 0; i < words.Length; ++i)
      decoded[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2));

    // 8-bit value scaled to 16-bit (<< 8) then - 0x8000.
    Assert.That(decoded, Is.EqualTo(new short[] {
      (short)((0x80 << 8) - 0x8000),
      (short)((0xC0 << 8) - 0x8000),
      (short)((0x40 << 8) - 0x8000),
      (short)((0xFF << 8) - 0x8000),
    }));
  }

  [Test]
  public void Truncated_MissingTrailingPacketTolerated() {
    // Header claims 8 words but only one short (unpadded) packet of 4 words is present.
    var words = new[] { 0x8000, 0x9000, 0x7000, 0xFFFF };
    var sds = Concat(DumpHeader(0, 16, 40000, 8), DataPacket(words, 16, 0, pad: false));

    using var ms = new MemoryStream(sds);
    var entries = new SdsFormatDescriptor().List(ms, null);
    var sample = entries.First(e => e.Name == "samples/000.wav");
    // 4 decoded words → 8 bytes PCM + 44 header.
    Assert.That(sample.OriginalSize, Is.EqualTo(44 + words.Length * 2));
  }

  [Test]
  public void NoHeader_FallsBackToFullOnly() {
    var junk = new byte[] { 0xF0, 0x7E, 0x00, 0x7F, 0x00 };
    using var ms = new MemoryStream(junk);
    var entries = new SdsFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.sds"));
  }
}
