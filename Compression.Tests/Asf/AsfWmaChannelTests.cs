#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.Asf;

namespace Compression.Tests.Asf;

/// <summary>
/// Pins the ASF descriptor's WMA wiring: a synthetic ASF whose audio stream is tagged
/// WMA v2 (0x161) and carries an all-zero coded superframe must surface decoded
/// per-channel WAVs (Kind <c>Channel</c>); a stream tagged WMA Pro (0x162) that lacks the
/// (>= 18-byte) codec-private extradata the WMA Pro decoder needs must fall back to the
/// raw <c>stream_NN.bin</c> blob, the documented graceful path.
/// </summary>
[TestFixture]
public class AsfWmaChannelTests {

  private static readonly byte[] HeaderObject =
    [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] FilePropertiesObject =
    [0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11, 0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] StreamPropertiesObject =
    [0x91, 0x07, 0xDC, 0xB7, 0xB7, 0xA9, 0xCF, 0x11, 0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65];
  private static readonly byte[] DataObject =
    [0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C];
  private static readonly byte[] AudioStreamType =
    [0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11, 0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B];

  private const int BlockAlign = 256;
  private const int PacketSize = 600;

  [Test]
  public void Wmav2_AllZeroSuperframe_SurfacesChannelWavs() {
    var asf = BuildWmaAsf(formatTag: 0x0161, streamNumber: 1, channels: 2);
    var entries = new AsfFormatDescriptor().List(new MemoryStream(asf), null);

    Assert.That(entries.Any(e => e.Kind == "Channel" && e.Name.StartsWith("streams/stream_01/")), Is.True,
      "expected decoded per-channel WAV entries for the WMA v2 stream");
    // No raw fallback blob when decoding succeeded.
    Assert.That(entries.Any(e => e.Name == "streams/stream_01.bin"), Is.False);
  }

  [Test]
  public void WmaPro_Tag_NoExtradata_FallsBackToRawStreamBlob() {
    // 0x162 with cbSize == 0 carries no decode flags / channel mask, so the WMA Pro
    // decoder cannot be constructed and the raw blob is surfaced (graceful fallback).
    var asf = BuildWmaAsf(formatTag: 0x0162, streamNumber: 1, channels: 2);
    var entries = new AsfFormatDescriptor().List(new MemoryStream(asf), null);

    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False,
      "WMA Pro without extradata must not be decoded");
    Assert.That(entries.Any(e => e.Name == "streams/stream_01.bin" && e.Kind == "Stream"), Is.True);
  }

  [Test]
  public void WmaPro_Tag_WithExtradata_AttemptsDecode_OrGracefullyFallsBack() {
    // A 0x162 stream carrying a valid 18-byte WMA Pro extradata tail and an all-zero
    // packet drives the decoder. The reference skips the first frame, so an all-zero
    // packet yields either no decoded output (then the raw blob is surfaced) or silent
    // channel WAVs — but never both, and never a non-graceful failure.
    var asf = BuildWmaProAsf(streamNumber: 1, channels: 2);
    var entries = new AsfFormatDescriptor().List(new MemoryStream(asf), null);

    var hasChannels = entries.Any(e => e.Kind == "Channel" && e.Name.StartsWith("streams/stream_01/"));
    var hasBlob = entries.Any(e => e.Name == "streams/stream_01.bin" && e.Kind == "Stream");
    Assert.That(hasChannels ^ hasBlob, Is.True,
      "exactly one of decoded channel WAVs or the raw fallback blob must be present");
  }

  // ── synthetic ASF assembly ───────────────────────────────────────────────────

  private static byte[] BuildWmaAsf(int formatTag, int streamNumber, int channels) {
    var fileProps = BuildFileProperties(PacketSize);
    var streamProps = BuildAudioStreamProperties(streamNumber, formatTag, channels,
      sampleRate: 8000, byteRate: 16000, bits: 16, blockAlign: BlockAlign);
    var header = BuildHeaderObject([WrapObject(FilePropertiesObject, fileProps),
                                    WrapObject(StreamPropertiesObject, streamProps)]);

    // One media object = one all-zero coded superframe of block_align bytes.
    var packet = BuildSinglePayloadPacket(streamNumber, new byte[BlockAlign], PacketSize);
    var data = BuildDataObject(packet);

    using var ms = new MemoryStream();
    ms.Write(header);
    ms.Write(data);
    return ms.ToArray();
  }

  private static byte[] BuildWmaProAsf(int streamNumber, int channels) {
    var fileProps = BuildFileProperties(PacketSize);
    // 18-byte WMA Pro extradata: bits-per-sample @0 = 16, channel mask @2 = 0,
    // decode flags @14 = 0 (no len-prefix, single subframe, no DRC).
    var extradata = new byte[18];
    extradata[0] = 16;
    var streamProps = BuildAudioStreamPropertiesEx(streamNumber, 0x0162, channels,
      sampleRate: 8000, byteRate: 16000, bits: 16, blockAlign: BlockAlign, extradata: extradata);
    var header = BuildHeaderObject([WrapObject(FilePropertiesObject, fileProps),
                                    WrapObject(StreamPropertiesObject, streamProps)]);

    var packet = BuildSinglePayloadPacket(streamNumber, new byte[BlockAlign], PacketSize);
    var data = BuildDataObject(packet);

    using var ms = new MemoryStream();
    ms.Write(header);
    ms.Write(data);
    return ms.ToArray();
  }

  private static byte[] BuildAudioStreamPropertiesEx(int streamNumber, int formatTag, int channels,
      int sampleRate, int byteRate, int bits, int blockAlign, byte[] extradata) {
    using var ts = new MemoryStream();
    WriteU16(ts, (ushort)formatTag);
    WriteU16(ts, (ushort)channels);
    WriteU32(ts, (uint)sampleRate);
    WriteU32(ts, (uint)byteRate);
    WriteU16(ts, (ushort)blockAlign);
    WriteU16(ts, (ushort)bits);
    WriteU16(ts, (ushort)extradata.Length); // cbSize
    ts.Write(extradata);
    var tsb = ts.ToArray();

    using var ms = new MemoryStream();
    ms.Write(AudioStreamType);
    ms.Write(new byte[16]);
    WriteU64(ms, 0);
    WriteU32(ms, (uint)tsb.Length);
    WriteU32(ms, 0);
    WriteU16(ms, (ushort)(streamNumber & 0x7F));
    WriteU32(ms, 0);
    ms.Write(tsb);
    return ms.ToArray();
  }

  private static byte[] BuildSinglePayloadPacket(int streamNumber, byte[] payload, int packetSize) {
    using var ms = new MemoryStream();
    ms.WriteByte((byte)((0b11 << 5) | (0b01 << 3))); // pktlen u32, padding u8, single payload
    ms.WriteByte((byte)((0b01 << 4) | (0b01 << 2) | 0b01));
    WriteU32(ms, (uint)packetSize);

    var replicated = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(replicated, (uint)payload.Length);
    var payloadFieldBytes = 1 + 1 + 1 + 1 + replicated.Length + payload.Length;
    var fixedHeader = 1 + 1 + 4 + 1 + 4 + 2;
    var padding = packetSize - (fixedHeader + payloadFieldBytes);

    ms.WriteByte((byte)padding);
    WriteU32(ms, 0);
    WriteU16(ms, 0);
    ms.WriteByte((byte)(streamNumber & 0x7F));
    ms.WriteByte(0);
    ms.WriteByte(0);
    ms.WriteByte((byte)replicated.Length);
    ms.Write(replicated);
    ms.Write(payload);
    for (var i = 0; i < padding; ++i) ms.WriteByte(0);
    return ms.ToArray();
  }

  private static byte[] BuildAudioStreamProperties(int streamNumber, int formatTag, int channels,
      int sampleRate, int byteRate, int bits, int blockAlign) {
    using var ts = new MemoryStream();
    WriteU16(ts, (ushort)formatTag);
    WriteU16(ts, (ushort)channels);
    WriteU32(ts, (uint)sampleRate);
    WriteU32(ts, (uint)byteRate);
    WriteU16(ts, (ushort)blockAlign);
    WriteU16(ts, (ushort)bits);
    WriteU16(ts, 0); // cbSize (no extradata → flags2 default 0)
    var tsb = ts.ToArray();

    using var ms = new MemoryStream();
    ms.Write(AudioStreamType);
    ms.Write(new byte[16]);
    WriteU64(ms, 0);
    WriteU32(ms, (uint)tsb.Length);
    WriteU32(ms, 0);
    WriteU16(ms, (ushort)(streamNumber & 0x7F));
    WriteU32(ms, 0);
    ms.Write(tsb);
    return ms.ToArray();
  }

  private static byte[] BuildFileProperties(int packetSize) {
    using var ms = new MemoryStream();
    ms.Write(new byte[16]);
    WriteU64(ms, 5000);
    WriteU64(ms, 0);
    WriteU64(ms, 1);
    WriteU64(ms, 100000000);
    WriteU64(ms, 100000000);
    WriteU64(ms, 0);
    WriteU32(ms, 2);
    WriteU32(ms, (uint)packetSize);  // min packet size
    WriteU32(ms, (uint)packetSize);  // max packet size
    WriteU32(ms, 128000);
    return ms.ToArray();
  }

  private static byte[] BuildHeaderObject(List<byte[]> children) {
    using var body = new MemoryStream();
    foreach (var c in children) body.Write(c);
    var bodyBytes = body.ToArray();
    var size = (ulong)(16 + 8 + 4 + 1 + 1 + bodyBytes.Length);
    using var ms = new MemoryStream();
    ms.Write(HeaderObject);
    WriteU64(ms, size);
    WriteU32(ms, (uint)children.Count);
    ms.WriteByte(0);
    ms.WriteByte(0);
    ms.Write(bodyBytes);
    return ms.ToArray();
  }

  private static byte[] WrapObject(byte[] guid, byte[] body) {
    var size = (ulong)(16 + 8 + body.Length);
    using var ms = new MemoryStream();
    ms.Write(guid);
    WriteU64(ms, size);
    ms.Write(body);
    return ms.ToArray();
  }

  private static byte[] BuildDataObject(byte[] packet) {
    var size = (ulong)(16 + 8 + 16 + 8 + 2 + packet.Length);
    using var ms = new MemoryStream();
    ms.Write(DataObject);
    WriteU64(ms, size);
    ms.Write(new byte[16]);
    WriteU64(ms, 1);
    WriteU16(ms, 0);
    ms.Write(packet);
    return ms.ToArray();
  }

  private static void WriteU16(MemoryStream ms, ushort v) {
    Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); ms.Write(b);
  }
  private static void WriteU32(MemoryStream ms, uint v) {
    Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b);
  }
  private static void WriteU64(MemoryStream ms, ulong v) {
    Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); ms.Write(b);
  }
}
