using System.Buffers.Binary;
using System.Text;
using Codec.Opus;

namespace Compression.Tests.Codecs.Opus;

[TestFixture]
public class OpusCodecTests {

  // ──────────── A1. TOC byte parse ────────────

  [Test]
  public void Toc_Config0_IsSilkOnlyNarrowband10ms_Mono_Code0() {
    // config=0 (00000), s=0, c=0  →  byte = 0b00000_0_00 = 0x00
    var toc = OpusPacketReader.ParseToc(0x00);
    Assert.That(toc.Mode, Is.EqualTo(OpusMode.SilkOnly));
    Assert.That(toc.Bandwidth, Is.EqualTo(OpusBandwidth.Narrowband));
    Assert.That(toc.FrameDurationMicros, Is.EqualTo(10000));
    Assert.That(toc.IsStereo, Is.False);
    Assert.That(toc.FrameCountCode, Is.EqualTo(0));
  }

  [Test]
  public void Toc_Config12_IsHybridSWB10ms() {
    // config=12 (01100), s=0, c=0 → byte = 0b01100_0_00 = 0x60
    var toc = OpusPacketReader.ParseToc(0x60);
    Assert.That(toc.Mode, Is.EqualTo(OpusMode.Hybrid));
    Assert.That(toc.Bandwidth, Is.EqualTo(OpusBandwidth.SuperWideband));
    Assert.That(toc.FrameDurationMicros, Is.EqualTo(10000));
  }

  [Test]
  public void Toc_Config28_StereoCode3_IsCeltOnlyFB2_5ms() {
    // config=28 (11100), s=1, c=3 → byte = 0b11100_1_11 = 0xE7
    var toc = OpusPacketReader.ParseToc(0xE7);
    Assert.That(toc.Mode, Is.EqualTo(OpusMode.CeltOnly));
    Assert.That(toc.Bandwidth, Is.EqualTo(OpusBandwidth.Fullband));
    Assert.That(toc.FrameDurationMicros, Is.EqualTo(2500));
    Assert.That(toc.IsStereo, Is.True);
    Assert.That(toc.FrameCountCode, Is.EqualTo(3));
    // 2.5 ms @ 48 kHz = 120 samples
    Assert.That(toc.FrameSamplesAt48k, Is.EqualTo(120));
  }

  [Test]
  public void Toc_Config21_IsCeltOnlyWideband_5ms() {
    // config=21 (10101), s=0, c=0 → byte = 0b10101_0_00 = 0xA8
    var toc = OpusPacketReader.ParseToc(0xA8);
    Assert.That(toc.Mode, Is.EqualTo(OpusMode.CeltOnly));
    Assert.That(toc.Bandwidth, Is.EqualTo(OpusBandwidth.Wideband));
    Assert.That(toc.FrameDurationMicros, Is.EqualTo(5000));
    // 5 ms @ 48 kHz = 240 samples
    Assert.That(toc.FrameSamplesAt48k, Is.EqualTo(240));
  }

  // ──────────── A2. Frame packing codes ────────────

  [Test]
  public void FramePacking_Code0_HasOneFrame() {
    var packet = new byte[] { 0x00, 0xAA, 0xBB, 0xCC };
    Assert.That(OpusPacketReader.CountFrames(packet), Is.EqualTo(1));
    var ranges = OpusPacketReader.SplitFrames(packet);
    Assert.That(ranges.Count, Is.EqualTo(1));
    Assert.That(ranges[0], Is.EqualTo(new Range(1, 4)));
  }

  [Test]
  public void FramePacking_Code1_HasTwoEqualFrames() {
    var packet = new byte[] { 0x01, 0xAA, 0xBB, 0xCC, 0xDD };
    Assert.That(OpusPacketReader.CountFrames(packet), Is.EqualTo(2));
    var ranges = OpusPacketReader.SplitFrames(packet);
    Assert.That(ranges.Count, Is.EqualTo(2));
    Assert.That(ranges[0], Is.EqualTo(new Range(1, 3)));
    Assert.That(ranges[1], Is.EqualTo(new Range(3, 5)));
  }

  [Test]
  public void FramePacking_Code3_VariableCount_Cbr() {
    // toc.code=3 (lower 2 bits = 0b11), VBR=0, padding=0, count=4
    // packet: [TOC | 0x04 | 8 data bytes]; 8/4 = 2 bytes/frame
    var packet = new byte[] { 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8 };
    Assert.That(OpusPacketReader.CountFrames(packet), Is.EqualTo(4));
    var ranges = OpusPacketReader.SplitFrames(packet);
    Assert.That(ranges.Count, Is.EqualTo(4));
    Assert.That(ranges[0], Is.EqualTo(new Range(2, 4)));
    Assert.That(ranges[3], Is.EqualTo(new Range(8, 10)));
  }

  // ──────────── A3. Ogg header parse ────────────

  [Test]
  public void OggHead_HandCrafted_DecodesChannelsAndPreSkip() {
    var stream = new MemoryStream();
    var head = BuildOpusHead(channels: 2, preSkip: 312, inputRate: 48000);
    WriteOggPage(stream, serial: 0x12345678, sequence: 0, headerType: 0x02 /* BOS */, packets: new[] { head });

    stream.Position = 0;
    var info = OpusCodec.ReadStreamInfo(stream);
    Assert.That(info.Channels, Is.EqualTo(2));
    Assert.That(info.PreSkip, Is.EqualTo(312));
    Assert.That(info.InputSampleRate, Is.EqualTo(48000));
    Assert.That(info.SampleRate, Is.EqualTo(48000));
  }

  [Test]
  public void OggHead_AndTags_DecodesVendor() {
    var stream = new MemoryStream();
    var head = BuildOpusHead(channels: 1, preSkip: 0, inputRate: 24000);
    var tags = BuildOpusTags(vendor: "Codec.Opus 1.0");
    WriteOggPage(stream, serial: 1, sequence: 0, headerType: 0x02, packets: new[] { head });
    WriteOggPage(stream, serial: 1, sequence: 1, headerType: 0x00, packets: new[] { tags });

    stream.Position = 0;
    var info = OpusCodec.ReadStreamInfo(stream);
    Assert.That(info.Channels, Is.EqualTo(1));
    Assert.That(info.Vendor, Is.EqualTo("Codec.Opus 1.0"));
  }

  // ──────────── A4. End-to-end CELT framing → silence PCM ────────────

  [Test]
  public void Decompress_CeltFrames_EmitsCorrectSampleCount() {
    var stream = new MemoryStream();
    var head = BuildOpusHead(channels: 2, preSkip: 0, inputRate: 48000);
    var tags = BuildOpusTags(vendor: "Codec.Opus");
    // config=21 CELT-only WB 5 ms (240 samples/frame), code=0 (1 frame), stereo bit=0
    // byte = 0b10101_0_00 = 0xA8
    var audioFrame1 = new byte[] { 0xA8, 0x00, 0x00, 0x00 };
    var audioFrame2 = new byte[] { 0xA8, 0x00, 0x00, 0x00 };
    WriteOggPage(stream, 7, 0, 0x02, new[] { head });
    WriteOggPage(stream, 7, 1, 0x00, new[] { tags });
    WriteOggPage(stream, 7, 2, 0x00, new[] { audioFrame1 });
    WriteOggPage(stream, 7, 3, 0x04 /* EOS */, new[] { audioFrame2 });

    stream.Position = 0;
    var pcm = new MemoryStream();
    OpusCodec.Decompress(stream, pcm);

    // 2 packets × 1 frame × 240 samples × 2 channels × 2 bytes = 1920 bytes
    Assert.That(pcm.Length, Is.EqualTo(1920));
  }

  // ──────────── A5. Hybrid mode rejected ────────────

  [Test]
  public void Decompress_HybridConfig_ThrowsNotSupported() {
    var stream = new MemoryStream();
    var head = BuildOpusHead(channels: 2, preSkip: 0, inputRate: 48000);
    var tags = BuildOpusTags(vendor: "x");
    // config=12 Hybrid SWB 10 ms, code 0
    var hybridFrame = new byte[] { 0x60, 0x00, 0x00, 0x00 };
    WriteOggPage(stream, 9, 0, 0x02, new[] { head });
    WriteOggPage(stream, 9, 1, 0x00, new[] { tags });
    WriteOggPage(stream, 9, 2, 0x04, new[] { hybridFrame });

    stream.Position = 0;
    var pcm = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => OpusCodec.Decompress(stream, pcm));
  }

  // ──────────── A6. Range decoder smoke test ────────────

  [Test]
  public void RangeDecoder_ConstructsAndReadsRawBitsWithoutThrowing() {
    var dec = new OpusRangeDecoder(new byte[] { 0xAB, 0xCD, 0xEF, 0x01, 0x23, 0x45, 0x67, 0x89 });
    var bits = dec.ReadBitsRaw(4);
    Assert.That(bits, Is.LessThan(16u));
    Assert.That(dec.Tell, Is.GreaterThan(0));
  }

  // ──────────── helpers ────────────

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
    // Build segment table from the packet sizes (each packet emits ceil(len/255) segments,
    // with a trailing <255 segment marking end-of-packet).
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
    // granule position: zeros for our tests
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(14, 4), serial);
    BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(18, 4), sequence);
    // crc: zeros (reader does not verify)
    header[26] = (byte)segments.Count;
    stream.Write(header);
    stream.Write(segments.ToArray(), 0, segments.Count);
    body.Position = 0;
    body.CopyTo(stream);
  }
}
