#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.RealMedia;

namespace Compression.Tests.RealMedia;

[TestFixture]
public sealed class RealMediaMuxerTests {
  [Test]
  public void Ac3Mux_WritesRmfAndDemuxRestoresCanonicalWordOrder() {
    var descriptor = new RealMediaFormatDescriptor();
    var originalPackets = new[] {
      new AudioPacket([0x0B, 0x77, 0x10, 0x20, 0x30, 0x40], 1536),
      new AudioPacket([0x0B, 0x77, 0x50, 0x60, 0x70, 0x80], 1536),
    };
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("ac3", 48000, 2, Properties: new Dictionary<string, string> { ["bitrate"] = "128000" }),
      originalPackets);

    using var output = new MemoryStream();
    descriptor.Mux(output, encoded, new FormatCreateOptions());
    var rm = output.ToArray();

    Assert.That(rm.AsSpan(0, 4).ToArray(), Is.EqualTo(".RMF"u8.ToArray()));
    Assert.That(descriptor.TryDemux(new MemoryStream(rm), out var demuxed), Is.True);
    Assert.That(demuxed, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(demuxed!.Format.CodecId, Is.EqualTo("ac3"));
      Assert.That(demuxed.Format.SampleRate, Is.EqualTo(48000));
      Assert.That(demuxed.Format.Channels, Is.EqualTo(2));
      Assert.That(demuxed.Packets.Count, Is.EqualTo(2));
      Assert.That(demuxed.Packets[0].Data, Is.EqualTo(originalPackets[0].Data));
      Assert.That(demuxed.Packets[1].Data, Is.EqualTo(originalPackets[1].Data));
      Assert.That(demuxed.CodecPrivateData, Is.Not.Null);
    });

    var entries = descriptor.List(new MemoryStream(rm), null);
    Assert.That(entries.Any(entry => entry.Name == "streams/stream_00.bin" && entry.Method == "dnet"), Is.True);
    using var carried = new MemoryStream();
    descriptor.ExtractEntry(new MemoryStream(rm), "streams/stream_00.bin", carried, null);
    Assert.That(carried.ToArray(), Is.EqualTo(new byte[] {
      0x77, 0x0B, 0x20, 0x10, 0x40, 0x30,
      0x77, 0x0B, 0x60, 0x50, 0x80, 0x70,
    }));
  }

  [Test]
  public void NativeCook_DemuxThenRemux_PreservesPacketsPrivateDataAndTimestamps() {
    var privateData = BuildRealAudioV4TypeSpecific("cook", 44100, 2, 4);
    var source = BuildRmf(
      privateData,
      [(0u, new byte[] { 0x11, 0x22, 0x33 }), (23u, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD })],
      duration: 46);
    var descriptor = new RealMediaFormatDescriptor();

    Assert.That(descriptor.TryDemux(new MemoryStream(source), out var demuxed), Is.True);
    Assert.That(demuxed, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(demuxed!.Format.CodecId, Is.EqualTo("cook"));
      Assert.That(demuxed.CodecPrivateData, Is.EqualTo(privateData));
      Assert.That(demuxed.Packets.Select(packet => packet.GranulePosition), Is.EqualTo(new long?[] { 0, 23 }));
    });

    using var output = new MemoryStream();
    descriptor.Mux(output, demuxed!, new FormatCreateOptions());

    Assert.That(descriptor.TryDemux(new MemoryStream(output.ToArray()), out var remuxed), Is.True);
    Assert.That(remuxed, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(remuxed!.CodecPrivateData, Is.EqualTo(privateData));
      Assert.That(remuxed.Packets.Select(packet => packet.Data), Is.EqualTo(demuxed!.Packets.Select(packet => packet.Data)));
      Assert.That(remuxed.Packets.Select(packet => packet.GranulePosition), Is.EqualTo(new long?[] { 0, 23 }));
    });
  }

  [Test]
  public void NativeCodecWithoutPrivateData_IsRejected() {
    var descriptor = new RealMediaFormatDescriptor();
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("cook", 44100, 2),
      [new AudioPacket([0x01, 0x02, 0x03], 1024)]);

    using var output = new MemoryStream();
    Assert.That(
      () => descriptor.Mux(output, encoded, new FormatCreateOptions()),
      Throws.TypeOf<NotSupportedException>().With.Message.Contains("codec-private data"));
  }

  [Test]
  public void PacketPastUint16Length_IsRejected() {
    var descriptor = new RealMediaFormatDescriptor();
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("ac3", 48000, 2),
      [new AudioPacket(new byte[ushort.MaxValue - 12 + 1], 1536)]);

    using var output = new MemoryStream();
    Assert.That(
      () => descriptor.Mux(output, encoded, new FormatCreateOptions()),
      Throws.TypeOf<NotSupportedException>().With.Message.Contains("65523"));
  }

  private static byte[] BuildRmf(byte[] typeSpecific, (uint Timestamp, byte[] Payload)[] packets, uint duration) {
    using var rm = new MemoryStream();
    WriteChunk(rm, ".RMF", body => {
      WriteU16(body, 0);
      WriteU32(body, 0);
      WriteU32(body, 4);
    });
    WriteChunk(rm, "PROP", body => {
      WriteU16(body, 0);
      WriteU32(body, 128000);
      WriteU32(body, 128000);
      WriteU32(body, (uint)packets.Max(packet => packet.Payload.Length));
      WriteU32(body, (uint)packets.Average(packet => packet.Payload.Length));
      WriteU32(body, (uint)packets.Length);
      WriteU32(body, duration);
      WriteU32(body, 0);
      WriteU32(body, 0);
      WriteU32(body, 0);
      WriteU16(body, 1);
      WriteU16(body, 3);
    });
    WriteChunk(rm, "MDPR", body => {
      WriteU16(body, 0);
      WriteU16(body, 0);
      WriteU32(body, 128000);
      WriteU32(body, 128000);
      WriteU32(body, (uint)packets.Max(packet => packet.Payload.Length));
      WriteU32(body, (uint)packets.Average(packet => packet.Payload.Length));
      WriteU32(body, 0);
      WriteU32(body, 0);
      WriteU32(body, duration);
      WriteByteString(body, "Audio");
      WriteByteString(body, "audio/x-pn-realaudio");
      WriteU32(body, (uint)typeSpecific.Length);
      body.Write(typeSpecific);
    });
    WriteChunk(rm, "CONT", body => {
      WriteU16(body, 0);
      WriteU16(body, 0);
      WriteU16(body, 0);
      WriteU16(body, 0);
      WriteU16(body, 0);
    });
    WriteChunk(rm, "DATA", body => {
      WriteU16(body, 0);
      WriteU32(body, (uint)packets.Length);
      WriteU32(body, 0);
      foreach (var packet in packets) {
        WriteU16(body, 0);
        WriteU16(body, checked((ushort)(packet.Payload.Length + 12)));
        WriteU16(body, 0);
        WriteU32(body, packet.Timestamp);
        body.WriteByte(0);
        body.WriteByte(2);
        body.Write(packet.Payload);
      }
    });
    return rm.ToArray();
  }

  private static byte[] BuildRealAudioV4TypeSpecific(string codec, ushort sampleRate, ushort channels, ushort frameSize) {
    using var data = new MemoryStream();
    data.Write(".ra"u8);
    data.WriteByte(0xfd);
    WriteU32(data, 0x00040000);
    data.Write(".ra4"u8);
    WriteU32(data, 0);
    WriteU16(data, 4);
    WriteU32(data, 0x39);
    WriteU16(data, 0);
    WriteU32(data, frameSize);
    WriteU32(data, 0);
    WriteU32(data, 0);
    WriteU32(data, 0);
    WriteU16(data, 1);
    WriteU16(data, frameSize);
    WriteU16(data, 0);
    WriteU16(data, 0);
    WriteU16(data, sampleRate);
    WriteU32(data, 16);
    WriteU16(data, channels);
    WriteByteString(data, "Int0");
    WriteByteString(data, codec);
    return data.ToArray();
  }

  private static void WriteChunk(Stream output, string id, Action<MemoryStream> writeBody) {
    using var body = new MemoryStream();
    writeBody(body);
    output.Write(Encoding.ASCII.GetBytes(id));
    WriteU32(output, checked((uint)(body.Length + 8)));
    body.Position = 0;
    body.CopyTo(output);
  }

  private static void WriteByteString(Stream output, string value) {
    var bytes = Encoding.ASCII.GetBytes(value);
    output.WriteByte(checked((byte)bytes.Length));
    output.Write(bytes);
  }

  private static void WriteU16(Stream output, ushort value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteU32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
    output.Write(bytes);
  }
}
