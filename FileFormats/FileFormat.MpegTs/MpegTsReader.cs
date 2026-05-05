#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.MpegTs;

/// <summary>
/// Reader for MPEG-2 Transport Stream files (<c>.ts</c>, <c>.m2ts</c>, <c>.mts</c>) per
/// ISO/IEC 13818-1.
/// </summary>
/// <remarks>
/// <para>
/// A TS file is a sequence of fixed-size packets (188 bytes for plain <c>.ts</c>; the
/// reader auto-detects 192-byte BDAV-prefixed packets used by Blu-ray <c>.m2ts</c> and
/// strips the 4-byte timestamp prefix). Each packet starts with sync byte <c>0x47</c>,
/// then 3 bytes of flags+PID+continuity, then optional adaptation field, then payload.
/// </para>
/// <para>
/// The reader walks the stream, parses PAT (PID 0x0000) and PMT (PID from the PAT) once,
/// and for every other PID concatenates the payload portions of all packets to produce
/// the reassembled elementary-stream bytes. PES header parsing within those streams is
/// out of scope — callers that want the raw PES will get raw PES.
/// </para>
/// <para>Skipped/intentional simplifications:</para>
/// <list type="bullet">
///   <item>Only the first PAT is honored; subsequent PAT updates are ignored.</item>
///   <item>The first PMT per program PID is honored; updates are ignored.</item>
///   <item>Pointer-field handling on PSI sections assumes payload-unit-start packets have a single section starting at offset (1+pointer) — multi-section concatenation is not supported.</item>
///   <item>Scrambled packets (transport_scrambling_control != 0) are still concatenated as-is; descrambling is out of scope.</item>
///   <item>Adaptation-field-only packets (adaptation_field_control == 2) contribute zero payload, as expected.</item>
/// </list>
/// </remarks>
public sealed class MpegTsReader {

  public const byte SyncByte = 0x47;
  public const int PacketSize = 188;
  public const int M2tsPacketSize = 192; // Blu-ray BDAV: 4-byte timestamp prefix + 188-byte packet
  public const int PatPid = 0x0000;
  public const int NullPid = 0x1FFF;

  /// <summary>
  /// Maps the 8-bit stream_type value from a PMT entry to a short identifier used in
  /// emitted entry filenames (e.g. <c>"h264"</c>).
  /// </summary>
  public static string StreamTypeName(byte type) => type switch {
    0x01 => "mpeg1video",
    0x02 => "mpeg2video",
    0x03 => "mpeg1audio",
    0x04 => "mpeg2audio",
    0x06 => "private",      // commonly DVB subtitles / teletext / AC3
    0x0F => "aac_adts",
    0x10 => "mpeg4video",
    0x11 => "aac_latm",
    0x15 => "metadata",
    0x1B => "h264",
    0x1C => "aac_raw",
    0x21 => "jpeg2000",
    0x24 => "h265",
    0x25 => "h265_temporal",
    0x80 => "lpcm_or_ac3",  // BD: LPCM in some muxes, AC3 in ATSC
    0x81 => "ac3",
    0x82 => "dts",
    0x83 => "truehd",
    0x84 => "ac3_plus",
    0x85 => "dts_hd",
    0x86 => "dts_hd_master",
    _ => $"st{type:X2}",
  };

  /// <summary>One detected elementary stream within the TS file.</summary>
  public sealed record ElementaryStream(int Pid, byte StreamType, int ProgramNumber, byte[] Payload);

  /// <summary>One program from the Program Association Table.</summary>
  public sealed record Program(int ProgramNumber, int PmtPid);

  /// <summary>Result of parsing a TS file.</summary>
  public sealed record TransportStream(
    int PacketCount,
    int PacketSizeUsed,
    IReadOnlyList<Program> Programs,
    IReadOnlyList<ElementaryStream> Streams);

  /// <summary>
  /// Parses a complete TS file. Auto-detects 188 vs 192 byte packet stride from the
  /// position of the second sync byte.
  /// </summary>
  public static TransportStream Read(ReadOnlySpan<byte> data) {
    var stride = DetectStride(data);
    var prefix = stride == M2tsPacketSize ? 4 : 0;

    if (data.Length < stride) throw new InvalidDataException("MPEG-TS: file shorter than one packet.");

    // PMT PIDs discovered from the PAT; we collect their payloads to parse the PMT later.
    var pmtPidsByProgramNumber = new Dictionary<int, int>();
    var pmtBuffers = new Dictionary<int, MemoryStream>(); // pid → assembled PSI bytes (incomplete sections still ok for first-section-only parse)
    var streamPayloads = new Dictionary<int, MemoryStream>(); // pid → concatenated PES bytes
    var programByPid = new Dictionary<int, int>();          // pid → program number
    var streamTypeByPid = new Dictionary<int, byte>();      // pid → stream_type

    var patSeen = false;
    var packetCount = 0;
    for (var off = 0; off + stride <= data.Length; off += stride) {
      var pkt = data.Slice(off + prefix, PacketSize);
      if (pkt[0] != SyncByte) continue; // resync would be nicer but not critical for in-spec files
      packetCount++;

      var b1 = pkt[1];
      var b2 = pkt[2];
      var b3 = pkt[3];
      var payloadUnitStart = (b1 & 0x40) != 0;
      var pid = ((b1 & 0x1F) << 8) | b2;
      var afc = (b3 >> 4) & 0x03; // adaptation_field_control: 0=reserved 1=payload only 2=adapt only 3=both

      if (pid == NullPid) continue;
      if (afc == 0 || afc == 2) continue; // no payload

      var payloadStart = 4;
      if (afc == 3) {
        // adaptation field present; first byte is its length
        var afLen = pkt[4];
        payloadStart = 5 + afLen;
        if (payloadStart >= PacketSize) continue;
      }
      var payload = pkt[payloadStart..];

      if (pid == PatPid) {
        if (patSeen) continue;
        // PSI: first byte is pointer_field for payload_unit_start packets.
        if (!payloadUnitStart) continue;
        var pointer = payload[0];
        if (1 + pointer >= payload.Length) continue;
        ParsePat(payload[(1 + pointer)..], pmtPidsByProgramNumber);
        patSeen = pmtPidsByProgramNumber.Count > 0;
        continue;
      }

      // Is this the PMT of a program?
      var matchedProgram = -1;
      foreach (var kv in pmtPidsByProgramNumber)
        if (kv.Value == pid) { matchedProgram = kv.Key; break; }

      if (matchedProgram >= 0) {
        // Only parse the first PMT we see for this program.
        if (!pmtBuffers.ContainsKey(pid)) {
          if (!payloadUnitStart) continue;
          var pointer = payload[0];
          if (1 + pointer >= payload.Length) continue;
          var sectionStart = 1 + pointer;
          var ms = new MemoryStream();
          ms.Write(payload[sectionStart..]);
          pmtBuffers[pid] = ms;
          ParsePmt(ms.GetBuffer().AsSpan(0, (int)ms.Length), matchedProgram, pid, streamTypeByPid, programByPid);
        }
        continue;
      }

      // Elementary-stream payload — accumulate.
      if (!streamPayloads.TryGetValue(pid, out var sb)) {
        sb = new MemoryStream();
        streamPayloads[pid] = sb;
      }
      sb.Write(payload);
    }

    var programs = pmtPidsByProgramNumber
      .Select(kv => new Program(kv.Key, kv.Value))
      .OrderBy(p => p.ProgramNumber)
      .ToList();

    var streams = streamPayloads
      .Select(kv => new ElementaryStream(
        Pid: kv.Key,
        StreamType: streamTypeByPid.TryGetValue(kv.Key, out var st) ? st : (byte)0,
        ProgramNumber: programByPid.TryGetValue(kv.Key, out var prog) ? prog : 0,
        Payload: kv.Value.ToArray()))
      .OrderBy(s => s.Pid)
      .ToList();

    return new TransportStream(packetCount, stride, programs, streams);
  }

  /// <summary>Detects whether the file uses 188- or 192-byte stride.</summary>
  private static int DetectStride(ReadOnlySpan<byte> data) {
    if (data.Length >= 1 && data[0] == SyncByte) {
      if (data.Length >= PacketSize + 1 && data[PacketSize] == SyncByte) return PacketSize;
    }
    if (data.Length >= 4 && data[4] == SyncByte) {
      if (data.Length >= M2tsPacketSize + 4 + 1 && data[M2tsPacketSize + 4] == SyncByte) return M2tsPacketSize;
    }
    throw new InvalidDataException("MPEG-TS: no sync byte at offset 0/188/192 — not a transport stream.");
  }

  /// <summary>Parses a PAT section: extracts program_number → PMT PID pairs.</summary>
  private static void ParsePat(ReadOnlySpan<byte> section, Dictionary<int, int> programs) {
    if (section.Length < 8) return;
    // table_id(1) section_syntax_indicator+section_length(2) ts_id(2) version+cni(1) sec_num(1) last_sec_num(1)
    var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
    // body excluding the 4-byte CRC at the end; first 5 bytes after the 3-byte header are PSI metadata.
    var bodyStart = 8;
    var bodyEnd = 3 + sectionLength - 4;
    if (bodyEnd > section.Length) bodyEnd = section.Length;
    for (var p = bodyStart; p + 4 <= bodyEnd; p += 4) {
      var programNumber = BinaryPrimitives.ReadUInt16BigEndian(section[p..]);
      var pid = ((section[p + 2] & 0x1F) << 8) | section[p + 3];
      if (programNumber == 0) continue; // network PID, not a program
      programs[programNumber] = pid;
    }
  }

  /// <summary>Parses a PMT section: extracts elementary-stream PIDs and their stream_type bytes.</summary>
  private static void ParsePmt(ReadOnlySpan<byte> section, int programNumber, int pmtPid,
      Dictionary<int, byte> streamTypeByPid, Dictionary<int, int> programByPid) {
    if (section.Length < 12) return;
    var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
    var bodyEnd = 3 + sectionLength - 4;
    if (bodyEnd > section.Length) bodyEnd = section.Length;

    var programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
    var p = 12 + programInfoLength;
    while (p + 5 <= bodyEnd) {
      var streamType = section[p];
      var elementaryPid = ((section[p + 1] & 0x1F) << 8) | section[p + 2];
      var esInfoLength = ((section[p + 3] & 0x0F) << 8) | section[p + 4];
      streamTypeByPid[elementaryPid] = streamType;
      programByPid[elementaryPid] = programNumber;
      p += 5 + esInfoLength;
    }
  }
}
