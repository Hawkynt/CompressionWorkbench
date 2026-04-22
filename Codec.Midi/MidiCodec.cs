#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace Codec.Midi;

/// <summary>
/// Standard MIDI File (SMF) parser + per-track re-emitter. The archive descriptor
/// uses this to enumerate tracks and produce format-0 single-track outputs from
/// multi-track inputs.
/// <para>
/// MIDI channel voice messages are not interpreted here — callers that only want
/// meta-events get them via <see cref="ParseMetaEvents"/>; byte-level track slicing
/// via <see cref="ExtractTrackBytes"/> returns the raw <c>MTrk</c> chunk data so
/// downstream tools keep note-on/note-off semantics intact.
/// </para>
/// </summary>
public sealed class MidiCodec {
  public sealed record FileHeader(int Format, int NumTracks, int Division);

  public sealed record TrackChunk(int Index, int FileOffset, int ByteLength);

  public sealed record MetaEvent(int TrackIndex, byte Type, byte[] Data);

  public FileHeader ReadHeader(ReadOnlySpan<byte> data) {
    if (data.Length < 14 ||
        data[0] != 'M' || data[1] != 'T' || data[2] != 'h' || data[3] != 'd')
      throw new InvalidDataException("Not a MIDI file: missing 'MThd' magic.");
    var headerLen = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
    if (headerLen < 6)
      throw new InvalidDataException("MThd chunk too small.");
    var format = BinaryPrimitives.ReadInt16BigEndian(data[8..]);
    var ntrks = BinaryPrimitives.ReadInt16BigEndian(data[10..]);
    var division = BinaryPrimitives.ReadInt16BigEndian(data[12..]);
    return new FileHeader(format, ntrks, division);
  }

  public IReadOnlyList<TrackChunk> FindTracks(ReadOnlySpan<byte> data) {
    var tracks = new List<TrackChunk>();
    var pos = 8 + BinaryPrimitives.ReadInt32BigEndian(data[4..]);
    while (pos + 8 <= data.Length) {
      var isMtrk = data[pos] == 'M' && data[pos + 1] == 'T' && data[pos + 2] == 'r' && data[pos + 3] == 'k';
      var chunkLen = BinaryPrimitives.ReadInt32BigEndian(data[(pos + 4)..]);
      if (pos + 8 + chunkLen > data.Length) break;
      if (isMtrk)
        tracks.Add(new TrackChunk(tracks.Count, pos + 8, chunkLen));
      pos += 8 + chunkLen;
    }
    return tracks;
  }

  /// <summary>
  /// Reads meta-events from a single MTrk chunk. Running-status preservation is
  /// handled correctly — every event's delta-time + status is decoded before the
  /// meta filter so the stream position stays coherent.
  /// </summary>
  public IReadOnlyList<MetaEvent> ParseMetaEvents(ReadOnlySpan<byte> data, TrackChunk track) {
    var events = new List<MetaEvent>();
    var body = data.Slice(track.FileOffset, track.ByteLength);
    var pos = 0;
    byte runningStatus = 0;

    while (pos < body.Length) {
      // Delta-time (variable-length quantity).
      var delta = ReadVlq(body, ref pos);
      _ = delta;
      if (pos >= body.Length) break;

      var status = body[pos];
      if (status >= 0x80) {
        runningStatus = status;
        ++pos;
      } else {
        status = runningStatus;
      }

      if (status == 0xFF) {
        // Meta-event: [0xFF][type][vlq length][data]
        if (pos >= body.Length) break;
        var type = body[pos++];
        var length = ReadVlq(body, ref pos);
        if (pos + length > body.Length) break;
        events.Add(new MetaEvent(track.Index, type, body.Slice(pos, length).ToArray()));
        pos += length;
        if (type == 0x2F) break; // End-of-track.
      } else if (status == 0xF0 || status == 0xF7) {
        // System-exclusive: [length vlq][data]
        var length = ReadVlq(body, ref pos);
        pos += length;
      } else {
        // Channel voice/mode message — 1 or 2 data bytes.
        pos += StatusDataBytes(status);
      }
    }
    return events;
  }

  /// <summary>
  /// Returns the raw <c>MTrk</c> payload bytes (minus the 8-byte "MTrk" + length header).
  /// </summary>
  public byte[] ExtractTrackBytes(ReadOnlySpan<byte> data, TrackChunk track)
    => data.Slice(track.FileOffset, track.ByteLength).ToArray();

  /// <summary>
  /// Wraps a raw MTrk body in a format-0 SMF file using <paramref name="division"/>
  /// copied from the source.
  /// </summary>
  public byte[] BuildSingleTrackFile(byte[] trackBody, int division) {
    using var ms = new MemoryStream();
    ms.Write("MThd"u8);
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(buf, 6);
    ms.Write(buf);
    Span<byte> hdr = stackalloc byte[6];
    BinaryPrimitives.WriteInt16BigEndian(hdr[0..], 0);                 // format=0
    BinaryPrimitives.WriteInt16BigEndian(hdr[2..], 1);                 // ntrks=1
    BinaryPrimitives.WriteInt16BigEndian(hdr[4..], (short)division);
    ms.Write(hdr);

    ms.Write("MTrk"u8);
    BinaryPrimitives.WriteInt32BigEndian(buf, trackBody.Length);
    ms.Write(buf);
    ms.Write(trackBody);
    return ms.ToArray();
  }

  private static int ReadVlq(ReadOnlySpan<byte> body, ref int pos) {
    var v = 0;
    while (pos < body.Length) {
      var b = body[pos++];
      v = (v << 7) | (b & 0x7F);
      if ((b & 0x80) == 0) return v;
    }
    return v;
  }

  private static int StatusDataBytes(byte status) {
    // Program change (0xCn) and channel pressure (0xDn) have one data byte;
    // everything else with status 0x80–0xEF has two.
    var hi = status & 0xF0;
    return hi is 0xC0 or 0xD0 ? 1 : 2;
  }
}
