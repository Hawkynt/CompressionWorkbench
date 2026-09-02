#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Mus;

/// <summary>
/// Converts a DMX/Doom MUS score into a Standard MIDI File (format 0).
/// <para>MUS is a compact, single-track sequencer format used by id's DMX sound
/// library. Each event byte packs: bit 7 = last-event-in-group (a delay varint
/// follows the group), bits 4–6 = event type, bits 0–3 = MUS channel. MUS channel
/// 15 is percussion and maps to MIDI channel 9; the remaining channels 0–14 map to
/// MIDI 0–8 and 10–15, skipping 9.</para>
/// <para>Timing convention: MUS runs at 140 Hz. The emitted SMF keeps MUS ticks 1:1
/// as MIDI delta ticks and declares a division of 70 ticks/quarter with a tempo of
/// 681818 µs/quarter, which yields 70 / (681818e-6) ≈ 140 ticks per second.</para>
/// </summary>
public static class MusToMidiConverter {

  // SMF division and tempo chosen so that ticks advance at 140 Hz (DMX rate).
  /// <summary>
  /// Defines the division constant value.
  /// </summary>
  public const int Division = 70;
  /// <summary>
  /// Defines the tempo micros per quarter constant value.
  /// </summary>
  public const int TempoMicrosPerQuarter = 681818;

  // MUS event types (bits 4–6).
  private const int EvtReleaseNote = 0;
  private const int EvtPlayNote = 1;
  private const int EvtPitchWheel = 2;
  private const int EvtSystemEvent = 3;
  private const int EvtController = 4;
  private const int EvtScoreEnd = 6;

  // MUS controller index → MIDI controller number (index 0 = program change).
  private static readonly int[] ControllerMap = [0, 0, 1, 7, 10, 11, 91, 93, 64, 67];

  // MUS system-event index (10..14) → MIDI controller number.
  private static readonly int[] SystemControllerMap = [120, 123, 126, 127, 121];

  /// <summary>Maps a MUS channel (0–15, 15 = percussion) to a MIDI channel (0–15, 9 = percussion).</summary>
  private static int MapChannel(int musChannel) {
    if (musChannel == 15)
      return 9;
    return musChannel < 9 ? musChannel : musChannel + 1;
  }

  /// <summary>
  /// Represents a result.
  /// </summary>
  public sealed record Result(byte[] Midi, int EventCount);

  /// <summary>
  /// Parses the MUS header and event stream and returns a format-0 SMF blob.
  /// Throws <see cref="InvalidDataException"/> on a malformed header.
  /// </summary>
  public static Result Convert(ReadOnlySpan<byte> data) {
    if (data.Length < 16 || data[0] != 'M' || data[1] != 'U' || data[2] != 'S' || data[3] != 0x1A)
      throw new InvalidDataException("Not a MUS file: missing 'MUS\\x1A' magic.");

    var scoreLen = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
    var scoreStart = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);

    var track = new List<byte>();
    var lastVolume = new int[16];
    Array.Fill(lastVolume, 127);

    var pos = (int)scoreStart;
    var end = Math.Min(data.Length, scoreStart + scoreLen);
    if (end > data.Length) end = data.Length;

    var pendingDelta = 0;
    var eventCount = 0;
    var done = false;

    while (pos < end && !done) {
      var descriptor = data[pos++];
      var last = (descriptor & 0x80) != 0;
      var type = (descriptor >> 4) & 0x07;
      var midiChannel = MapChannel(descriptor & 0x0F);

      switch (type) {
        case EvtReleaseNote: {
          if (pos >= end) { done = true; break; }
          var note = data[pos++] & 0x7F;
          EmitEvent(track, ref pendingDelta, (byte)(0x80 | midiChannel), (byte)note, 0);
          ++eventCount;
          break;
        }
        case EvtPlayNote: {
          if (pos >= end) { done = true; break; }
          var noteByte = data[pos++];
          var note = noteByte & 0x7F;
          var volume = lastVolume[midiChannel];
          if ((noteByte & 0x80) != 0) {
            if (pos >= end) { done = true; break; }
            volume = data[pos++] & 0x7F;
            lastVolume[midiChannel] = volume;
          }
          EmitEvent(track, ref pendingDelta, (byte)(0x90 | midiChannel), (byte)note, (byte)volume);
          ++eventCount;
          break;
        }
        case EvtPitchWheel: {
          if (pos >= end) { done = true; break; }
          var wheel = data[pos++];                 // 0..255, 0x80 = centre
          var bend = wheel * 64;                   // → 14-bit (0..16320, centre 8192)
          EmitEvent(track, ref pendingDelta, (byte)(0xE0 | midiChannel), (byte)(bend & 0x7F), (byte)((bend >> 7) & 0x7F));
          ++eventCount;
          break;
        }
        case EvtSystemEvent: {
          if (pos >= end) { done = true; break; }
          var ctrl = data[pos++];                  // 10..14
          var idx = ctrl - 10;
          if (idx >= 0 && idx < SystemControllerMap.Length)
            EmitEvent(track, ref pendingDelta, (byte)(0xB0 | midiChannel), (byte)SystemControllerMap[idx], 0);
          ++eventCount;
          break;
        }
        case EvtController: {
          if (pos + 1 >= end) { done = true; break; }
          var ctrlIndex = data[pos++];
          var value = data[pos++] & 0x7F;
          if (ctrlIndex == 0) {
            // Index 0 → program change (uses the value as the program number).
            EmitEvent(track, ref pendingDelta, (byte)(0xC0 | midiChannel), (byte)value, null);
          } else if (ctrlIndex < ControllerMap.Length) {
            EmitEvent(track, ref pendingDelta, (byte)(0xB0 | midiChannel), (byte)ControllerMap[ctrlIndex], (byte)value);
          }
          ++eventCount;
          break;
        }
        case EvtScoreEnd:
          done = true;
          break;
        default:
          // Unknown / unused event type — stop to stay coherent.
          done = true;
          break;
      }

      if (!done && last) {
        // A delay (varint) follows the last event in this group.
        var delay = ReadVarint(data, ref pos, end);
        pendingDelta += delay;
      }
    }

    AppendDelta(track, pendingDelta);
    track.AddRange([0xFF, 0x2F, 0x00]);            // End-of-track.

    return new Result(BuildFormat0(track, Division), eventCount);
  }

  // ── SMF emission ───────────────────────────────────────────────────────────

  /// <summary>Writes a channel-voice event, flushing the accumulated delta as its delta-time.</summary>
  private static void EmitEvent(List<byte> track, ref int pendingDelta, byte status, byte d1, byte? d2) {
    AppendDelta(track, pendingDelta);
    pendingDelta = 0;
    track.Add(status);
    track.Add(d1);
    if (d2.HasValue)
      track.Add(d2.Value);
  }

  private static void AppendDelta(List<byte> track, int value) => WriteVlq(track, value);

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

  /// <summary>MUS varint: 7 bits per byte, bit 7 = continuation, big-endian order.</summary>
  private static int ReadVarint(ReadOnlySpan<byte> data, ref int pos, int end) {
    var value = 0;
    while (pos < end) {
      var b = data[pos++];
      value = (value << 7) | (b & 0x7F);
      if ((b & 0x80) == 0)
        break;
    }
    return value;
  }

  private static byte[] BuildFormat0(List<byte> trackBody, int division) {
    using var ms = new MemoryStream();
    ms.Write("MThd"u8);
    Span<byte> u32 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(u32, 6);
    ms.Write(u32);
    Span<byte> hdr = stackalloc byte[6];
    BinaryPrimitives.WriteUInt16BigEndian(hdr[0..], 0);                 // format 0
    BinaryPrimitives.WriteUInt16BigEndian(hdr[2..], 1);                 // 1 track
    BinaryPrimitives.WriteUInt16BigEndian(hdr[4..], (ushort)division);
    ms.Write(hdr);

    // Prepend the tempo meta-event so the 140 Hz timing is well defined.
    var tempo = new List<byte> { 0x00, 0xFF, 0x51, 0x03,
      (byte)((TempoMicrosPerQuarter >> 16) & 0xFF),
      (byte)((TempoMicrosPerQuarter >> 8) & 0xFF),
      (byte)(TempoMicrosPerQuarter & 0xFF) };
    tempo.AddRange(trackBody);

    ms.Write("MTrk"u8);
    BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)tempo.Count);
    ms.Write(u32);
    ms.Write(tempo.ToArray());
    return ms.ToArray();
  }
}
