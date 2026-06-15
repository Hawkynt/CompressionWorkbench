#pragma warning disable CS1591
namespace Codec.Tracker;

/// <summary>One cell of a pattern: an optional note, instrument, volume and effect.</summary>
/// <remarks>
/// <see cref="Period"/> is the raw Amiga period for MOD; for S3M the note is kept
/// as a semitone index in <see cref="Note"/> and the period is derived from the
/// instrument's C2SPD at playback time. <see cref="Note"/> uses 0 = none,
/// otherwise a 1-based note index; 254 marks a note-off where applicable.
/// </remarks>
internal struct TrackerCell {
  public int Note;       // semitone index (MOD: ProTracker note index 1..36; S3M: 1..120), 0 = none
  public int Period;     // raw MOD period (MOD only), 0 = none
  public int Instrument; // 1-based instrument number, 0 = none
  public int Volume;     // 0..64, or -1 = none
  public int Effect;     // effect command
  public int EffectParam; // effect parameter byte

  public TrackerCell() {
    this.Volume = -1;
  }
}

/// <summary>A pattern: rows × channels grid of <see cref="TrackerCell"/>.</summary>
internal sealed class TrackerPattern {
  public required int Rows;
  public required int Channels;
  public required TrackerCell[] Cells; // row-major: cell(row, ch) = Cells[row * Channels + ch]

  public ref TrackerCell Cell(int row, int channel) => ref this.Cells[row * this.Channels + channel];
}

/// <summary>
/// The shared parsed song: orders, patterns, samples, channel count and the
/// initial speed/tempo. Both the MOD and S3M parsers populate this; the player
/// consumes it through the effect dispatch appropriate to its <see cref="Kind"/>.
/// </summary>
internal sealed class TrackerSong {
  public required TrackerKind Kind;
  public required int Channels;
  public required int[] Order;        // sequence of pattern indices
  public required TrackerPattern[] Patterns;
  public required TrackerSample?[] Samples; // 1-based: index 0 unused / null

  public int InitialSpeed = 6;   // ticks per row
  public int InitialTempo = 125; // BPM
  public int GlobalVolume = 64;  // 0..64 (S3M)

  public int[] ChannelPan = [];  // 0..255 per channel
  public bool[] ChannelMuted = []; // per channel

  /// <summary>Restart position (MOD byte 951), or 0.</summary>
  public int RestartPosition;
}

internal enum TrackerKind {
  Mod,
  S3m,
}
