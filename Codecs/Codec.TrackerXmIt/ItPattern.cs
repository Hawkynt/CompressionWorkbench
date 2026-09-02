#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.TrackerXmIt;

/// <summary>A decoded IT pattern: rows × 64 channels of <see cref="ItCell"/> (per ITTECH.TXT).</summary>
public sealed class ItPattern {

  /// <summary>
  /// Defines the max channels constant value.
  /// </summary>
public const int MaxChannels = 64;
  /// <summary>
  /// Provides the rows value.
  /// </summary>
public int Rows = 64;
  /// <summary>
  /// Provides the cells value.
  /// </summary>
public ItCell[] Cells = []; // row-major: cells[row * MaxChannels + channel]

  /// <summary>
  /// Performs the cell operation.
  /// </summary>
public ItCell Cell(int row, int channel) => this.Cells[row * MaxChannels + channel];

  /// <summary>
  /// Performs the empty operation.
  /// </summary>
public static ItPattern Empty() {
    return new ItPattern { Rows = 64, Cells = new ItCell[64 * MaxChannels] };
  }

  /// <summary>
  /// Decodes IT's RLE-ish packed pattern format. Each row is a stream of cells; a leading
  /// channel byte's high bit (0x80) signals a "mask" byte follows, selecting which of
  /// note/instrument/volume/command+param are present, with last-value memory per channel.
  /// </summary>
  public static ItPattern Parse(byte[] blob, int off) {
    if (off <= 0 || off + 8 > blob.Length) return Empty();
    var packedSize = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off, 2));
    var rows = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off + 2, 2));
    if (rows <= 0 || rows > 256) rows = 64;
    var dataStart = off + 8;
    var dataEnd = Math.Min(blob.Length, dataStart + packedSize);

    var pattern = new ItPattern { Rows = rows, Cells = new ItCell[rows * MaxChannels] };
    var lastMask = new byte[MaxChannels];
    var lastNote = new byte[MaxChannels];
    var lastIns = new byte[MaxChannels];
    var lastVol = new byte[MaxChannels];
    var lastCmd = new byte[MaxChannels];
    var lastParam = new byte[MaxChannels];

    var pos = dataStart;
    var row = 0;
    while (row < rows && pos < dataEnd) {
      var channelVar = blob[pos++];
      if (channelVar == 0) { ++row; continue; }

      var channel = (channelVar - 1) & 0x3F;
      byte mask;
      if ((channelVar & 0x80) != 0) {
        if (pos >= dataEnd) break;
        mask = blob[pos++];
        lastMask[channel] = mask;
      } else {
        mask = lastMask[channel];
      }

      var cell = new ItCell();
      if ((mask & 0x01) != 0) { if (pos < dataEnd) lastNote[channel] = blob[pos++]; }
      if ((mask & 0x02) != 0) { if (pos < dataEnd) lastIns[channel] = blob[pos++]; }
      if ((mask & 0x04) != 0) { if (pos < dataEnd) lastVol[channel] = blob[pos++]; }
      if ((mask & 0x08) != 0) {
        if (pos < dataEnd) lastCmd[channel] = blob[pos++];
        if (pos < dataEnd) lastParam[channel] = blob[pos++];
      }

      cell.HasNote = (mask & 0x11) != 0;
      cell.Note = (mask & 0x11) != 0 ? lastNote[channel] : (byte)0;
      cell.HasInstrument = (mask & 0x22) != 0;
      cell.Instrument = (mask & 0x22) != 0 ? lastIns[channel] : (byte)0;
      cell.HasVolume = (mask & 0x44) != 0;
      cell.Volume = (mask & 0x44) != 0 ? lastVol[channel] : (byte)0;
      cell.HasCommand = (mask & 0x88) != 0;
      cell.Command = (mask & 0x88) != 0 ? lastCmd[channel] : (byte)0;
      cell.Param = (mask & 0x88) != 0 ? lastParam[channel] : (byte)0;

      pattern.Cells[row * MaxChannels + channel] = cell;
    }

    return pattern;
  }
}

/// <summary>One IT pattern cell. Note 255 = note off, 254 = note cut, 246..253 = note fade range.</summary>
public struct ItCell {
  /// <summary>
  /// Provides the has note and has instrument and has volume and has command value.
  /// </summary>
public bool HasNote, HasInstrument, HasVolume, HasCommand;
  /// <summary>
  /// Provides the note value.
  /// </summary>
public byte Note;        // 0..119 note, 255 = off, 254 = cut, 253 = fade
  /// <summary>
  /// Provides the instrument value.
  /// </summary>
public byte Instrument;  // 1-based
  /// <summary>
  /// Provides the volume value.
  /// </summary>
public byte Volume;      // 0..64 volume; 65..213 volume-column commands
  /// <summary>
  /// Provides the command value.
  /// </summary>
public byte Command;     // 1=A..26=Z
  /// <summary>
  /// Provides the param value.
  /// </summary>
public byte Param;
}
