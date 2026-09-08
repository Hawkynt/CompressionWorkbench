using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.MpegPs;

namespace Compression.Tests.MpegPs;

[TestFixture]
public class MpegPsTests {

  /// <summary>MPEG-2 pack header: start code, SCR/mux-rate bytes with markers, no stuffing.</summary>
  private static byte[] Mpeg2Pack() => [
    0x00, 0x00, 0x01, 0xBA,
    0x44, 0x00, 0x04, 0x00, 0x04, 0x01, // '01' marker + SCR
    0x01, 0x89, 0xC3,                   // program_mux_rate + markers
    0xF8,                               // reserved + pack_stuffing_length = 0
  ];

  /// <summary>MPEG-1 pack header: start code, '0010' SCR marker, mux rate.</summary>
  private static byte[] Mpeg1Pack() => [
    0x00, 0x00, 0x01, 0xBA,
    0x21, 0x00, 0x01, 0x00, 0x01, // '0010' + SCR
    0x80, 0x1B, 0x93,             // mux_rate
  ];

  private static byte[] Timestamp(long pts, int prefix) => [
    (byte)((prefix << 4) | (int)(((pts >> 30) & 0x07) << 1) | 1),
    (byte)((pts >> 22) & 0xFF),
    (byte)((((pts >> 15) & 0x7F) << 1) | 1),
    (byte)((pts >> 7) & 0xFF),
    (byte)(((pts & 0x7F) << 1) | 1),
  ];

  /// <summary>MPEG-2 PES packet with a PTS and the given payload.</summary>
  private static byte[] Mpeg2Pes(byte streamId, long pts, byte[] payload) {
    var header = new byte[] { 0x80, 0x80, 0x05 };
    var ts = Timestamp(pts, 0x2);
    var length = header.Length + ts.Length + payload.Length;
    var pkt = new byte[6 + length];
    pkt[0] = 0; pkt[1] = 0; pkt[2] = 1; pkt[3] = streamId;
    BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(4), (ushort)length);
    header.CopyTo(pkt, 6);
    ts.CopyTo(pkt, 9);
    payload.CopyTo(pkt, 14);
    return pkt;
  }

  /// <summary>MPEG-1 packet: two stuffing bytes, STD buffer, PTS, payload.</summary>
  private static byte[] Mpeg1Pes(byte streamId, long pts, byte[] payload) {
    var head = new byte[] { 0xFF, 0xFF, 0x40, 0x00 };
    var ts = Timestamp(pts, 0x2);
    var length = head.Length + ts.Length + payload.Length;
    var pkt = new byte[6 + length];
    pkt[0] = 0; pkt[1] = 0; pkt[2] = 1; pkt[3] = streamId;
    BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(4), (ushort)length);
    head.CopyTo(pkt, 6);
    ts.CopyTo(pkt, 10);
    payload.CopyTo(pkt, 15);
    return pkt;
  }

  private static byte[] Padding(int count) {
    var pkt = new byte[6 + count];
    pkt[2] = 1; pkt[3] = 0xBE;
    BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(4), (ushort)count);
    Array.Fill(pkt, (byte)0xFF, 6, count);
    return pkt;
  }

  private static byte[] Concat(params byte[][] parts) {
    using var ms = new MemoryStream();
    foreach (var p in parts) ms.Write(p);
    return ms.ToArray();
  }

  private static readonly byte[] VideoA = [0x00, 0x00, 0x01, 0xB3, 0x11, 0x22, 0x33];
  private static readonly byte[] VideoB = [0x44, 0x55, 0x66, 0x77];
  private static readonly byte[] Audio = [0xFF, 0xFD, 0x90, 0x00, 0xAA];
  private static readonly byte[] Ac3 = [0x0B, 0x77, 0x12, 0x34];

  private static byte[] BuildMpeg2File() {
    // DVD-style private stream 1: substream id 0x80 (AC-3) + 3-byte substream header.
    var ac3Payload = Concat([0x80, 0x01, 0x00, 0x01], Ac3);
    return Concat(
      Mpeg2Pack(),
      Mpeg2Pes(0xE0, 90_000, VideoA),
      Mpeg2Pes(0xC0, 90_000, Audio),
      Padding(8),
      Mpeg2Pack(),
      Mpeg2Pes(0xE0, 93_000, VideoB),
      Mpeg2Pes(0xBD, 91_000, ac3Payload),
      [0x00, 0x00, 0x01, 0xB9]);
  }

  [Test, Category("HappyPath")]
  public void Read_Mpeg2_SplitsStreamsAndStripsPesHeaders() {
    var ps = MpegPsReader.Read(BuildMpeg2File());

    Assert.That(ps.MpegVersion, Is.EqualTo(2));
    Assert.That(ps.PackCount, Is.EqualTo(2));
    Assert.That(ps.PesPacketCount, Is.EqualTo(5));
    Assert.That(ps.HasProgramEnd, Is.True);
    Assert.That(ps.Streams, Has.Count.EqualTo(3));

    var video = ps.Streams.Single(s => s.StreamId == 0xE0);
    Assert.That(video.Kind, Is.EqualTo("mpeg2video"));
    Assert.That(video.PacketCount, Is.EqualTo(2));
    Assert.That(video.FirstPts, Is.EqualTo(90_000));
    Assert.That(video.LastPts, Is.EqualTo(93_000));
    Assert.That(video.Payload, Is.EqualTo(Concat(VideoA, VideoB)));

    var audio = ps.Streams.Single(s => s.StreamId == 0xC0);
    Assert.That(audio.Extension, Is.EqualTo(".mp2"));
    Assert.That(audio.Payload, Is.EqualTo(Audio));

    var ac3 = ps.Streams.Single(s => s.StreamId == 0xBD);
    Assert.That(ac3.SubstreamId, Is.EqualTo(0x80));
    Assert.That(ac3.Kind, Is.EqualTo("ac3"));
    Assert.That(ac3.Payload, Is.EqualTo(Ac3), "DVD substream header must be stripped");
    Assert.That(ac3.EntryName, Is.EqualTo("stream_BD_80_ac3.ac3"));
  }

  [Test, Category("HappyPath")]
  public void Read_Mpeg1_ParsesStuffingStdAndPts() {
    var file = Concat(Mpeg1Pack(), Mpeg1Pes(0xE0, 45_000, VideoA), Mpeg1Pes(0xC0, 45_000, Audio));
    var ps = MpegPsReader.Read(file);

    Assert.That(ps.MpegVersion, Is.EqualTo(1));
    var video = ps.Streams.Single(s => s.StreamId == 0xE0);
    Assert.That(video.Kind, Is.EqualTo("mpeg1video"));
    Assert.That(video.Extension, Is.EqualTo(".m1v"));
    Assert.That(video.FirstPts, Is.EqualTo(45_000));
    Assert.That(video.Payload, Is.EqualTo(VideoA));
    Assert.That(ps.Streams.Single(s => s.StreamId == 0xC0).Payload, Is.EqualTo(Audio));
  }

  [Test, Category("EdgeCase")]
  public void Read_UnboundedPesLength_EndsAtNextStartCode() {
    var pes = Mpeg2Pes(0xE0, 90_000, VideoB);
    BinaryPrimitives.WriteUInt16BigEndian(pes.AsSpan(4), 0);
    var file = Concat(Mpeg2Pack(), pes, Mpeg2Pack(), Mpeg2Pes(0xC0, 90_000, Audio));
    var ps = MpegPsReader.Read(file);

    Assert.That(ps.Streams.Single(s => s.StreamId == 0xE0).Payload, Is.EqualTo(VideoB));
    Assert.That(ps.Streams.Single(s => s.StreamId == 0xC0).Payload, Is.EqualTo(Audio));
  }

  [Test, Category("HappyPath")]
  public void Read_ProgramStreamMap_NamesStreamsByDeclaredType() {
    // PSM: flags, version, info_length=0, map_length=8, two 4-byte entries (H.264 on E0, AAC on C0), CRC.
    var psmBody = new byte[] {
      0x80, 0x01, 0x00, 0x00,
      0x00, 0x08,
      0x1B, 0xE0, 0x00, 0x00,
      0x0F, 0xC0, 0x00, 0x00,
      0x00, 0x00, 0x00, 0x00,
    };
    var psm = new byte[6 + psmBody.Length];
    psm[2] = 1; psm[3] = 0xBC;
    BinaryPrimitives.WriteUInt16BigEndian(psm.AsSpan(4), (ushort)psmBody.Length);
    psmBody.CopyTo(psm, 6);

    var ps = MpegPsReader.Read(Concat(Mpeg2Pack(), psm, Mpeg2Pes(0xE0, 0, VideoB), Mpeg2Pes(0xC0, 0, Audio)));
    Assert.That(ps.Streams.Single(s => s.StreamId == 0xE0).EntryName, Is.EqualTo("stream_E0_h264.h264"));
    Assert.That(ps.Streams.Single(s => s.StreamId == 0xC0).EntryName, Is.EqualTo("stream_C0_aac_adts.aac"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ReturnsMetadataAndStreams() {
    using var ms = new MemoryStream(BuildMpeg2File());
    var entries = new MpegPsFormatDescriptor().List(ms, null);

    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] {
      "metadata.ini", "stream_BD_80_ac3.ac3", "stream_C0_mpegaudio.mp2", "stream_E0_mpeg2video.m2v",
    }));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesRawElementaryStreams() {
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(BuildMpeg2File());
      new MpegPsFormatDescriptor().Extract(ms, tmp, null, null);

      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "stream_E0_mpeg2video.m2v")), Is.EqualTo(Concat(VideoA, VideoB)));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "stream_BD_80_ac3.ac3")), Is.EqualTo(Ac3));
      var metadata = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(metadata, Does.Contain("mpeg_version = 2"));
      Assert.That(metadata, Does.Contain("[stream_E0_mpeg2video.m2v]"));
      Assert.That(metadata, Does.Contain("first_pts_ms = 1000.000"));
    } finally {
      Directory.Delete(tmp, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ExtractEntry_StreamsOneEntry() {
    using var ms = new MemoryStream(BuildMpeg2File());
    using var output = new MemoryStream();
    new MpegPsFormatDescriptor().ExtractEntry(ms, "stream_C0_mpegaudio.mp2", output, null);
    Assert.That(output.ToArray(), Is.EqualTo(Audio));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesMuxAndPacketRemux() {
    var descriptor = new MpegPsFormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(descriptor, Is.InstanceOf<IAudioMuxTarget>());
    Assert.That(descriptor.SupportedMuxCodecs, Does.Contain("aac"));
    Assert.That(descriptor.SupportedMuxCodecs, Does.Contain("mp2"));
    Assert.That(descriptor.SupportedMuxCodecs, Does.Contain("mp3"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_RoundTripsElementaryStreamsAndPreservesExplicitIds() {
    var video = Concat(VideoA, VideoB);
    var inputs = new[] {
      ArchiveInputInfo.InMemory("metadata.ini", "[mpegps]\nmpeg_version = 2\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("stream_E2_mpeg2video.m2v", video),
      ArchiveInputInfo.InMemory("stream_C3_mpegaudio.mp2", Audio),
    };
    using var output = new MemoryStream();
    new MpegPsFormatDescriptor().Create(output, inputs, new FormatCreateOptions());

    var ps = MpegPsReader.Read(output.ToArray());
    Assert.That(ps.MpegVersion, Is.EqualTo(2));
    Assert.That(ps.HasProgramEnd, Is.True);
    Assert.That(ps.Streams.Single(s => s.StreamId == 0xE2).Payload, Is.EqualTo(video));
    Assert.That(ps.Streams.Single(s => s.StreamId == 0xC3).Payload, Is.EqualTo(Audio));
  }

  [Test, Category("HappyPath")]
  public void Create_ProgramStreamMapDeclaresModernCodecsAndHasValidCrc() {
    var h264 = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x64, 0x00, 0x28 };
    var aac = new byte[] { 0xFF, 0xF1, 0x4C, 0x80, 0x01, 0x5F, 0xFC, 0x11, 0x22, 0x33 };
    using var output = new MemoryStream();
    new MpegPsFormatDescriptor().Create(output, [
      ArchiveInputInfo.InMemory("video.h264", h264),
      ArchiveInputInfo.InMemory("audio.aac", aac),
    ], new FormatCreateOptions());

    var file = output.ToArray();
    var ps = MpegPsReader.Read(file);
    Assert.That(ps.Streams.Single(s => s.StreamId == 0xE0).Kind, Is.EqualTo("h264"));
    Assert.That(ps.Streams.Single(s => s.StreamId == 0xC0).Kind, Is.EqualTo("aac_adts"));

    var psmOffset = FindStartCode(file, MpegPsReader.ProgramStreamMapId);
    Assert.That(psmOffset, Is.GreaterThanOrEqualTo(0));
    var psmLength = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(psmOffset + 4, 2));
    Assert.That(Mpeg2CrcRemainder(file.AsSpan(psmOffset, 6 + psmLength)), Is.Zero,
      "PSM CRC must produce the zero remainder specified by ISO/IEC 13818-1");
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void Create_SplitsLargeElementaryStreamIntoBoundedPesPackets() {
    var payload = new byte[140_000];
    for (var i = 0; i < payload.Length; ++i)
      payload[i] = (byte)(i * 31 + 7);

    using var output = new MemoryStream();
    new MpegPsFormatDescriptor().Create(output,
      [ArchiveInputInfo.InMemory("video.m2v", payload)], new FormatCreateOptions());

    var video = MpegPsReader.Read(output.ToArray()).Streams.Single();
    Assert.That(video.PacketCount, Is.EqualTo(3));
    Assert.That(video.Payload, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Mux_Mp2_PreservesPacketsAndGeneratesPts() {
    var packet2 = new byte[] { 0xFF, 0xFD, 0x90, 0x00, 0xBB };
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("mp2", 48_000, 2, Properties: new Dictionary<string, string> {
        ["mpeg-version"] = "1",
      }),
      [new AudioPacket(Audio, 1152), new AudioPacket(packet2, 1152)]);

    using var output = new MemoryStream();
    new MpegPsFormatDescriptor().Mux(output, encoded, new FormatCreateOptions());
    var ps = MpegPsReader.Read(output.ToArray());
    var audio = ps.Streams.Single(s => s.StreamId == 0xC0);

    Assert.That(audio.Payload, Is.EqualTo(Concat(Audio, packet2)));
    Assert.That(audio.FirstPts, Is.Zero);
    Assert.That(audio.LastPts, Is.EqualTo(2160));
    Assert.That(ps.PackCount, Is.EqualTo(2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Mux_Aac_RebuildsAdtsInsideProgramStream() {
    var accessUnit = new byte[] { 0x11, 0x22, 0x33 };
    var encoded = new AudioEncodedStream(
      new AudioStreamFormat("aac", 48_000, 2, Properties: new Dictionary<string, string> {
        ["object-type"] = "2",
        ["sample-rate-index"] = "3",
        ["adts-mpeg2"] = "0",
      }),
      [new AudioPacket(accessUnit, 1024)]);

    using var output = new MemoryStream();
    new MpegPsFormatDescriptor().Mux(output, encoded, new FormatCreateOptions());
    var audio = MpegPsReader.Read(output.ToArray()).Streams.Single(s => s.StreamId == 0xC0);

    Assert.That(audio.Kind, Is.EqualTo("aac_adts"));
    Assert.That(audio.Payload, Is.EqualTo(new byte[] {
      0xFF, 0xF1, 0x4C, 0x80, 0x01, 0x5F, 0xFC, 0x11, 0x22, 0x33,
    }));
    Assert.That(audio.FirstPts, Is.Zero);
  }

  [Test, Category("ErrorHandling")]
  public void Create_RejectsPrivateStreamInputsThatCannotBeFaithfullyReauthored() {
    var descriptor = new MpegPsFormatDescriptor();
    var input = ArchiveInputInfo.InMemory("stream_BD_80_ac3.ac3", Ac3);
    Assert.That(descriptor.CanAccept(input, out var reason), Is.False);
    Assert.That(reason, Does.Contain("unsupported MPEG-PS mux input"));
  }

  [Test, Category("ErrorHandling")]
  public void Read_WithoutPackHeader_Throws() {
    Assert.Throws<InvalidDataException>(() => MpegPsReader.Read([0x00, 0x00, 0x01, 0xB3, 0x00]));
  }

  [Test, Category("EdgeCase")]
  public void Read_TruncatedPacket_KeepsWhatWasRead() {
    var full = BuildMpeg2File();
    var ps = MpegPsReader.Read(full.AsSpan(0, full.Length - 12));
    Assert.That(ps.Streams.Any(s => s.StreamId == 0xE0), Is.True);
  }

  private static int FindStartCode(ReadOnlySpan<byte> data, byte code) {
    var offset = 0;
    while ((offset = MpegPsReader.FindStartCode(data, offset)) >= 0) {
      if (data[offset + 3] == code)
        return offset;
      offset += 4;
    }
    return -1;
  }

  private static uint Mpeg2CrcRemainder(ReadOnlySpan<byte> data) {
    const uint polynomial = 0x04C11DB7;
    var crc = uint.MaxValue;
    foreach (var value in data) {
      crc ^= (uint)value << 24;
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ polynomial : crc << 1;
    }
    return crc;
  }
}
