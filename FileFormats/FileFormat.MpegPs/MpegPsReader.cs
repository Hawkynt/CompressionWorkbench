#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.MpegPs;

/// <summary>
/// Reader for MPEG program streams (<c>.mpg</c>, <c>.vob</c>, <c>.m2p</c>) per ISO/IEC 13818-1
/// §2.5 (MPEG-2) and ISO/IEC 11172-1 §2.4 (MPEG-1).
/// </summary>
/// <remarks>
/// <para>
/// A program stream is a sequence of packs. Every pack opens with a pack header
/// (start code <c>00 00 01 BA</c>, then the SCR and mux rate), optionally carries a
/// system header (<c>00 00 01 BB</c>), and is followed by PES packets
/// (<c>00 00 01</c> + stream id + 16-bit length). The reader walks those packets,
/// strips each PES header, and concatenates the remaining payload per stream so
/// every elementary stream comes out as one contiguous byte run — MPEG video as raw
/// video ES, MPEG audio as raw frames, and the DVD private stream 1 substreams
/// (AC-3, DTS, LPCM, sub-pictures) split by their substream id with the DVD-specific
/// substream header removed.
/// </para>
/// <para>What the reader deliberately does not do:</para>
/// <list type="bullet">
///   <item>Scrambled PES packets are kept as-is; descrambling is out of scope.</item>
///   <item>The program stream map is used only to name video/audio streams by their
///   declared <c>stream_type</c>; its descriptors are not interpreted.</item>
///   <item>A PES packet whose length field is zero (unbounded, legal only for video
///   in transport streams) is terminated at the next start code.</item>
/// </list>
/// </remarks>
public sealed class MpegPsReader {

  public const byte PackStartCode = 0xBA;
  public const byte SystemHeaderStartCode = 0xBB;
  public const byte ProgramEndCode = 0xB9;
  public const byte ProgramStreamMapId = 0xBC;
  public const byte PrivateStream1Id = 0xBD;
  public const byte PaddingStreamId = 0xBE;
  public const byte PrivateStream2Id = 0xBF;
  public const byte ProgramStreamDirectoryId = 0xFF;

  /// <summary>One demuxed elementary stream.</summary>
  /// <param name="StreamId">PES stream id (<c>0xC0</c>–<c>0xDF</c> audio, <c>0xE0</c>–<c>0xEF</c> video, <c>0xBD</c> private 1, <c>0xBF</c> private 2).</param>
  /// <param name="SubstreamId">DVD substream id for private stream 1, otherwise <c>-1</c>.</param>
  /// <param name="Kind">Short codec/kind identifier used in entry names (<c>mpeg2video</c>, <c>ac3</c>, …).</param>
  /// <param name="Extension">File extension that suits the raw payload.</param>
  /// <param name="PacketCount">Number of PES packets that contributed.</param>
  /// <param name="FirstPts">First presentation timestamp seen, in 90 kHz ticks.</param>
  /// <param name="LastPts">Last presentation timestamp seen, in 90 kHz ticks.</param>
  /// <param name="Payload">The concatenated payload with PES and substream headers removed.</param>
  public sealed record ElementaryStream(
    int StreamId, int SubstreamId, string Kind, string Extension,
    int PacketCount, long? FirstPts, long? LastPts, byte[] Payload) {
    public string EntryName => this.SubstreamId < 0
      ? $"stream_{this.StreamId:X2}_{this.Kind}{this.Extension}"
      : $"stream_{this.StreamId:X2}_{this.SubstreamId:X2}_{this.Kind}{this.Extension}";
  }

  /// <summary>Result of parsing a program stream.</summary>
  public sealed record ProgramStream(
    int MpegVersion,
    int PackCount,
    int PesPacketCount,
    bool HasProgramEnd,
    IReadOnlyList<ElementaryStream> Streams);

  private sealed class Accumulator {
    public int StreamId;
    public int SubstreamId = -1;
    public string Kind = "";
    public string Extension = ".bin";
    public int PacketCount;
    public long? FirstPts;
    public long? LastPts;
    public readonly MemoryStream Payload = new();
  }

  /// <summary>Parses a complete program stream held in memory.</summary>
  public static ProgramStream Read(ReadOnlySpan<byte> data) {
    if (data.Length < 4 || data[0] != 0 || data[1] != 0 || data[2] != 1 || data[3] != PackStartCode)
      throw new InvalidDataException("MPEG-PS: file does not start with a pack header (00 00 01 BA).");

    var version = 0;
    var packCount = 0;
    var pesCount = 0;
    var programEnd = false;
    var streamTypes = new Dictionary<int, byte>();
    var streams = new Dictionary<int, Accumulator>();

    var p = 0;
    while (p + 4 <= data.Length) {
      var next = FindStartCode(data, p);
      if (next < 0) break;
      p = next;
      var code = data[p + 3];

      if (code == PackStartCode) {
        if (p + 5 > data.Length) break;
        if ((data[p + 4] & 0xC0) == 0x40) {
          // MPEG-2: 14-byte header plus pack_stuffing_length trailing bytes.
          version = 2;
          if (p + 14 > data.Length) break;
          p += 14 + (data[p + 13] & 0x07);
        } else if ((data[p + 4] & 0xF0) == 0x20) {
          version = version == 0 ? 1 : version;
          p += 12;
        } else {
          p += 4;
          continue;
        }
        ++packCount;
        continue;
      }

      if (code == SystemHeaderStartCode) {
        if (p + 6 > data.Length) break;
        p += 6 + BinaryPrimitives.ReadUInt16BigEndian(data[(p + 4)..]);
        continue;
      }

      if (code == ProgramEndCode) {
        programEnd = true;
        break;
      }

      if (code < ProgramStreamMapId) {
        // A raw elementary-stream start code (picture/sequence/slice) outside a PES
        // packet: not a program-stream construct, skip the prefix and resync.
        p += 4;
        continue;
      }

      // PES packet.
      if (p + 6 > data.Length) break;
      var declared = BinaryPrimitives.ReadUInt16BigEndian(data[(p + 4)..]);
      var bodyStart = p + 6;
      int bodyEnd;
      if (declared == 0) {
        var following = FindStartCode(data, bodyStart);
        bodyEnd = following < 0 ? data.Length : following;
      } else {
        bodyEnd = Math.Min(data.Length, bodyStart + declared);
      }
      ++pesCount;
      var body = data[bodyStart..bodyEnd];
      p = bodyEnd;

      switch (code) {
        case PaddingStreamId:
        case ProgramStreamDirectoryId:
        case 0xF0: // ECM
        case 0xF1: // EMM
        case 0xF2: // DSM-CC
        case 0xF8: // ITU-T H.222.1 type E
          continue;
        case ProgramStreamMapId:
          ParseProgramStreamMap(body, streamTypes);
          continue;
        case PrivateStream2Id:
          Append(streams, code, -1, "private2", ".bin", null, body);
          continue;
      }

      var (payloadOffset, pts) = ParsePesHeader(body, version);
      if (payloadOffset < 0 || payloadOffset > body.Length) continue;
      var payload = body[payloadOffset..];

      if (code == PrivateStream1Id) {
        if (payload.Length == 0) continue;
        var sub = payload[0];
        var (kind, ext, skip) = ClassifySubstream(sub);
        if (skip > payload.Length) continue;
        Append(streams, code, sub, kind, ext, pts, payload[skip..]);
        continue;
      }

      var (k, e) = Classify(code, version, streamTypes);
      Append(streams, code, -1, k, e, pts, payload);
    }

    var result = streams.Values
      .OrderBy(a => a.StreamId).ThenBy(a => a.SubstreamId)
      .Select(a => new ElementaryStream(a.StreamId, a.SubstreamId, a.Kind, a.Extension, a.PacketCount, a.FirstPts, a.LastPts, a.Payload.ToArray()))
      .ToList();
    return new ProgramStream(version == 0 ? 2 : version, packCount, pesCount, programEnd, result);
  }

  /// <summary>Returns the offset of the next <c>00 00 01</c> prefix at or after <paramref name="from"/>, or -1.</summary>
  public static int FindStartCode(ReadOnlySpan<byte> data, int from) {
    for (var i = Math.Max(0, from); i + 3 < data.Length; ++i)
      if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
        return i;
    return -1;
  }

  /// <summary>
  /// Returns the payload offset inside a PES packet body (the bytes after the 16-bit
  /// length) and the PTS when present. <paramref name="version"/> selects between the
  /// MPEG-2 fixed header (<c>10xxxxxx</c> marker) and the MPEG-1 stuffing/STD/PTS form.
  /// </summary>
  public static (int PayloadOffset, long? Pts) ParsePesHeader(ReadOnlySpan<byte> body, int version) {
    if (body.Length == 0) return (0, null);

    if ((body[0] & 0xC0) == 0x80) {
      // MPEG-2 PES header: flags, PES_header_data_length, then optional fields.
      if (body.Length < 3) return (-1, null);
      var ptsDtsFlags = (body[1] >> 6) & 0x03;
      var headerDataLength = body[2];
      long? pts = null;
      if ((ptsDtsFlags & 0x02) != 0 && body.Length >= 8)
        pts = ReadTimestamp(body[3..]);
      return (3 + headerDataLength, pts);
    }

    // MPEG-1 PES header: stuffing bytes, optional STD buffer, timestamps.
    var q = 0;
    while (q < body.Length && body[q] == 0xFF && q < 16) ++q;
    if (q >= body.Length) return (-1, null);
    if ((body[q] & 0xC0) == 0x40) q += 2;
    if (q >= body.Length) return (-1, null);
    var marker = body[q] & 0xF0;
    if (marker == 0x20) {
      if (q + 5 > body.Length) return (-1, null);
      return (q + 5, ReadTimestamp(body[q..]));
    }
    if (marker == 0x30) {
      if (q + 10 > body.Length) return (-1, null);
      return (q + 10, ReadTimestamp(body[q..]));
    }
    if (body[q] == 0x0F) return (q + 1, null);
    if (version == 1) return (-1, null);
    // Neither form recognised: treat the whole body as payload so nothing is lost.
    return (0, null);
  }

  /// <summary>Decodes the 5-byte 33-bit timestamp field used by PTS and DTS.</summary>
  public static long ReadTimestamp(ReadOnlySpan<byte> t) =>
    ((long)((t[0] >> 1) & 0x07) << 30)
    | ((long)t[1] << 22)
    | ((long)((t[2] >> 1) & 0x7F) << 15)
    | ((long)t[3] << 7)
    | ((long)t[4] >> 1);

  private static void ParseProgramStreamMap(ReadOnlySpan<byte> body, Dictionary<int, byte> streamTypes) {
    // program_stream_map: flags(1) reserved+version(1) program_stream_info_length(2)
    // descriptors, elementary_stream_map_length(2), then 4-byte entries + descriptors, CRC32.
    if (body.Length < 6) return;
    var infoLength = BinaryPrimitives.ReadUInt16BigEndian(body[2..]);
    var q = 4 + infoLength;
    if (q + 2 > body.Length) return;
    var mapLength = BinaryPrimitives.ReadUInt16BigEndian(body[q..]);
    q += 2;
    var end = Math.Min(body.Length, q + mapLength);
    while (q + 4 <= end) {
      var streamType = body[q];
      var streamId = body[q + 1];
      var esInfoLength = BinaryPrimitives.ReadUInt16BigEndian(body[(q + 2)..]);
      streamTypes[streamId] = streamType;
      q += 4 + esInfoLength;
    }
  }

  private static (string Kind, string Extension) Classify(int streamId, int version, Dictionary<int, byte> streamTypes) {
    if (streamTypes.TryGetValue(streamId, out var type)) {
      switch (type) {
        case 0x01: return ("mpeg1video", ".m1v");
        case 0x02: return ("mpeg2video", ".m2v");
        case 0x03: return ("mpeg1audio", ".mp2");
        case 0x04: return ("mpeg2audio", ".mp2");
        case 0x0F: return ("aac_adts", ".aac");
        case 0x10: return ("mpeg4video", ".m4v");
        case 0x1B: return ("h264", ".h264");
        case 0x24: return ("h265", ".h265");
        case 0x81: return ("ac3", ".ac3");
        case 0x82: return ("dts", ".dts");
      }
    }
    if (streamId >= 0xE0 && streamId <= 0xEF)
      return version == 1 ? ("mpeg1video", ".m1v") : ("mpeg2video", ".m2v");
    if (streamId >= 0xC0 && streamId <= 0xDF)
      return ("mpegaudio", ".mp2");
    if (streamId == 0xFD)
      return ("extended", ".bin");
    return ($"id{streamId:X2}", ".bin");
  }

  /// <summary>
  /// Classifies a DVD private stream 1 substream and returns the number of bytes to
  /// drop from the start of the PES payload: the substream id plus the DVD substream
  /// header that precedes the codec data (4 bytes for AC-3/DTS/MLP, 7 for LPCM).
  /// </summary>
  public static (string Kind, string Extension, int Skip) ClassifySubstream(byte substreamId) {
    if (substreamId >= 0x20 && substreamId <= 0x3F) return ("subpicture", ".bin", 1);
    if (substreamId >= 0x80 && substreamId <= 0x87) return ("ac3", ".ac3", 4);
    if (substreamId >= 0x88 && substreamId <= 0x8F) return ("dts", ".dts", 4);
    if (substreamId >= 0xA0 && substreamId <= 0xA7) return ("lpcm", ".pcm", 7);
    if (substreamId >= 0xB0 && substreamId <= 0xBF) return ("mlp", ".mlp", 4);
    return ($"private{substreamId:X2}", ".bin", 1);
  }

  private static void Append(Dictionary<int, Accumulator> streams, int streamId, int substreamId,
      string kind, string extension, long? pts, ReadOnlySpan<byte> payload) {
    var key = (streamId << 8) | (substreamId & 0xFF);
    if (!streams.TryGetValue(key, out var acc)) {
      acc = new Accumulator { StreamId = streamId, SubstreamId = substreamId, Kind = kind, Extension = extension };
      streams[key] = acc;
    }
    ++acc.PacketCount;
    if (pts is { } t) {
      acc.FirstPts ??= t;
      acc.LastPts = t;
    }
    acc.Payload.Write(payload);
  }
}
