#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Opus;

namespace Compression.Tests.Opus;

[TestFixture]
public class OpusTests {

  // ──────────── Behavior: decodable Opus surfaces FULL + channels ────────────

  [Test]
  public void OpusDescriptor_ListsFullAndChannels_FromDecodableStream() {
    var opus = MakeDecodableStereoOpus();
    using var ms = new MemoryStream(opus);
    var entries = new OpusFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.opus"), Is.True, "Should contain FULL.opus");
    Assert.That(entries.First(e => e.Name == "FULL.opus").Method, Is.EqualTo("opus"));
    // Stereo OpusHead → LEFT.wav / RIGHT.wav.
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.True,
      "Decodable Opus should surface at least one Channel entry");
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True, "Should contain LEFT.wav");
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Kind, Is.EqualTo("Channel"));
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Method, Is.EqualTo("pcm"));
  }

  [Test]
  public void OpusDescriptor_ExtractedChannelIsValidMonoWav() {
    var opus = MakeDecodableStereoOpus();
    using var ms = new MemoryStream(opus);
    using var output = new MemoryStream();
    new OpusFormatDescriptor().ExtractEntry(ms, "LEFT.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1),
      "Per-channel WAV must be mono");
  }

  // ──────────── Behavior: graceful fallback to FULL-only ────────────

  [Test]
  public void OpusDescriptor_GarbageStream_FallsBackToFullOnly_NoThrow() {
    // "OggS" + junk: not a valid Ogg Opus stream — the decoder will throw,
    // and the descriptor must swallow it and still surface FULL.opus.
    var blob = new byte[64];
    "OggS"u8.CopyTo(blob);
    for (var i = 4; i < blob.Length; ++i) blob[i] = (byte)(i * 7);

    using var ms = new MemoryStream(blob);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new OpusFormatDescriptor().List(ms, null));
    Assert.That(entries.Any(e => e.Name == "FULL.opus"), Is.True,
      "Fallback must still surface FULL.opus");
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False,
      "Undecodable Opus must not surface channels");
  }

  // ──────────── helpers — build a real decodable Opus/Ogg stream ────────────
  // Mirrors OpusCodecTests.Decompress_CeltFrames_EmitsCorrectSampleCount.

  private static byte[] MakeDecodableStereoOpus() {
    var stream = new MemoryStream();
    var head = BuildOpusHead(channels: 2, preSkip: 0, inputRate: 48000);
    var tags = BuildOpusTags(vendor: "Codec.Opus");
    // config=21 CELT-only WB 5 ms (240 samples/frame), code=0 (1 frame) → 0xA8.
    var audioFrame1 = new byte[] { 0xA8, 0x00, 0x00, 0x00 };
    var audioFrame2 = new byte[] { 0xA8, 0x00, 0x00, 0x00 };
    WriteOggPage(stream, 7, 0, 0x02 /* BOS */, new[] { head });
    WriteOggPage(stream, 7, 1, 0x00, new[] { tags });
    WriteOggPage(stream, 7, 2, 0x00, new[] { audioFrame1 });
    WriteOggPage(stream, 7, 3, 0x04 /* EOS */, new[] { audioFrame2 });
    return stream.ToArray();
  }

  private static byte[] BuildOpusHead(byte channels, ushort preSkip, uint inputRate) {
    var packet = new byte[19];
    Encoding.ASCII.GetBytes("OpusHead").CopyTo(packet, 0);
    packet[8] = 1; // version
    packet[9] = channels;
    BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10, 2), preSkip);
    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), inputRate);
    BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(16, 2), 0);
    packet[18] = 0; // mapping family
    return packet;
  }

  private static byte[] BuildOpusTags(string vendor) {
    var vendorBytes = Encoding.UTF8.GetBytes(vendor);
    var packet = new byte[8 + 4 + vendorBytes.Length + 4];
    Encoding.ASCII.GetBytes("OpusTags").CopyTo(packet, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), (uint)vendorBytes.Length);
    vendorBytes.CopyTo(packet, 12);
    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12 + vendorBytes.Length, 4), 0);
    return packet;
  }

  private static void WriteOggPage(Stream stream, uint serial, uint sequence, byte headerType, byte[][] packets) {
    var segments = new List<byte>();
    var body = new MemoryStream();
    foreach (var pkt in packets) {
      var remaining = pkt.Length;
      var written = 0;
      while (remaining >= 255) {
        segments.Add(255);
        body.Write(pkt, written, 255);
        written += 255;
        remaining -= 255;
      }
      segments.Add((byte)remaining);
      if (remaining > 0) body.Write(pkt, written, remaining);
    }

    Span<byte> header = stackalloc byte[27];
    Encoding.ASCII.GetBytes("OggS").CopyTo(header);
    header[4] = 0; // version
    header[5] = headerType;
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(14, 4), serial);
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(18, 4), sequence);
    header[26] = (byte)segments.Count;
    stream.Write(header);
    stream.Write(segments.ToArray(), 0, segments.Count);
    body.Position = 0;
    body.CopyTo(stream);
  }
}
