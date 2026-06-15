#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Reflection;

namespace Compression.Tests.Asf;

/// <summary>
/// Exercises the ASF Data Object packet depayloader against hand-crafted packets that
/// each isolate one feature of the ASF packet layout: a single complete payload, a
/// multiple-payload packet, a compressed payload carrying several length-prefixed
/// sub-payloads, and a packet whose declared padding must be trimmed. Every case asserts
/// the exact reassembled bytes per stream. The depayloader is internal, so it is reached
/// via reflection.
/// </summary>
[TestFixture]
public class AsfDepayloaderTests {

  // ── reflection bridge to the internal FileFormat.Asf.AsfDepayloader ──────────

  private static Dictionary<int, byte[]> Depayload(byte[] packets, int packetSize) {
    var asm = typeof(FileFormat.Asf.AsfFormatDescriptor).Assembly;
    var t = asm.GetType("FileFormat.Asf.AsfDepayloader")!;
    var m = t.GetMethod("Depayload", BindingFlags.Static | BindingFlags.NonPublic)!;
    var raw = m.Invoke(null, [packets, packetSize])!;

    // raw is Dictionary<int, AsfDepayloader.StreamData>; project to byte[] via ToBlob().
    var result = new Dictionary<int, byte[]>();
    var dict = (System.Collections.IDictionary)raw;
    foreach (System.Collections.DictionaryEntry e in dict) {
      var blob = (byte[])e.Value!.GetType().GetMethod("ToBlob")!.Invoke(e.Value, null)!;
      result[(int)e.Key!] = blob;
    }
    return result;
  }

  [Test]
  public void SinglePayload_ReassemblesExactBytes() {
    byte[] payload = [0x10, 0x20, 0x30, 0x40, 0x50];
    var packet = SinglePayloadPacket(streamNumber: 5, payload, packetSize: 48);

    var streams = Depayload(packet, 48);

    Assert.That(streams.ContainsKey(5), Is.True);
    Assert.That(streams[5], Is.EqualTo(payload));
  }

  [Test]
  public void MultiplePayloads_TwoStreams_ReassembleIndependently() {
    byte[] a = [0xA1, 0xA2, 0xA3];
    byte[] b = [0xB1, 0xB2, 0xB3, 0xB4];
    var packet = MultiplePayloadPacket(
      [(streamNumber: 1, a), (streamNumber: 2, b)], packetSize: 64);

    var streams = Depayload(packet, 64);

    Assert.That(streams[1], Is.EqualTo(a));
    Assert.That(streams[2], Is.EqualTo(b));
  }

  [Test]
  public void CompressedPayload_SplitsIntoSubPayloads() {
    // One compressed payload (replicated size == 1) carrying three sub-objects, each a
    // complete media object that is concatenated in order for the stream.
    byte[][] subs = [[0x01], [0x02, 0x03], [0x04, 0x05, 0x06]];
    var packet = CompressedPayloadPacket(streamNumber: 7, subs, packetSize: 64);

    var streams = Depayload(packet, 64);

    Assert.That(streams[7], Is.EqualTo(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }));
  }

  [Test]
  public void Padding_IsTrimmed_NotIncludedInStream() {
    byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
    // Large padding declared; the depayloader must not fold the padding bytes (0xFF) into
    // the elementary stream.
    var packet = SinglePayloadPacket(streamNumber: 3, payload, packetSize: 96, paddingFill: 0xFF);

    var streams = Depayload(packet, 96);

    Assert.That(streams[3], Is.EqualTo(payload));
    Assert.That(streams[3], Has.No.Member((byte)0xFF));
  }

  [Test]
  public void ErrorCorrectionPrefix_IsSkipped() {
    byte[] payload = [0x11, 0x22, 0x33];
    var packet = SinglePayloadPacket(streamNumber: 4, payload, packetSize: 48, withErrorCorrection: true);

    var streams = Depayload(packet, 48);

    Assert.That(streams[4], Is.EqualTo(payload));
  }

  // ── packet builders ──────────────────────────────────────────────────────────

  private static byte[] SinglePayloadPacket(int streamNumber, byte[] payload, int packetSize,
      byte paddingFill = 0x00, bool withErrorCorrection = false) {
    using var ms = new MemoryStream();
    var ecLen = 0;
    if (withErrorCorrection) {
      ms.WriteByte(0x82); // standard EC flag: 2 bytes of EC data
      ms.WriteByte(0x00);
      ms.WriteByte(0x00);
      ecLen = 3;
    }
    var lengthTypeFlags = (0b11 << 5) | (0b01 << 3); // pktlen u32, padding u8, single payload
    ms.WriteByte((byte)lengthTypeFlags);
    var propertyFlags = (0b01 << 4) | (0b01 << 2) | 0b01; // u8 fields
    ms.WriteByte((byte)propertyFlags);
    WriteU32(ms, (uint)packetSize);

    var replicated = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(replicated, (uint)payload.Length);

    var payloadFieldBytes = 1 + 1 + 1 + 1 + replicated.Length + payload.Length;
    var fixedHeader = ecLen + 1 + 1 + 4 + 1 + 4 + 2;
    var padding = packetSize - (fixedHeader + payloadFieldBytes);
    if (padding < 0) throw new InvalidOperationException("packet too small");

    ms.WriteByte((byte)padding);
    WriteU32(ms, 0);  // send time
    WriteU16(ms, 0);  // duration

    ms.WriteByte((byte)(streamNumber & 0x7F));
    ms.WriteByte(0);  // media object number
    ms.WriteByte(0);  // offset
    ms.WriteByte((byte)replicated.Length);
    ms.Write(replicated);
    ms.Write(payload);
    for (var i = 0; i < padding; ++i) ms.WriteByte(paddingFill);
    return ms.ToArray();
  }

  private static byte[] MultiplePayloadPacket((int streamNumber, byte[] data)[] payloads, int packetSize) {
    using var ms = new MemoryStream();
    var lengthTypeFlags = (0b11 << 5) | (0b01 << 3) | 0b01; // pktlen u32, padding u8, multiple payloads
    ms.WriteByte((byte)lengthTypeFlags);
    var propertyFlags = (0b01 << 4) | (0b01 << 2) | 0b01;
    ms.WriteByte((byte)propertyFlags);
    WriteU32(ms, (uint)packetSize);

    // payload-length type = u16 (bits 6..7 = 10), count in low 6 bits.
    var payloadFlags = (0b10 << 6) | (payloads.Length & 0x3F);

    var bodyFields = 0;
    foreach (var (_, data) in payloads)
      bodyFields += 1 + 1 + 1 + 1 + 8 + 2 + data.Length; // stream+objnum+offset+replen+rep(8)+paylen(2)+data

    var fixedHeader = 1 + 1 + 4 + 1 + 4 + 2 + 1; // ...+ payload flags byte
    var padding = packetSize - (fixedHeader + bodyFields);
    if (padding < 0) throw new InvalidOperationException("packet too small");

    ms.WriteByte((byte)padding);
    WriteU32(ms, 0);
    WriteU16(ms, 0);
    ms.WriteByte((byte)payloadFlags);

    foreach (var (streamNumber, data) in payloads) {
      var replicated = new byte[8];
      BinaryPrimitives.WriteUInt32LittleEndian(replicated, (uint)data.Length);
      ms.WriteByte((byte)(streamNumber & 0x7F));
      ms.WriteByte(0);
      ms.WriteByte(0);
      ms.WriteByte((byte)replicated.Length);
      ms.Write(replicated);
      WriteU16(ms, (ushort)data.Length); // payload length (u16)
      ms.Write(data);
    }
    for (var i = 0; i < padding; ++i) ms.WriteByte(0);
    return ms.ToArray();
  }

  private static byte[] CompressedPayloadPacket(int streamNumber, byte[][] subs, int packetSize) {
    using var ms = new MemoryStream();
    var lengthTypeFlags = (0b11 << 5) | (0b01 << 3); // pktlen u32, padding u8, single payload
    ms.WriteByte((byte)lengthTypeFlags);
    var propertyFlags = (0b01 << 4) | (0b01 << 2) | 0b01; // u8 fields; replicated len type u8
    ms.WriteByte((byte)propertyFlags);
    WriteU32(ms, (uint)packetSize);

    // Build the sub-payload block: each sub is [len u8][bytes].
    using var block = new MemoryStream();
    foreach (var s in subs) { block.WriteByte((byte)s.Length); block.Write(s); }
    var blockBytes = block.ToArray();

    var payloadFieldBytes = 1 + 1 + 1 + 1 + 1 + blockBytes.Length; // stream+objnum+presTime+replen(=1)+repByte(1)+block
    var fixedHeader = 1 + 1 + 4 + 1 + 4 + 2;
    var padding = packetSize - (fixedHeader + payloadFieldBytes);
    if (padding < 0) throw new InvalidOperationException("packet too small");

    ms.WriteByte((byte)padding);
    WriteU32(ms, 0);
    WriteU16(ms, 0);

    ms.WriteByte((byte)(streamNumber & 0x7F));
    ms.WriteByte(0);     // media object number
    ms.WriteByte(0);     // "offset" field carries presentation time for compressed payloads
    ms.WriteByte(1);     // replicated length == 1 → compressed payload
    ms.WriteByte(0);     // the single replicated byte (presentation time delta)
    ms.Write(blockBytes);
    for (var i = 0; i < padding; ++i) ms.WriteByte(0);
    return ms.ToArray();
  }

  private static void WriteU16(MemoryStream ms, ushort v) {
    Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); ms.Write(b);
  }

  private static void WriteU32(MemoryStream ms, uint v) {
    Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b);
  }
}
