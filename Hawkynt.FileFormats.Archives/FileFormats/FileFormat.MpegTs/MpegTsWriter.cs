#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.MpegTs;

/// <summary>
/// Clean-room MPEG-2 Transport Stream muxer for the descriptor's raw-PES representation.
/// The emitted transport syntax follows ITU-T H.222.0 / ISO/IEC 13818-1. FFmpeg's
/// LGPL-2.1+ MPEG-TS muxer is used only as an interoperability oracle; no implementation
/// code or implementation-specific structure is copied here.
/// </summary>
internal static class MpegTsWriter {
  private const int DefaultProgramNumber = 1;
  private const int FirstDefaultPmtPid = 0x1000;
  private const long TimestampWrap = 1L << 33;
  private const uint CrcPolynomial = 0x04C11DB7;
  private const int MaxProgramsPerPatSection = 42;
  private const int MaxStreamsPerPmtSection = 33;

  private sealed class Metadata {
    public int PacketSize { get; set; } = MpegTsReader.PacketSize;
    public Dictionary<int, int> Programs { get; } = [];
    public Dictionary<int, int> ProgramByPid { get; } = [];
    public Dictionary<int, byte> StreamTypeByPid { get; } = [];
    public Dictionary<int, List<int>> PayloadUnitStartsByPid { get; } = [];
    public List<int> PayloadUnitOrder { get; } = [];
  }

  private sealed record MuxStream(
    int Pid,
    byte StreamType,
    int ProgramNumber,
    byte[] Data,
    int[] PayloadUnitStarts);

  private sealed record PayloadUnit(
    MuxStream Stream,
    int Index,
    ReadOnlyMemory<byte> Data,
    long? Timestamp90Khz,
    long? OrderTimestamp90Khz);

  /// <summary>Writes a fresh TS or M2TS container from raw-PES pseudo-archive entries.</summary>
  internal static void Write(Stream output, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    if (!output.CanWrite) throw new ArgumentException("MPEG-TS: output stream is not writable.", nameof(output));

    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }

    var files = inputs.Where(static input => !input.IsDirectory).ToList();
    var metadataInputs = files
      .Where(static input => IsMetadataName(input.ArchiveName))
      .ToList();
    if (metadataInputs.Count > 1)
      throw new ArgumentException("MPEG-TS: at most one metadata.ini input is allowed.", nameof(inputs));

    var metadata = metadataInputs.Count == 0
      ? new Metadata()
      : ParseMetadata(metadataInputs[0].ReadContent());

    if (metadata.PacketSize is not (MpegTsReader.PacketSize or MpegTsReader.M2tsPacketSize))
      throw new ArgumentException(
        $"MPEG-TS: packet_size must be {MpegTsReader.PacketSize} or {MpegTsReader.M2tsPacketSize}.",
        nameof(inputs));

    var rawStreams = new List<(int Pid, byte StreamType, byte[] Data)>();
    var seenPids = new HashSet<int>();
    foreach (var input in files) {
      if (IsMetadataName(input.ArchiveName)) continue;
      if (!TryParseStreamFileName(input.ArchiveName, out var pid, out var streamType))
        throw new ArgumentException(
          $"MPEG-TS: unsupported input '{input.ArchiveName}'. Expected metadata.ini or stream_XXXX_<type>.bin.",
          nameof(inputs));
      if (!seenPids.Add(pid))
        throw new ArgumentException($"MPEG-TS: duplicate elementary PID 0x{pid:X4}.", nameof(inputs));
      if (metadata.StreamTypeByPid.TryGetValue(pid, out var declaredType) && declaredType != streamType)
        throw new ArgumentException(
          $"MPEG-TS: PID 0x{pid:X4} is type 0x{declaredType:X2} in metadata.ini but the filename says 0x{streamType:X2}.",
          nameof(inputs));
      rawStreams.Add((pid, streamType, input.ReadContent()));
    }

    var programPmtPids = BuildProgramMap(metadata, rawStreams);
    ValidatePidLayout(programPmtPids, rawStreams);

    var streams = rawStreams
      .Select(raw => {
        var programNumber = metadata.ProgramByPid.TryGetValue(raw.Pid, out var declaredProgram) && declaredProgram > 0
          ? declaredProgram
          : DefaultProgramNumber;
        var starts = DeterminePayloadUnitStarts(
          raw.Data,
          metadata.PayloadUnitStartsByPid.TryGetValue(raw.Pid, out var declaredStarts) ? declaredStarts : null);
        return new MuxStream(raw.Pid, raw.StreamType, programNumber, raw.Data, starts);
      })
      .OrderBy(static stream => stream.Pid)
      .ToList();

    var unitsByPid = streams.ToDictionary(static stream => stream.Pid, BuildPayloadUnits);
    var orderedUnits = OrderPayloadUnits(unitsByPid, metadata.PayloadUnitOrder);
    var pcrPidByProgram = ChoosePcrPids(streams, unitsByPid);

    var emitter = new PacketEmitter(output, metadata.PacketSize);
    WriteProgramTables(emitter, programPmtPids, streams, pcrPidByProgram);

    foreach (var unit in orderedUnits) {
      var hasPcr = pcrPidByProgram.TryGetValue(unit.Stream.ProgramNumber, out var pcrPid)
                   && pcrPid == unit.Stream.Pid
                   && unit.Timestamp90Khz.HasValue;
      emitter.WritePayloadUnit(
        unit.Stream.Pid,
        unit.Data.Span,
        payloadUnitStart: true,
        hasPcr ? unit.Timestamp90Khz : null);
    }

    // Repeating PSI at EOF makes short/static files friendlier to readers that begin probing late.
    WriteProgramTables(emitter, programPmtPids, streams, pcrPidByProgram);
  }

  internal static bool TryParseStreamFileName(string name, out int pid, out byte streamType) {
    pid = 0;
    streamType = 0;
    if (string.IsNullOrWhiteSpace(name)) return false;

    var leaf = Path.GetFileName(name.Replace('\\', '/'));
    if (!leaf.StartsWith("stream_", StringComparison.OrdinalIgnoreCase)
        || !leaf.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
        || leaf.Length <= 16
        || leaf[11] != '_')
      return false;

    if (!int.TryParse(leaf.AsSpan(7, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid)
        || !IsUsableElementaryPid(pid))
      return false;

    var typeName = leaf.Substring(12, leaf.Length - 16);
    return MpegTsReader.TryParseStreamTypeName(typeName, out streamType);
  }

  private static bool IsMetadataName(string name)
    => string.Equals(Path.GetFileName(name.Replace('\\', '/')), "metadata.ini", StringComparison.OrdinalIgnoreCase);

  private static bool IsUsableElementaryPid(int pid)
    => pid is >= 0x0020 and < MpegTsReader.NullPid;

  private static Metadata ParseMetadata(ReadOnlySpan<byte> bytes) {
    var metadata = new Metadata();
    var text = Encoding.UTF8.GetString(bytes);
    foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var line = rawLine.Trim();
      if (line.Length == 0 || line[0] is ';' or '#') continue;

      if (line.StartsWith("packet_size", StringComparison.OrdinalIgnoreCase)) {
        var equals = line.IndexOf('=');
        if (equals >= 0) {
          var value = line[(equals + 1)..].Trim();
          var space = value.IndexOf(' ');
          if (space >= 0) value = value[..space];
          if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var packetSize))
            metadata.PacketSize = packetSize;
        }
        continue;
      }

      if (line.StartsWith("program ", StringComparison.OrdinalIgnoreCase)) {
        var arrow = line.IndexOf("-> PMT PID", StringComparison.OrdinalIgnoreCase);
        if (arrow < 0) continue;
        var programText = line["program ".Length..arrow].Trim();
        var pidText = line[(arrow + "-> PMT PID".Length)..].Trim();
        if (int.TryParse(programText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var programNumber)
            && TryParsePid(pidText, out var pmtPid)
            && programNumber is > 0 and <= ushort.MaxValue)
          metadata.Programs[programNumber] = pmtPid;
        continue;
      }

      if (line.StartsWith("stream PID ", StringComparison.OrdinalIgnoreCase)) {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 8
            && TryParsePid(parts[2], out var pid)
            && TryParseByte(parts[4], out var streamType)
            && int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var programNumber)) {
          metadata.ProgramByPid[pid] = programNumber;
          metadata.StreamTypeByPid[pid] = streamType;
        }
        continue;
      }

      if (line.StartsWith("pusi_offsets ", StringComparison.OrdinalIgnoreCase)) {
        var equals = line.IndexOf('=');
        if (equals < 0) continue;
        var pidText = line["pusi_offsets ".Length..equals].Trim();
        if (!TryParsePid(pidText, out var pid)) continue;
        var offsets = new List<int>();
        foreach (var item in line[(equals + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
          if (int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
            offsets.Add(offset);
        metadata.PayloadUnitStartsByPid[pid] = offsets;
        continue;
      }

      if (line.StartsWith("pusi_order", StringComparison.OrdinalIgnoreCase)) {
        var equals = line.IndexOf('=');
        if (equals < 0) continue;
        foreach (var item in line[(equals + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
          if (TryParsePid(item, out var pid))
            metadata.PayloadUnitOrder.Add(pid);
      }
    }
    return metadata;
  }

  private static bool TryParsePid(ReadOnlySpan<char> text, out int pid) {
    text = text.Trim();
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
    return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid)
           && pid is >= 0 and <= MpegTsReader.NullPid;
  }

  private static bool TryParseByte(ReadOnlySpan<char> text, out byte value) {
    text = text.Trim();
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
    return byte.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
  }

  private static SortedDictionary<int, int> BuildProgramMap(
      Metadata metadata,
      IReadOnlyList<(int Pid, byte StreamType, byte[] Data)> streams) {
    var result = new SortedDictionary<int, int>();
    foreach (var (programNumber, pmtPid) in metadata.Programs) {
      if (programNumber is <= 0 or > ushort.MaxValue)
        throw new ArgumentException($"MPEG-TS: invalid program number {programNumber} in metadata.ini.");
      result[programNumber] = pmtPid;
    }

    var requiredPrograms = streams
      .Select(stream => metadata.ProgramByPid.TryGetValue(stream.Pid, out var declared) && declared > 0
        ? declared
        : DefaultProgramNumber)
      .Distinct()
      .Order();

    var occupied = streams.Select(static stream => stream.Pid).ToHashSet();
    foreach (var pmtPid in result.Values) occupied.Add(pmtPid);
    occupied.Add(MpegTsReader.PatPid);
    occupied.Add(MpegTsReader.NullPid);

    foreach (var programNumber in requiredPrograms) {
      if (programNumber > ushort.MaxValue)
        throw new ArgumentException($"MPEG-TS: invalid program number {programNumber} in metadata.ini.");
      if (result.ContainsKey(programNumber)) continue;
      var pmtPid = AllocatePmtPid(occupied);
      result[programNumber] = pmtPid;
      occupied.Add(pmtPid);
    }

    if (result.Count > MaxProgramsPerPatSection)
      throw new NotSupportedException(
        $"MPEG-TS: this writer currently supports at most {MaxProgramsPerPatSection} programs in one PAT section.");
    return result;
  }

  private static int AllocatePmtPid(HashSet<int> occupied) {
    for (var pid = FirstDefaultPmtPid; pid < MpegTsReader.NullPid; ++pid)
      if (!occupied.Contains(pid))
        return pid;
    for (var pid = 0x0020; pid < FirstDefaultPmtPid; ++pid)
      if (!occupied.Contains(pid))
        return pid;
    throw new NotSupportedException("MPEG-TS: no free PID remains for a PMT.");
  }

  private static void ValidatePidLayout(
      IReadOnlyDictionary<int, int> programPmtPids,
      IReadOnlyList<(int Pid, byte StreamType, byte[] Data)> streams) {
    var pmtPids = new HashSet<int>();
    foreach (var (programNumber, pmtPid) in programPmtPids) {
      if (programNumber is <= 0 or > ushort.MaxValue)
        throw new ArgumentException($"MPEG-TS: program number {programNumber} is out of range.");
      if (!IsUsableElementaryPid(pmtPid))
        throw new ArgumentException($"MPEG-TS: PMT PID 0x{pmtPid:X4} is reserved or out of range.");
      if (!pmtPids.Add(pmtPid))
        throw new ArgumentException($"MPEG-TS: PMT PID 0x{pmtPid:X4} is assigned to more than one program.");
    }

    foreach (var stream in streams) {
      if (pmtPids.Contains(stream.Pid))
        throw new ArgumentException($"MPEG-TS: elementary PID 0x{stream.Pid:X4} collides with a PMT PID.");
    }
  }

  private static int[] DeterminePayloadUnitStarts(byte[] data, IReadOnlyList<int>? declaredStarts) {
    if (data.Length == 0) return [];

    if (declaredStarts is { Count: > 0 }) {
      var normalized = declaredStarts
        .Where(offset => offset >= 0 && offset < data.Length)
        .Distinct()
        .Order()
        .ToList();
      // Replaced streams can leave stale offsets in metadata.ini. Treat the whole declaration as
      // stale rather than silently applying a partial boundary map to different bytes.
      var declarationIsUsable = normalized.Count == declaredStarts.Distinct().Count()
                                && normalized.Count > 0;
      if (declarationIsUsable) {
        if (normalized[0] != 0) normalized.Insert(0, 0);
        return [.. normalized];
      }
    }

    var inferred = new List<int> { 0 };
    var position = 0;
    while (position + 6 <= data.Length && IsPesStart(data.AsSpan(position))) {
      var packetLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 4, 2));
      if (packetLength == 0) break; // unbounded video PES: no reliable next boundary without metadata
      var next = position + 6 + packetLength;
      if (next >= data.Length) break;
      if (!IsPesStart(data.AsSpan(next))) break;
      inferred.Add(next);
      position = next;
    }
    return [.. inferred];
  }

  private static bool IsPesStart(ReadOnlySpan<byte> data)
    => data.Length >= 6 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01;

  private static List<PayloadUnit> BuildPayloadUnits(MuxStream stream) {
    var units = new List<PayloadUnit>(stream.PayloadUnitStarts.Length);
    long wrapOffset = 0;
    long? previousRawTimestamp = null;
    long? previousOrderTimestamp = null;

    for (var index = 0; index < stream.PayloadUnitStarts.Length; ++index) {
      var start = stream.PayloadUnitStarts[index];
      var end = index + 1 < stream.PayloadUnitStarts.Length
        ? stream.PayloadUnitStarts[index + 1]
        : stream.Data.Length;
      if (end <= start) continue;

      var data = stream.Data.AsMemory(start, end - start);
      var rawTimestamp = TryReadPesDecodeTimestamp(data.Span);
      long? timestamp = null;
      long? orderTimestamp = null;
      if (rawTimestamp.HasValue) {
        if (previousRawTimestamp.HasValue
            && previousRawTimestamp.Value - rawTimestamp.Value > TimestampWrap / 2)
          wrapOffset += TimestampWrap;
        timestamp = rawTimestamp.Value + wrapOffset;
        orderTimestamp = previousOrderTimestamp.HasValue
          ? Math.Max(previousOrderTimestamp.Value, timestamp.Value)
          : timestamp.Value;
        previousRawTimestamp = rawTimestamp;
        previousOrderTimestamp = orderTimestamp;
      }

      units.Add(new PayloadUnit(stream, index, data, timestamp, orderTimestamp));
    }
    return units;
  }

  private static List<PayloadUnit> OrderPayloadUnits(
      IReadOnlyDictionary<int, List<PayloadUnit>> unitsByPid,
      IReadOnlyList<int> declaredOrder) {
    var totalUnits = unitsByPid.Values.Sum(static units => units.Count);
    if (totalUnits == 0) return [];

    if (declaredOrder.Count == totalUnits) {
      var cursors = unitsByPid.Keys.ToDictionary(static pid => pid, static _ => 0);
      var exact = new List<PayloadUnit>(totalUnits);
      var valid = true;
      foreach (var pid in declaredOrder) {
        if (!unitsByPid.TryGetValue(pid, out var units)
            || !cursors.TryGetValue(pid, out var cursor)
            || cursor >= units.Count) {
          valid = false;
          break;
        }
        exact.Add(units[cursor]);
        cursors[pid] = cursor + 1;
      }
      if (valid && cursors.All(pair => pair.Value == unitsByPid[pair.Key].Count))
        return exact;
    }

    var all = unitsByPid.Values.SelectMany(static units => units).ToList();
    if (all.Any(static unit => unit.OrderTimestamp90Khz.HasValue)) {
      var maxTimestamp = all
        .Where(static unit => unit.OrderTimestamp90Khz.HasValue)
        .Max(static unit => unit.OrderTimestamp90Khz!.Value);
      return all
        .OrderBy(unit => unit.OrderTimestamp90Khz ?? maxTimestamp + 1 + unit.Index)
        .ThenBy(static unit => unit.Index)
        .ThenBy(static unit => unit.Stream.Pid)
        .ToList();
    }

    // With no timestamps at all, round-robin by payload-unit index rather than serializing one
    // entire PID before the next. This is deterministic and gives untimed remuxes sane interleave.
    return all
      .OrderBy(static unit => unit.Index)
      .ThenBy(static unit => unit.Stream.Pid)
      .ToList();
  }

  private static Dictionary<int, int> ChoosePcrPids(
      IReadOnlyList<MuxStream> streams,
      IReadOnlyDictionary<int, List<PayloadUnit>> unitsByPid) {
    var result = new Dictionary<int, int>();
    foreach (var group in streams.GroupBy(static stream => stream.ProgramNumber)) {
      var candidate = group
        .Where(stream => unitsByPid[stream.Pid].Any(static unit => unit.Timestamp90Khz.HasValue))
        .OrderByDescending(static stream => IsVideoStreamType(stream.StreamType))
        .ThenBy(static stream => stream.Pid)
        .FirstOrDefault();
      result[group.Key] = candidate?.Pid ?? MpegTsReader.NullPid;
    }
    return result;
  }

  private static bool IsVideoStreamType(byte streamType)
    => streamType is 0x01 or 0x02 or 0x10 or 0x1B or 0x21 or 0x24 or 0x25;

  private static long? TryReadPesDecodeTimestamp(ReadOnlySpan<byte> pes) {
    if (!IsPesStart(pes) || pes.Length < 9) return null;

    // MPEG-2 PES optional-header form.
    if ((pes[6] & 0xC0) == 0x80) {
      var flags = (pes[7] >> 6) & 0x03;
      var headerLength = pes[8];
      if (9 + headerLength > pes.Length) return null;
      return flags switch {
        0x02 when TryDecodeTimestamp(pes[9..], out var pts) => pts,
        0x03 when TryDecodeTimestamp(pes[14..], out var dts) => dts,
        _ => null,
      };
    }

    // MPEG-1 PES form: skip stuffing and optional STD_buffer_scale/size before PTS/DTS.
    var position = 6;
    while (position < pes.Length && pes[position] == 0xFF) ++position;
    if (position + 2 <= pes.Length && (pes[position] & 0xC0) == 0x40) position += 2;
    if (position >= pes.Length) return null;
    var marker = pes[position] & 0xF0;
    if (marker == 0x20 && TryDecodeTimestamp(pes[position..], out var mpeg1Pts)) return mpeg1Pts;
    if (marker == 0x30
        && position + 10 <= pes.Length
        && TryDecodeTimestamp(pes[(position + 5)..], out var mpeg1Dts))
      return mpeg1Dts;
    return null;
  }

  private static bool TryDecodeTimestamp(ReadOnlySpan<byte> data, out long timestamp) {
    timestamp = 0;
    if (data.Length < 5 || (data[0] & 1) == 0 || (data[2] & 1) == 0 || (data[4] & 1) == 0)
      return false;
    timestamp = ((long)(data[0] & 0x0E) << 29)
                | ((long)data[1] << 22)
                | ((long)(data[2] & 0xFE) << 14)
                | ((long)data[3] << 7)
                | (uint)((data[4] & 0xFE) >> 1);
    return true;
  }

  private static void WriteProgramTables(
      PacketEmitter emitter,
      IReadOnlyDictionary<int, int> programPmtPids,
      IReadOnlyList<MuxStream> streams,
      IReadOnlyDictionary<int, int> pcrPidByProgram) {
    var pat = BuildPat(programPmtPids);
    emitter.WritePayloadUnit(MpegTsReader.PatPid, BuildPsiPayload(pat), payloadUnitStart: true, pcr90Khz: null);

    foreach (var (programNumber, pmtPid) in programPmtPids) {
      var programStreams = streams.Where(stream => stream.ProgramNumber == programNumber).ToList();
      if (programStreams.Count > MaxStreamsPerPmtSection)
        throw new NotSupportedException(
          $"MPEG-TS: this writer currently supports at most {MaxStreamsPerPmtSection} streams per program in one PMT section.");
      var pcrPid = pcrPidByProgram.TryGetValue(programNumber, out var configuredPcrPid)
        ? configuredPcrPid
        : MpegTsReader.NullPid;
      var pmt = BuildPmt(programNumber, pcrPid, programStreams);
      emitter.WritePayloadUnit(pmtPid, BuildPsiPayload(pmt), payloadUnitStart: true, pcr90Khz: null);
    }
  }

  private static byte[] BuildPsiPayload(byte[] section) {
    if (section.Length + 1 > MpegTsReader.PacketSize - 4)
      throw new NotSupportedException(
        "MPEG-TS: multi-packet PSI sections are outside the current reader/writer profile.");
    var payload = new byte[section.Length + 1];
    payload[0] = 0; // pointer_field: section starts immediately
    section.CopyTo(payload.AsSpan(1));
    return payload;
  }

  private static byte[] BuildPat(IReadOnlyDictionary<int, int> programPmtPids) {
    var sectionLength = 9 + 4 * programPmtPids.Count;
    var section = new byte[3 + sectionLength];
    section[0] = 0x00;
    section[1] = (byte)(0xB0 | (sectionLength >> 8));
    section[2] = (byte)sectionLength;
    section[3] = 0x00;
    section[4] = 0x01; // transport_stream_id = 1
    section[5] = 0xC1; // version 0, current_next_indicator = 1
    section[6] = 0;
    section[7] = 0;

    var position = 8;
    foreach (var (programNumber, pmtPid) in programPmtPids) {
      BinaryPrimitives.WriteUInt16BigEndian(section.AsSpan(position, 2), (ushort)programNumber);
      section[position + 2] = (byte)(0xE0 | (pmtPid >> 8));
      section[position + 3] = (byte)pmtPid;
      position += 4;
    }
    AppendCrc(section);
    return section;
  }

  private static byte[] BuildPmt(int programNumber, int pcrPid, IReadOnlyList<MuxStream> streams) {
    var sectionLength = 13 + 5 * streams.Count;
    var section = new byte[3 + sectionLength];
    section[0] = 0x02;
    section[1] = (byte)(0xB0 | (sectionLength >> 8));
    section[2] = (byte)sectionLength;
    BinaryPrimitives.WriteUInt16BigEndian(section.AsSpan(3, 2), (ushort)programNumber);
    section[5] = 0xC1; // version 0, current_next_indicator = 1
    section[6] = 0;
    section[7] = 0;
    section[8] = (byte)(0xE0 | (pcrPid >> 8));
    section[9] = (byte)pcrPid;
    section[10] = 0xF0;
    section[11] = 0x00; // program_info_length = 0

    var position = 12;
    foreach (var stream in streams.OrderBy(static stream => stream.Pid)) {
      section[position] = stream.StreamType;
      section[position + 1] = (byte)(0xE0 | (stream.Pid >> 8));
      section[position + 2] = (byte)stream.Pid;
      section[position + 3] = 0xF0;
      section[position + 4] = 0x00; // ES_info_length = 0
      position += 5;
    }
    AppendCrc(section);
    return section;
  }

  private static void AppendCrc(Span<byte> section) {
    var data = section[..^4];
    BinaryPrimitives.WriteUInt32BigEndian(section[^4..], ComputeCrc32Mpeg2(data));
  }

  private static uint ComputeCrc32Mpeg2(ReadOnlySpan<byte> data) {
    var crc = uint.MaxValue;
    foreach (var value in data) {
      crc ^= (uint)value << 24;
      for (var bit = 0; bit < 8; ++bit)
        crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ CrcPolynomial : crc << 1;
    }
    return crc;
  }

  private sealed class PacketEmitter(Stream output, int packetSize) {
    private readonly Dictionary<int, byte> _continuityCounters = [];
    private long _packetIndex;

    internal void WritePayloadUnit(int pid, ReadOnlySpan<byte> payload, bool payloadUnitStart, long? pcr90Khz) {
      var offset = 0;
      var first = true;
      while (offset < payload.Length) {
        var includePcr = first && pcr90Khz.HasValue;
        var maxPayload = includePcr ? 176 : 184;
        var count = Math.Min(maxPayload, payload.Length - offset);
        WritePacket(
          pid,
          payload[(offset)..(offset + count)],
          payloadUnitStart && first,
          includePcr ? pcr90Khz : null);
        offset += count;
        first = false;
      }
    }

    private void WritePacket(int pid, ReadOnlySpan<byte> payload, bool payloadUnitStart, long? pcr90Khz) {
      Span<byte> packet = stackalloc byte[MpegTsReader.PacketSize];
      packet.Fill(0xFF);

      packet[0] = MpegTsReader.SyncByte;
      packet[1] = (byte)((payloadUnitStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
      packet[2] = (byte)pid;

      var continuity = this._continuityCounters.TryGetValue(pid, out var counter) ? counter : (byte)0;
      this._continuityCounters[pid] = (byte)((continuity + 1) & 0x0F);

      var useAdaptationField = pcr90Khz.HasValue || payload.Length < 184;
      packet[3] = (byte)((useAdaptationField ? 0x30 : 0x10) | continuity);

      var payloadOffset = 4;
      if (useAdaptationField) {
        var adaptationLength = 183 - payload.Length;
        if (pcr90Khz.HasValue && adaptationLength < 7)
          throw new InvalidOperationException("MPEG-TS: internal PCR adaptation-field sizing error.");
        packet[4] = (byte)adaptationLength;
        payloadOffset = 5 + adaptationLength;
        if (adaptationLength > 0) {
          packet[5] = pcr90Khz.HasValue ? (byte)0x10 : (byte)0x00;
          if (pcr90Khz.HasValue) WritePcr(packet[6..12], pcr90Khz.Value);
        }
      }

      payload.CopyTo(packet[payloadOffset..]);
      WritePhysicalPacket(packet);
    }

    private void WritePhysicalPacket(ReadOnlySpan<byte> packet) {
      if (packetSize == MpegTsReader.M2tsPacketSize) {
        Span<byte> prefix = stackalloc byte[4];
        var arrivalTimestamp = (uint)((this._packetIndex * 27_000L) & 0x3FFF_FFFF);
        BinaryPrimitives.WriteUInt32BigEndian(prefix, arrivalTimestamp); // copy_permission_indicator = 0
        output.Write(prefix);
      }
      output.Write(packet);
      ++this._packetIndex;
    }

    private static void WritePcr(Span<byte> destination, long timestamp90Khz) {
      var base90Khz = timestamp90Khz % TimestampWrap;
      if (base90Khz < 0) base90Khz += TimestampWrap;
      destination[0] = (byte)(base90Khz >> 25);
      destination[1] = (byte)(base90Khz >> 17);
      destination[2] = (byte)(base90Khz >> 9);
      destination[3] = (byte)(base90Khz >> 1);
      destination[4] = (byte)(((base90Khz & 1) << 7) | 0x7E); // reserved=111111, PCR_ext[8]=0
      destination[5] = 0; // PCR_ext[7..0] = 0
    }
  }
}
