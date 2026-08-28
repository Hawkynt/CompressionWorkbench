#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Ogg;

namespace Compression.Tests.Ogg;

/// <summary>
/// Pins the Ogg descriptor's channel-split path: a decodable Opus-in-Ogg stream
/// surfaces per-channel WAVs, while undecodable input falls back to the raw packet
/// blobs without throwing. (Opus-in-Ogg is used because it is the one Xiph codec
/// with a synthetic decode path; the same wiring covers Vorbis on real vectors.)
/// </summary>
[TestFixture]
public class OggChannelSplitTests {

  [Test]
  public void OggDescriptor_DecodableStereoOpus_SurfacesLeftAndRightChannels() {
    var ogg = BuildStereoCeltOpus();
    using var ms = new MemoryStream(ogg);
    var entries = new OggFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.ogg"), Is.True);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav" && e.Kind == "Channel"), Is.True, "expected LEFT.wav channel");
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav" && e.Kind == "Channel"), Is.True, "expected RIGHT.wav channel");
    Assert.That(entries.First(e => e.Name == "LEFT.wav").Method, Is.EqualTo("pcm"));
  }

  [Test]
  public void OggDescriptor_ExtractedChannelIsMonoWav() {
    var ogg = BuildStereoCeltOpus();
    using var ms = new MemoryStream(ogg);
    using var output = new MemoryStream();
    new OggFormatDescriptor().ExtractEntry(ms, "LEFT.wav", output, null);
    var wav = output.ToArray();
    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1)); // mono
  }

  [Test]
  public void OggDescriptor_HybridAudio_ListsFullAndItsChannels() {
    // A well-formed Ogg/Opus stream whose audio frame uses hybrid mode (config 12,
    // byte 0x60). Hybrid used to be refused and the descriptor had to fall back to
    // FULL.ogg alone; the vendored decoder handles it, so the channels come out too.
    var stream = new MemoryStream();
    WriteOggPage(stream, 9, 0, 0x02, [BuildOpusHead(channels: 2, preSkip: 0, inputRate: 48000)]);
    WriteOggPage(stream, 9, 1, 0x00, [BuildOpusTags("Codec.Opus")]);
    WriteOggPage(stream, 9, 2, 0x04, [[0x60, 0x00, 0x00, 0x00]]); // hybrid → unsupported
    var ogg = stream.ToArray();

    using var ms = new MemoryStream(ogg);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.That(() => entries = new OggFormatDescriptor().List(ms, null), Throws.Nothing);
    Assert.That(entries.Any(e => e.Name == "FULL.ogg"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.True);
  }

  // ── synthetic stereo CELT-only Opus-in-Ogg (mirrors Codec.Opus end-to-end test) ──

  private static byte[] BuildStereoCeltOpus() {
    var stream = new MemoryStream();
    var head = BuildOpusHead(channels: 2, preSkip: 0, inputRate: 48000);
    var tags = BuildOpusTags("Codec.Opus");
    var frame = new byte[] { 0xA8, 0x00, 0x00, 0x00 }; // config 21, CELT-only WB 5 ms, 1 frame
    WriteOggPage(stream, 7, 0, 0x02, [head]);
    WriteOggPage(stream, 7, 1, 0x00, [tags]);
    WriteOggPage(stream, 7, 2, 0x00, [frame]);
    WriteOggPage(stream, 7, 3, 0x04, [frame]);
    return stream.ToArray();
  }

  private static byte[] BuildOpusHead(byte channels, ushort preSkip, uint inputRate) {
    var packet = new byte[19];
    Encoding.ASCII.GetBytes("OpusHead").CopyTo(packet, 0);
    packet[8] = 1;
    packet[9] = channels;
    BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10, 2), preSkip);
    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), inputRate);
    BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(16, 2), 0);
    packet[18] = 0;
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
