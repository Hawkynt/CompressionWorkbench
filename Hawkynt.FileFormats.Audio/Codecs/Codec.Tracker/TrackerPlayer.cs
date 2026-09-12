#pragma warning disable CS1591
namespace Codec.Tracker;

/// <summary>
/// The shared tick/row engine that drives a <see cref="TrackerSong"/> through the
/// mixer. Speed is ticks-per-row, tempo is BPM where the row-processing rate is
/// <c>BPM × 2 / 5</c> Hz (i.e. 50 Hz at the classic 125 BPM). On every tick the
/// engine applies the per-tick effect updates; on tick 0 of each row it applies
/// the note/effect setup. MOD and S3M differ only in their effect dispatch and
/// in how a note maps to a playback frequency, handled by the two subclasses.
/// </summary>
/// <remarks>
/// Effect semantics follow the ProTracker 2.3 effect reference and the FireLight
/// "fmoddoc" MOD documentation for MOD, and the official Scream Tracker 3
/// TECH.DOC for S3M, cross-checked against OpenMPT where the documents are
/// ambiguous (notably arpeggio tick cycling and the S3M default-pan scheme).
/// </remarks>
internal abstract class TrackerPlayer {

  protected readonly TrackerSong Song;
  protected readonly int OutputRate;
  protected readonly MixerChannel[] Channels;
  protected readonly ChannelState[] State;

  protected int Speed;
  protected int Tempo;
  protected int GlobalVolume;

  // Position bookkeeping.
  protected int OrderIndex;
  protected int Row;
  protected int Tick;

  // Flow control set by effects during a row, applied at row end.
  protected int PatternDelay;       // EE / S6 extra row repeats
  protected bool PatternBreakRequested;
  protected int PatternBreakRow;
  protected bool PositionJumpRequested;
  protected int PositionJumpOrder;

  private bool _songEnded;

  protected TrackerPlayer(TrackerSong song, int outputRate) {
    this.Song = song;
    this.OutputRate = outputRate;
    this.Channels = new MixerChannel[song.Channels];
    this.State = new ChannelState[song.Channels];
    for (var i = 0; i < song.Channels; ++i) {
      this.Channels[i] = new MixerChannel();
      this.State[i] = new ChannelState();
      this.Channels[i].Pan = song.ChannelPan.Length > i ? song.ChannelPan[i] : 128;
    }
    this.Speed = song.InitialSpeed;
    this.Tempo = song.InitialTempo;
    this.GlobalVolume = song.GlobalVolume;
  }

  /// <summary>True once the song has reached its natural end (caller stops rendering).</summary>
  public bool Ended => this._songEnded;

  /// <summary>Number of output frames per tracker tick at the current tempo.</summary>
  protected int SamplesPerTick => (int)Math.Round(this.OutputRate * 5.0 / (this.Tempo * 2.0));

  /// <summary>
  /// Renders the whole song into interleaved stereo 16-bit PCM, capped at
  /// <paramref name="maxSeconds"/>. Stops at the natural song end (when the
  /// length traversal would revisit a loop point) or the cap, whichever first.
  /// </summary>
  public byte[] Render(double maxSeconds) {
    var maxFrames = (long)(maxSeconds * this.OutputRate);
    var output = new List<byte>(capacity: 1 << 20);
    long produced = 0;

    this.OrderIndex = 0;
    this.Row = 0;
    this.Tick = 0;

    var left = new int[1 << 14];
    var right = new int[1 << 14];

    while (produced < maxFrames && !this._songEnded) {
      // Process one tick (sets up notes on tick 0).
      this.ProcessTick();
      if (this._songEnded)
        break;

      var frames = this.SamplesPerTick;
      if (frames <= 0)
        frames = 1;
      while (frames > left.Length) {
        left = new int[left.Length * 2];
        right = new int[right.Length * 2];
      }
      Array.Clear(left, 0, frames);
      Array.Clear(right, 0, frames);

      var master = this.GlobalVolume / 64.0;
      foreach (var ch in this.Channels)
        ch.Mix(left, right, frames, this.OutputRate, master);

      for (var i = 0; i < frames && produced < maxFrames; ++i, ++produced) {
        var l = Clamp16(left[i]);
        var r = Clamp16(right[i]);
        output.Add((byte)(l & 0xFF));
        output.Add((byte)((l >> 8) & 0xFF));
        output.Add((byte)(r & 0xFF));
        output.Add((byte)((r >> 8) & 0xFF));
      }

      this.AdvanceTick();
    }

    return output.ToArray();
  }

  private static short Clamp16(int v) => v > short.MaxValue ? short.MaxValue : v < short.MinValue ? short.MinValue : (short)v;

  /// <summary>Applies note setup (tick 0) and per-tick effect updates.</summary>
  private void ProcessTick() {
    if (this.OrderIndex >= this.Song.Order.Length) {
      this._songEnded = true;
      return;
    }

    var patternIdx = this.Song.Order[this.OrderIndex];
    if (patternIdx < 0 || patternIdx >= this.Song.Patterns.Length) {
      // Skip markers (255 = end, 254 = skip in S3M).
      this.AdvanceOrderSkipping();
      if (this._songEnded)
        return;
      patternIdx = this.Song.Order[this.OrderIndex];
    }

    var pattern = this.Song.Patterns[patternIdx];
    if (this.Row >= pattern.Rows)
      this.Row = 0;

    if (this.Tick == 0)
      this.ProcessRow(pattern, this.Row);
    else
      this.ProcessEffectsTick(pattern, this.Row);
  }

  private void AdvanceOrderSkipping() {
    while (this.OrderIndex < this.Song.Order.Length) {
      var p = this.Song.Order[this.OrderIndex];
      if (p >= 0 && p < this.Song.Patterns.Length)
        return;
      ++this.OrderIndex;
    }
    this._songEnded = true;
  }

  private void ProcessRow(TrackerPattern pattern, int row) {
    this.PatternBreakRequested = false;
    this.PositionJumpRequested = false;

    for (var ch = 0; ch < this.Song.Channels; ++ch) {
      ref var cell = ref pattern.Cell(row, ch);
      this.ProcessNote(ch, ref cell);
    }
  }

  private void ProcessEffectsTick(TrackerPattern pattern, int row) {
    for (var ch = 0; ch < this.Song.Channels; ++ch) {
      ref var cell = ref pattern.Cell(row, ch);
      this.ProcessEffectTickN(ch, ref cell);
    }
  }

  /// <summary>Advances tick/row/order, honouring speed, pattern delay, breaks and jumps.</summary>
  private void AdvanceTick() {
    ++this.Tick;
    if (this.Tick < this.Speed * (this.PatternDelay + 1))
      return;

    this.Tick = 0;
    this.PatternDelay = 0;

    if (this.PositionJumpRequested) {
      this.OrderIndex = this.PositionJumpOrder;
      this.Row = this.PatternBreakRequested ? this.PatternBreakRow : 0;
      this.NormalizeOrder();
      return;
    }

    if (this.PatternBreakRequested) {
      ++this.OrderIndex;
      this.Row = this.PatternBreakRow;
      this.NormalizeOrder();
      return;
    }

    ++this.Row;
    var patternIdx = this.OrderIndex < this.Song.Order.Length ? this.Song.Order[this.OrderIndex] : -1;
    var rows = patternIdx >= 0 && patternIdx < this.Song.Patterns.Length ? this.Song.Patterns[patternIdx].Rows : 64;
    if (this.Row >= rows) {
      this.Row = 0;
      ++this.OrderIndex;
      this.NormalizeOrder();
    }
  }

  private void NormalizeOrder() {
    if (this.OrderIndex >= this.Song.Order.Length) {
      this._songEnded = true;
      return;
    }
    this.AdvanceOrderSkipping();
  }

  // Subclass hooks: note trigger + effect dispatch differ between MOD and S3M.
  protected abstract void ProcessNote(int channel, ref TrackerCell cell);
  protected abstract void ProcessEffectTickN(int channel, ref TrackerCell cell);
}
