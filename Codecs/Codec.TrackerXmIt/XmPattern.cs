#pragma warning disable CS1591
namespace Codec.TrackerXmIt;

/// <summary>A decoded XM pattern: a rows × channels grid of <see cref="XmCell"/>.</summary>
public sealed class XmPattern {

  public int Rows;
  public int Channels;
  public XmCell[] Cells = []; // row-major: cells[row * Channels + channel]

  public XmCell Cell(int row, int channel) => this.Cells[row * this.Channels + channel];

  public static XmPattern Empty(int channels) {
    var p = new XmPattern { Rows = 64, Channels = channels, Cells = new XmCell[64 * channels] };
    return p;
  }

  /// <summary>
  /// Unpacks XM packed pattern data per XM.TXT. Each cell either begins with a control byte
  /// (high bit set) whose low bits flag which of note/instrument/volume/effect/param follow, or
  /// is a full uncompressed 5-byte cell.
  /// </summary>
  public static XmPattern Unpack(byte[] data, int rows, int channels) {
    var pattern = new XmPattern { Rows = rows, Channels = channels, Cells = new XmCell[rows * channels] };
    var pos = 0;
    for (var row = 0; row < rows; ++row) {
      for (var ch = 0; ch < channels; ++ch) {
        var cell = new XmCell();
        if (pos >= data.Length) { pattern.Cells[row * channels + ch] = cell; continue; }
        var first = data[pos++];
        if ((first & 0x80) != 0) {
          if ((first & 0x01) != 0 && pos < data.Length) cell.Note = data[pos++];
          if ((first & 0x02) != 0 && pos < data.Length) cell.Instrument = data[pos++];
          if ((first & 0x04) != 0 && pos < data.Length) cell.Volume = data[pos++];
          if ((first & 0x08) != 0 && pos < data.Length) cell.Effect = data[pos++];
          if ((first & 0x10) != 0 && pos < data.Length) cell.Param = data[pos++];
        } else {
          cell.Note = first;
          if (pos < data.Length) cell.Instrument = data[pos++];
          if (pos < data.Length) cell.Volume = data[pos++];
          if (pos < data.Length) cell.Effect = data[pos++];
          if (pos < data.Length) cell.Param = data[pos++];
        }
        pattern.Cells[row * channels + ch] = cell;
      }
    }
    return pattern;
  }
}

/// <summary>One XM pattern cell. Note 97 = key-off; 0 = none.</summary>
public struct XmCell {
  public byte Note;        // 1..96 = note, 97 = key off, 0 = none
  public byte Instrument;  // 1-based, 0 = none
  public byte Volume;      // volume column byte
  public byte Effect;      // effect type
  public byte Param;       // effect parameter
}
