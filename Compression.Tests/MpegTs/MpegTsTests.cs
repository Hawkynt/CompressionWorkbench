using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.MpegTs;

namespace Compression.Tests.MpegTs;

[TestFixture]
public class MpegTsTests {

  /// <summary>
  /// Builds one 188-byte TS packet. The supplied <paramref name="payload"/> bytes are
  /// padded out to fill the packet (or truncated if too long). Adaptation field is omitted.
  /// </summary>
  private static byte[] BuildPacket(int pid, bool payloadUnitStart, byte continuity, byte[] payload) {
    var pkt = new byte[MpegTsReader.PacketSize];
    pkt[0] = MpegTsReader.SyncByte;
    pkt[1] = (byte)((payloadUnitStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
    pkt[2] = (byte)(pid & 0xFF);
    pkt[3] = (byte)(0x10 | (continuity & 0x0F)); // afc=1 (payload only)
    var copyLen = Math.Min(payload.Length, MpegTsReader.PacketSize - 4);
    Array.Copy(payload, 0, pkt, 4, copyLen);
    return pkt;
  }

  /// <summary>
  /// Builds the PSI payload bytes (pointer-field + section) for a minimal PAT advertising
  /// a single program with the given PMT PID.
  /// </summary>
  private static byte[] BuildPatPayload(int programNumber, int pmtPid) {
    // section: table_id(0x00), section_syntax(b'1'), '0', '11', section_length(12 bits),
    // ts_id(2), version+cni(1), section_num(1), last_section_num(1),
    // [program_number(2), reserved+pid(2)] x N, CRC32(4)
    var section = new byte[8 + 4 + 4]; // header(8) + 1 program(4) + crc(4) = 16
    section[0] = 0x00;
    var sectionLength = 5 + 4 + 4; // post-header bytes (5 PSI fields) + 4 program bytes + 4 CRC
    section[1] = (byte)(0xB0 | ((sectionLength >> 8) & 0x0F));
    section[2] = (byte)(sectionLength & 0xFF);
    section[3] = 0x00; section[4] = 0x01; // ts_id = 1
    section[5] = 0xC1; section[6] = 0x00; section[7] = 0x00;
    section[8] = (byte)((programNumber >> 8) & 0xFF);
    section[9] = (byte)(programNumber & 0xFF);
    section[10] = (byte)(0xE0 | ((pmtPid >> 8) & 0x1F));
    section[11] = (byte)(pmtPid & 0xFF);
    // CRC bytes left as zero — reader doesn't validate.

    // Prepend pointer_field (1 byte = 0 → section starts immediately).
    var payload = new byte[1 + section.Length];
    payload[0] = 0;
    section.CopyTo(payload.AsSpan(1));
    return payload;
  }

  /// <summary>Builds the PSI payload bytes for a minimal PMT with one stream entry.</summary>
  private static byte[] BuildPmtPayload(int programNumber, int streamPid, byte streamType) {
    // section: table_id(0x02), syntax fields, program_number(2), version+cni(1),
    // section_num(1), last(1), reserved+PCR_PID(2), reserved+program_info_length(2),
    // program_info(N), [stream_type(1), reserved+elementary_pid(2), reserved+es_info_length(2)] x N, CRC(4)
    var streamEntryLen = 5;
    var bodyLen = 9 + streamEntryLen; // PSI body bytes from byte index 3 onward (excluding CRC)
    var sectionLength = bodyLen + 4;  // include CRC
    var section = new byte[3 + bodyLen + 4];
    section[0] = 0x02;
    section[1] = (byte)(0xB0 | ((sectionLength >> 8) & 0x0F));
    section[2] = (byte)(sectionLength & 0xFF);
    section[3] = (byte)((programNumber >> 8) & 0xFF);
    section[4] = (byte)(programNumber & 0xFF);
    section[5] = 0xC1; section[6] = 0x00; section[7] = 0x00;
    section[8] = 0xE1; section[9] = 0x00;     // PCR_PID = 0x100
    section[10] = 0xF0; section[11] = 0x00;   // program_info_length = 0
    section[12] = streamType;
    section[13] = (byte)(0xE0 | ((streamPid >> 8) & 0x1F));
    section[14] = (byte)(streamPid & 0xFF);
    section[15] = 0xF0; section[16] = 0x00;   // es_info_length = 0
    // CRC remains zero.

    var payload = new byte[1 + section.Length];
    payload[0] = 0;
    section.CopyTo(payload.AsSpan(1));
    return payload;
  }

  /// <summary>
  /// Builds a minimal TS containing PAT (PID 0), PMT (PID 0x100), and N data packets on PID 0x200
  /// each carrying a 4-byte payload (continuity 0, 1, 2, ...).
  /// </summary>
  private static byte[] BuildMinimalTs(int videoPackets, byte streamType = 0x1B) {
    using var ms = new MemoryStream();
    ms.Write(BuildPacket(MpegTsReader.PatPid, payloadUnitStart: true, continuity: 0,
      payload: BuildPatPayload(programNumber: 1, pmtPid: 0x100)));
    ms.Write(BuildPacket(0x100, payloadUnitStart: true, continuity: 0,
      payload: BuildPmtPayload(programNumber: 1, streamPid: 0x200, streamType: streamType)));
    for (var i = 0; i < videoPackets; i++)
      ms.Write(BuildPacket(0x200, payloadUnitStart: i == 0, continuity: (byte)i,
        payload: [0xAA, 0xBB, (byte)i, 0xDD]));
    return ms.ToArray();
  }

  private static byte[] BuildPes(byte streamId, long pts90Khz, int payloadLength, byte seed) {
    var packetLength = checked(payloadLength + 8);
    if (packetLength > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(payloadLength));

    var result = new byte[14 + payloadLength];
    result[0] = 0x00;
    result[1] = 0x00;
    result[2] = 0x01;
    result[3] = streamId;
    BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), (ushort)packetLength);
    result[6] = 0x80; // MPEG-2 PES optional-header marker bits
    result[7] = 0x80; // PTS only
    result[8] = 5;
    WritePts(result.AsSpan(9, 5), pts90Khz);
    for (var i = 0; i < payloadLength; ++i)
      result[14 + i] = (byte)(seed + i * 17);
    return result;
  }

  private static void WritePts(Span<byte> destination, long pts90Khz) {
    var pts = pts90Khz & ((1L << 33) - 1);
    destination[0] = (byte)(0x20 | (((pts >> 30) & 0x07) << 1) | 1);
    destination[1] = (byte)(pts >> 22);
    destination[2] = (byte)((((pts >> 15) & 0x7F) << 1) | 1);
    destination[3] = (byte)(pts >> 7);
    destination[4] = (byte)(((pts & 0x7F) << 1) | 1);
  }

  private static byte[] Join(params byte[][] parts) => parts.SelectMany(static part => part).ToArray();

  private static byte[] BuildMuxMetadata(
      int packetSize,
      int videoSecondOffset,
      int audioSecondOffset,
      bool includeAudio = true) {
    var lines = new List<string> {
      "[mpegts]",
      $"packet_size = {packetSize}" + (packetSize == MpegTsReader.M2tsPacketSize ? " (m2ts)" : ""),
      "program 1 -> PMT PID 0x1000",
      "stream PID 0x0200 type 0x1B (h264) program 1 bytes 0",
      videoSecondOffset > 0 ? $"pusi_offsets 0x0200 = 0,{videoSecondOffset}" : "pusi_offsets 0x0200 = 0",
    };
    if (includeAudio) {
      lines.Add("stream PID 0x0201 type 0x0F (aac_adts) program 1 bytes 0");
      lines.Add(audioSecondOffset > 0 ? $"pusi_offsets 0x0201 = 0,{audioSecondOffset}" : "pusi_offsets 0x0201 = 0");
      lines.Add(videoSecondOffset > 0 && audioSecondOffset > 0
        ? "pusi_order = 0x0200,0x0201,0x0200,0x0201"
        : "pusi_order = 0x0200,0x0201");
    } else {
      lines.Add(videoSecondOffset > 0 ? "pusi_order = 0x0200,0x0200" : "pusi_order = 0x0200");
    }
    return Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
  }

  private static int PacketPid(ReadOnlySpan<byte> packet)
    => ((packet[1] & 0x1F) << 8) | packet[2];

  private static byte[] ExtractPsiSection(ReadOnlySpan<byte> packet) {
    var afc = (packet[3] >> 4) & 0x03;
    var payloadStart = afc == 3 ? 5 + packet[4] : 4;
    var pointer = packet[payloadStart];
    var sectionStart = payloadStart + 1 + pointer;
    var sectionLength = ((packet[sectionStart + 1] & 0x0F) << 8) | packet[sectionStart + 2];
    return packet.Slice(sectionStart, 3 + sectionLength).ToArray();
  }

  private static uint Mpeg2Crc(ReadOnlySpan<byte> data) {
    const uint polynomial = 0x04C11DB7;
    var crc = uint.MaxValue;
    foreach (var value in data) {
      crc ^= (uint)value << 24;
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ polynomial : crc << 1;
    }
    return crc;
  }

  [Test, Category("HappyPath")]
  public void Read_DetectsPatPmtAndElementaryStream() {
    var data = BuildMinimalTs(videoPackets: 5, streamType: 0x1B);
    var ts = MpegTsReader.Read(data);
    Assert.That(ts.PacketSizeUsed, Is.EqualTo(MpegTsReader.PacketSize));
    Assert.That(ts.Programs, Has.Count.EqualTo(1));
    Assert.That(ts.Programs[0].PmtPid, Is.EqualTo(0x100));
    Assert.That(ts.Streams, Has.Count.EqualTo(1));
    Assert.That(ts.Streams[0].Pid, Is.EqualTo(0x200));
    Assert.That(ts.Streams[0].StreamType, Is.EqualTo((byte)0x1B));
    // 5 packets × 184 bytes payload each (no AF) = 920 bytes.
    Assert.That(ts.Streams[0].Payload, Has.Length.EqualTo(5 * (MpegTsReader.PacketSize - 4)));
  }

  [Test, Category("HappyPath")]
  public void StreamTypeName_KnownValues_MapCorrectly() {
    Assert.That(MpegTsReader.StreamTypeName(0x1B), Is.EqualTo("h264"));
    Assert.That(MpegTsReader.StreamTypeName(0x24), Is.EqualTo("h265"));
    Assert.That(MpegTsReader.StreamTypeName(0x0F), Is.EqualTo("aac_adts"));
    Assert.That(MpegTsReader.StreamTypeName(0x81), Is.EqualTo("ac3"));
    Assert.That(MpegTsReader.StreamTypeName(0xFE), Does.StartWith("st"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_NamesEntriesByPidAndType() {
    var data = BuildMinimalTs(videoPackets: 2, streamType: 0x1B);
    using var ms = new MemoryStream(data);
    var entries = new MpegTsFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "stream_0200_h264.bin"), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_WritesPerStreamFiles() {
    var data = BuildMinimalTs(videoPackets: 3, streamType: 0x0F);
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new MpegTsFormatDescriptor().Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "stream_0200_aac_adts.bin")), Is.True);
      Assert.That(new FileInfo(Path.Combine(tmp, "stream_0200_aac_adts.bin")).Length,
        Is.EqualTo(3 * (MpegTsReader.PacketSize - 4)));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTripsPesBoundariesAndInterleave() {
    var video0 = BuildPes(0xE0, 90_000, 240, 0x10);
    var video1 = BuildPes(0xE0, 180_000, 37, 0x20);
    var audio0 = BuildPes(0xC0, 90_000, 61, 0x30);
    var audio1 = BuildPes(0xC0, 180_000, 83, 0x40);
    var video = Join(video0, video1);
    var audio = Join(audio0, audio1);
    var metadata = BuildMuxMetadata(MpegTsReader.PacketSize, video0.Length, audio0.Length);

    var descriptor = new MpegTsFormatDescriptor();
    using var output = new MemoryStream();
    descriptor.Create(output, [
      ArchiveInputInfo.InMemory("metadata.ini", metadata),
      ArchiveInputInfo.InMemory("stream_0200_h264.bin", video),
      ArchiveInputInfo.InMemory("stream_0201_aac_adts.bin", audio),
    ], new FormatCreateOptions());

    var bytes = output.ToArray();
    Assert.That(bytes.Length, Is.GreaterThan(0));
    Assert.That(bytes.Length % MpegTsReader.PacketSize, Is.Zero);

    var ts = MpegTsReader.Read(bytes);
    Assert.That(ts.Programs, Has.Count.EqualTo(1));
    Assert.That(ts.Streams, Has.Count.EqualTo(2));
    var videoRead = ts.Streams.Single(stream => stream.Pid == 0x0200);
    var audioRead = ts.Streams.Single(stream => stream.Pid == 0x0201);
    Assert.That(videoRead.StreamType, Is.EqualTo((byte)0x1B));
    Assert.That(audioRead.StreamType, Is.EqualTo((byte)0x0F));
    Assert.That(videoRead.Payload, Is.EqualTo(video));
    Assert.That(audioRead.Payload, Is.EqualTo(audio));
    Assert.That(videoRead.PayloadUnitStarts, Is.EqualTo(new[] { 0, video0.Length }));
    Assert.That(audioRead.PayloadUnitStarts, Is.EqualTo(new[] { 0, audio0.Length }));
    Assert.That(ts.PayloadUnitOrder, Is.EqualTo(new[] { 0x0200, 0x0201, 0x0200, 0x0201 }));

    var packetCount = bytes.Length / MpegTsReader.PacketSize;
    var previousContinuity = new Dictionary<int, int>();
    var videoPcrStarts = 0;
    for (var packetIndex = 0; packetIndex < packetCount; ++packetIndex) {
      var packet = bytes.AsSpan(packetIndex * MpegTsReader.PacketSize, MpegTsReader.PacketSize);
      Assert.That(packet[0], Is.EqualTo(MpegTsReader.SyncByte));
      var pid = PacketPid(packet);
      var continuity = packet[3] & 0x0F;
      if (previousContinuity.TryGetValue(pid, out var previous))
        Assert.That(continuity, Is.EqualTo((previous + 1) & 0x0F), $"PID 0x{pid:X4} continuity at packet {packetIndex}");
      previousContinuity[pid] = continuity;

      if (pid == 0x0200 && (packet[1] & 0x40) != 0) {
        var afc = (packet[3] >> 4) & 0x03;
        if (afc == 3 && packet[4] >= 7 && (packet[5] & 0x10) != 0)
          ++videoPcrStarts;
      }
    }
    Assert.That(videoPcrStarts, Is.EqualTo(2), "Each timestamped video PES start should carry PCR on the chosen PCR PID.");

    var patPacket = bytes.AsSpan(0, MpegTsReader.PacketSize);
    Assert.That(PacketPid(patPacket), Is.EqualTo(MpegTsReader.PatPid));
    Assert.That(Mpeg2Crc(ExtractPsiSection(patPacket)), Is.Zero, "PAT CRC remainder");

    var pmtPid = ts.Programs[0].PmtPid;
    ReadOnlySpan<byte> pmtPacket = default;
    for (var packetIndex = 0; packetIndex < packetCount; ++packetIndex) {
      var packet = bytes.AsSpan(packetIndex * MpegTsReader.PacketSize, MpegTsReader.PacketSize);
      if (PacketPid(packet) != pmtPid) continue;
      pmtPacket = packet;
      break;
    }
    Assert.That(pmtPacket.IsEmpty, Is.False);
    Assert.That(Mpeg2Crc(ExtractPsiSection(pmtPacket)), Is.Zero, "PMT CRC remainder");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_EmitsM2tsFramingWhenMetadataRequests192BytePackets() {
    var pes = BuildPes(0xE0, 90_000, 211, 0x51);
    var metadata = BuildMuxMetadata(MpegTsReader.M2tsPacketSize, 0, 0, includeAudio: false);
    var descriptor = new MpegTsFormatDescriptor();

    using var output = new MemoryStream();
    descriptor.Create(output, [
      ArchiveInputInfo.InMemory("metadata.ini", metadata),
      ArchiveInputInfo.InMemory("stream_0200_h264.bin", pes),
    ], new FormatCreateOptions());

    var bytes = output.ToArray();
    Assert.That(bytes.Length % MpegTsReader.M2tsPacketSize, Is.Zero);
    for (var offset = 0; offset < bytes.Length; offset += MpegTsReader.M2tsPacketSize)
      Assert.That(bytes[offset + 4], Is.EqualTo(MpegTsReader.SyncByte));

    var ts = MpegTsReader.Read(bytes);
    Assert.That(ts.PacketSizeUsed, Is.EqualTo(MpegTsReader.M2tsPacketSize));
    Assert.That(ts.Streams.Single(stream => stream.Pid == 0x0200).Payload, Is.EqualTo(pes));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Modify_RebuildCanReplaceAndRemovePesStream() {
    var first = BuildPes(0xE0, 90_000, 180, 0x61);
    var second = BuildPes(0xE0, 180_000, 90, 0x62);
    var original = Join(first, second);
    var metadata = BuildMuxMetadata(MpegTsReader.PacketSize, first.Length, 0, includeAudio: false);
    var descriptor = new MpegTsFormatDescriptor();
    var modifier = (IArchiveModifiable)descriptor;

    using var archive = new MemoryStream();
    descriptor.Create(archive, [
      ArchiveInputInfo.InMemory("metadata.ini", metadata),
      ArchiveInputInfo.InMemory("stream_0200_h264.bin", original),
    ], new FormatCreateOptions());

    // Deliberately shorter than the stale second PUSI offset retained in metadata.ini;
    // the remuxer must discard that stale boundary map and infer the replacement PES.
    var replacement = BuildPes(0xE0, 270_000, 31, 0x71);
    archive.Position = 0;
    modifier.Add(archive, [ArchiveInputInfo.InMemory("stream_0200_h264.bin", replacement)]);

    archive.Position = 0;
    using (var extracted = new MemoryStream()) {
      descriptor.ExtractEntry(archive, "stream_0200_h264.bin", extracted, null);
      Assert.That(extracted.ToArray(), Is.EqualTo(replacement));
    }

    archive.Position = 0;
    modifier.Remove(archive, ["stream_0200_h264.bin"]);
    archive.Position = 0;
    var entries = descriptor.List(archive, null);
    Assert.That(entries.Select(entry => entry.Name), Is.EqualTo(new[] { "metadata.ini" }));
    Assert.That(((IArchivePurgeable)descriptor).CanPurgeToEmpty, Is.False);
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_Create_RejectsArbitraryPseudoArchiveInputs() {
    var descriptor = new MpegTsFormatDescriptor();
    Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("stream_0200_h264.bin", [0, 0, 1, 0xE0]), out _), Is.True);
    Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("metadata.ini", []), out _), Is.True);
    Assert.That(descriptor.CanAccept(ArchiveInputInfo.InMemory("payload.bin", [1, 2, 3]), out var reason), Is.False);
    Assert.That(reason, Does.Contain("stream_XXXX"));
    Assert.That(descriptor.CanAccept(new ArchiveInputInfo("", "folder/", IsDirectory: true), out _), Is.False);

    using var output = new MemoryStream();
    Assert.That(
      () => descriptor.Create(output, [ArchiveInputInfo.InMemory("payload.bin", [1, 2, 3])], new FormatCreateOptions()),
      Throws.InstanceOf<ArgumentException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_NoSyncByte_Throws() {
    var data = new byte[200]; // all zeros
    Assert.That(() => MpegTsReader.Read(data), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_RejectsNonTsData() {
    var data = new byte[600];
    data[0] = 0x47; // sync byte at 0 only — no recurrence at 188 or 376
    using var ms = new MemoryStream(data);
    Assert.That(() => new MpegTsFormatDescriptor().List(ms, null),
      Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Read_NullPidPackets_AreIgnored() {
    using var ms = new MemoryStream();
    ms.Write(BuildPacket(MpegTsReader.PatPid, payloadUnitStart: true, continuity: 0,
      payload: BuildPatPayload(programNumber: 1, pmtPid: 0x100)));
    ms.Write(BuildPacket(0x100, payloadUnitStart: true, continuity: 0,
      payload: BuildPmtPayload(programNumber: 1, streamPid: 0x200, streamType: 0x1B)));
    // Null packets between data packets — should not appear as a stream.
    ms.Write(BuildPacket(MpegTsReader.NullPid, payloadUnitStart: false, continuity: 0, payload: [0xFF]));
    ms.Write(BuildPacket(0x200, payloadUnitStart: true, continuity: 0, payload: [0xAA]));

    var ts = MpegTsReader.Read(ms.ToArray());
    Assert.That(ts.Streams.Any(s => s.Pid == MpegTsReader.NullPid), Is.False);
    Assert.That(ts.Streams.Any(s => s.Pid == 0x200), Is.True);
  }
}
