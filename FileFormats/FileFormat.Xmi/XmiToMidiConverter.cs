#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Xmi;

/// <summary>
/// Converts a Miles XMIDI (<c>.xmi</c>) song into a Standard MIDI File (format 0).
/// <para>XMIDI is an IFF-based format. The interesting part is the <c>EVNT</c> chunk:
/// it carries near-MIDI events with two key differences — note-on events carry an
/// explicit varint duration (there are no note-off events; the off must be scheduled
/// at <c>t + duration</c>), and inter-event delays are encoded as runs of bytes whose
/// value is &lt; 0x80 (each such byte adds its full value to the delay, with 0x7F
/// chaining into the next byte).</para>
/// <para>Timing convention: XMIDI ticks run at 120 Hz. The emitted SMF keeps those
/// ticks 1:1 as MIDI delta ticks, declaring a division of 60 ticks/quarter with the
/// default tempo of 500000 µs/quarter, giving 60 / 0.5 = 120 ticks per second.</para>
/// </summary>
public static class XmiToMidiConverter {

  public const int Division = 60;
  public const int TempoMicrosPerQuarter = 500000;

  public sealed record Song(byte[] Midi, IReadOnlyList<byte> Timbres);

  /// <summary>
  /// Parses the IFF wrapper and returns one converted SMF per XMID song.
  /// Throws <see cref="InvalidDataException"/> when the IFF structure is invalid.
  /// </summary>
  public static IReadOnlyList<Song> Convert(ReadOnlySpan<byte> data) {
    if (data.Length < 12 || !Match(data, 0, "FORM") || !Match(data, 8, "XDIR"))
      throw new InvalidDataException("Not an XMI file: missing FORM…XDIR.");

    var songs = new List<Song>();
    // Walk the file for every FORM…XMID; each is one song.
    var pos = 0;
    while (pos + 12 <= data.Length) {
      if (Match(data, pos, "FORM")) {
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]);
        var bodyStart = pos + 8;
        if (Match(data, bodyStart, "XMID") && bodyStart + len <= data.Length) {
          var bodyEnd = bodyStart + len;
          var song = ConvertSong(data[(bodyStart + 4)..bodyEnd]);
          if (song != null)
            songs.Add(song);
          pos = bodyEnd + (len & 1);             // IFF chunks pad to even length.
          continue;
        }
      }
      ++pos;
    }

    if (songs.Count == 0)
      throw new InvalidDataException("No XMID song bodies found.");
    return songs;
  }

  private static Song? ConvertSong(ReadOnlySpan<byte> body) {
    var timbres = new List<byte>();
    ReadOnlySpan<byte> evnt = default;
    var found = false;

    // Walk the song's sub-chunks (TIMB, RBRN, EVNT).
    var pos = 0;
    while (pos + 8 <= body.Length) {
      var id = body.Slice(pos, 4);
      var len = (int)BinaryPrimitives.ReadUInt32BigEndian(body[(pos + 4)..]);
      var dataStart = pos + 8;
      if (dataStart + len > body.Length)
        break;
      var chunk = body.Slice(dataStart, len);

      if (Match4(id, "TIMB")) {
        // u16 count, then count × (patch, bank) byte pairs.
        if (len >= 2) {
          var count = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
          for (var i = 0; i < count && 2 + i * 2 < len; ++i)
            timbres.Add(chunk[2 + i * 2]);
        }
      } else if (Match4(id, "EVNT")) {
        evnt = chunk;
        found = true;
      }

      pos = dataStart + len + (len & 1);
    }

    if (!found)
      return null;

    var midi = ConvertEvents(evnt, Division);
    return new Song(midi, timbres);
  }

  // A pending note-off: absolute tick + MIDI status/note to emit.
  private readonly record struct PendingOff(long Tick, byte Status, byte Note);

  // Orders pending note-offs by tick, then by status and note. Several notes routinely fall due
  // on the same tick, and List.Sort is an unstable introsort, so comparing on the tick alone
  // would leave the order of those note-offs - and with it the emitted MIDI bytes - down to how
  // the sort happened to permute equal keys. Two note-offs agreeing on all three fields are
  // indistinguishable in the output, so this comparison pins the byte stream down completely.
  private static int ComparePendingOff(PendingOff x, PendingOff y) {
    if (x.Tick != y.Tick)
      return x.Tick.CompareTo(y.Tick);

    return x.Status != y.Status ? x.Status.CompareTo(y.Status) : x.Note.CompareTo(y.Note);
  }

  private static byte[] ConvertEvents(ReadOnlySpan<byte> evnt, int division) {
    var track = new List<byte>();
    var pending = new List<PendingOff>();
    long currentTick = 0;      // absolute tick of the last emitted MIDI event
    long absTick = 0;          // running absolute tick position in the stream
    byte runningStatus = 0;

    // Tempo meta first.
    track.AddRange([0x00, 0xFF, 0x51, 0x03,
      (byte)((TempoMicrosPerQuarter >> 16) & 0xFF),
      (byte)((TempoMicrosPerQuarter >> 8) & 0xFF),
      (byte)(TempoMicrosPerQuarter & 0xFF)]);

    var pos = 0;
    while (pos < evnt.Length) {
      var b = evnt[pos];

      if (b < 0x80) {
        // Delay accumulation: each byte < 0x80 adds its value; 0x7F chains.
        var delay = 0;
        while (pos < evnt.Length && evnt[pos] < 0x80) {
          delay += evnt[pos];
          if (evnt[pos] != 0x7F) { ++pos; break; }
          ++pos;
        }
        absTick += delay;
        // Flush any note-offs that fall within the advanced time window.
        FlushPendingUpTo(track, pending, ref currentTick, absTick);
        continue;
      }

      // Status byte.
      runningStatus = b;
      ++pos;
      var hi = runningStatus & 0xF0;

      if (runningStatus == 0xFF) {
        // Meta-event: type + varint length + data (rare in EVNT; pass through).
        if (pos >= evnt.Length) break;
        var type = evnt[pos++];
        var metaLen = ReadVlq(evnt, ref pos);
        FlushPendingUpTo(track, pending, ref currentTick, absTick);
        EmitDelta(track, ref currentTick, absTick);
        track.Add(0xFF);
        track.Add(type);
        WriteVlq(track, metaLen);
        for (var i = 0; i < metaLen && pos < evnt.Length; ++i)
          track.Add(evnt[pos++]);
        if (type == 0x2F) break;
        continue;
      }

      if (hi == 0x90) {
        // Note-on carries note, velocity, then a varint duration.
        if (pos + 1 >= evnt.Length) break;
        var note = evnt[pos++];
        var velocity = evnt[pos++];
        var duration = ReadVlq(evnt, ref pos);

        FlushPendingUpTo(track, pending, ref currentTick, absTick);
        EmitDelta(track, ref currentTick, absTick);
        track.Add(runningStatus);
        track.Add(note);
        track.Add(velocity);

        // Schedule the matching note-off.
        pending.Add(new PendingOff(absTick + duration, (byte)(0x80 | (runningStatus & 0x0F)), note));
        pending.Sort(ComparePendingOff);
        continue;
      }

      // Other channel-voice / sysex events.
      var dataBytes = ChannelDataBytes(hi);
      FlushPendingUpTo(track, pending, ref currentTick, absTick);
      EmitDelta(track, ref currentTick, absTick);
      track.Add(runningStatus);
      for (var i = 0; i < dataBytes && pos < evnt.Length; ++i)
        track.Add(evnt[pos++]);
    }

    // Flush remaining note-offs at their scheduled ticks.
    pending.Sort(ComparePendingOff);
    foreach (var off in pending) {
      var at = Math.Max(off.Tick, currentTick);
      EmitDelta(track, ref currentTick, at);
      track.Add(off.Status);
      track.Add(off.Note);
      track.Add(0x40);
    }

    track.AddRange([0x00, 0xFF, 0x2F, 0x00]);
    return BuildFormat0(track, division);
  }

  /// <summary>Emits all pending note-offs whose tick is at or before <paramref name="upto"/>.</summary>
  private static void FlushPendingUpTo(List<byte> track, List<PendingOff> pending, ref long currentTick, long upto) {
    var i = 0;
    while (i < pending.Count && pending[i].Tick <= upto) {
      var off = pending[i];
      var at = Math.Max(off.Tick, currentTick);
      EmitDelta(track, ref currentTick, at);
      track.Add(off.Status);
      track.Add(off.Note);
      track.Add(0x40);
      pending.RemoveAt(i);
    }
  }

  private static void EmitDelta(List<byte> track, ref long currentTick, long target) {
    var delta = (int)Math.Max(0, target - currentTick);
    WriteVlq(track, delta);
    currentTick = target;
  }

  private static int ChannelDataBytes(int hi) => hi is 0xC0 or 0xD0 ? 1 : 2;

  private static int ReadVlq(ReadOnlySpan<byte> data, ref int pos) {
    var v = 0;
    while (pos < data.Length) {
      var b = data[pos++];
      v = (v << 7) | (b & 0x7F);
      if ((b & 0x80) == 0) break;
    }
    return v;
  }

  private static void WriteVlq(List<byte> track, int value) {
    Span<byte> buf = stackalloc byte[5];
    var count = 0;
    buf[count++] = (byte)(value & 0x7F);
    value >>= 7;
    while (value > 0) {
      buf[count++] = (byte)((value & 0x7F) | 0x80);
      value >>= 7;
    }
    for (var i = count - 1; i >= 0; --i)
      track.Add(buf[i]);
  }

  private static byte[] BuildFormat0(List<byte> trackBody, int division) {
    using var ms = new MemoryStream();
    ms.Write("MThd"u8);
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(u32, 6);
    ms.Write(u32);
    Span<byte> hdr = stackalloc byte[6];
    BinaryPrimitives.WriteUInt16BigEndian(hdr[0..], 0);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[2..], 1);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[4..], (ushort)division);
    ms.Write(hdr);
    ms.Write("MTrk"u8);
    BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)trackBody.Count);
    ms.Write(u32);
    ms.Write(trackBody.ToArray());
    return ms.ToArray();
  }

  private static bool Match(ReadOnlySpan<byte> data, int offset, string tag)
    => offset + 4 <= data.Length && Match4(data.Slice(offset, 4), tag);

  private static bool Match4(ReadOnlySpan<byte> span, string tag)
    => span.Length >= 4 && span[0] == tag[0] && span[1] == tag[1] && span[2] == tag[2] && span[3] == tag[3];
}
