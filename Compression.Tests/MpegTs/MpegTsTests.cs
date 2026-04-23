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
