#pragma warning disable CS1591
namespace Codec.TrackerXmIt;

/// <summary>
/// The XM playback engine: per-tick effect processing plus per-sample mixing into interleaved
/// stereo 16-bit PCM. Frequency, envelopes, vibrato, fadeout and effects 0..X follow XM.TXT
/// (Triton); volume-column and effect-memory semantics follow OpenMPT/libxmp where XM.TXT is
/// ambiguous.
/// </summary>
internal sealed class XmEngine {

  private readonly XmModule _mod;
  private readonly int _sampleRate;
  private readonly XmChannel[] _channels;

  private int _tempo;       // ticks per row
  private int _bpm;
  private int _orderIndex;
  private int _row;
  private int _tick;
  private double _samplesPerTick;

  // Pattern flow control.
  private int _patternDelay;       // EEx extra row repeats
  private bool _patternBreakPending;
  private int _patternBreakRow;
  private bool _positionJumpPending;
  private int _positionJumpOrder;
  private int _patternLoopRow;     // E6x loop point per-channel handled separately
  private int _globalVolume = 64;  // Gxx (0..64)

  public XmEngine(XmModule mod, int sampleRate) {
    this._mod = mod;
    this._sampleRate = sampleRate;
    this._channels = new XmChannel[mod.ChannelCount];
    for (var i = 0; i < this._channels.Length; ++i) {
      this._channels[i] = new XmChannel();
      this._channels[i].SetLinear(mod.LinearFrequency);
      this._channels[i].SetDefaultPan(128);
    }
    this._tempo = mod.DefaultTempo;
    this._bpm = mod.DefaultBpm;
    this.UpdateTimingForBpm();
  }

  private void UpdateTimingForBpm()
    => this._samplesPerTick = this._sampleRate * 2.5 / Math.Max(1, this._bpm);

  // ── public entry points ─────────────────────────────────────────────────────

  public byte[] Render(double maxSeconds) {
    var maxSamples = (long)(maxSeconds * this._sampleRate);
    var output = new List<short>(capacity: 1 << 16);
    var visited = new HashSet<int>();
    var done = false;

    while (!done && output.Count / 2 < maxSamples) {
      // Loop detection: revisiting an (order, row) pair stops the song.
      var key = (this._orderIndex << 16) | this._row;
      if (this._tick == 0 && !visited.Add(key))
        break;

      ProcessTick();

      var ticksSamples = (int)this._samplesPerTick;
      var buffer = new float[ticksSamples * 2];
      MixTick(buffer);
      for (var i = 0; i < buffer.Length; ++i)
        output.Add(ClampToShort(buffer[i]));

      AdvanceTick(ref done, visited);
    }

    var pcm = new byte[output.Count * 2];
    for (var i = 0; i < output.Count; ++i) {
      pcm[i * 2] = (byte)(output[i] & 0xFF);
      pcm[i * 2 + 1] = (byte)((output[i] >> 8) & 0xFF);
    }
    return pcm;
  }

  public double EstimateSeconds(double maxSeconds) {
    var visited = new HashSet<int>();
    double seconds = 0;
    var done = false;
    // Reset to a clean traversal state.
    this._orderIndex = 0;
    this._row = 0;
    this._tick = 0;
    while (!done && seconds < maxSeconds) {
      if (this._tick == 0) {
        var key = (this._orderIndex << 16) | this._row;
        if (!visited.Add(key)) break;
        // Scan the row only for timing-affecting effects (Fxx).
        ScanRowTiming();
      }
      seconds += this._samplesPerTick / this._sampleRate;
      AdvanceTickTimingOnly(ref done);
    }
    return Math.Min(seconds, maxSeconds);
  }

  // ── tick processing ─────────────────────────────────────────────────────────

  private void ProcessTick() {
    if (this._tick == 0)
      ProcessRow();
    else
      ProcessTickEffects();

    foreach (var ch in this._channels)
      ch.UpdatePerTick(this._mod, this._sampleRate, this._tick);
  }

  private XmPattern CurrentPattern() {
    if (this._mod.Order.Length == 0) return XmPattern.Empty(this._mod.ChannelCount);
    var pat = this._mod.Order[Math.Clamp(this._orderIndex, 0, this._mod.Order.Length - 1)];
    if (pat >= this._mod.Patterns.Length) return XmPattern.Empty(this._mod.ChannelCount);
    return this._mod.Patterns[pat];
  }

  private void ProcessRow() {
    var pattern = CurrentPattern();
    if (this._row >= pattern.Rows) this._row = pattern.Rows - 1;

    this._patternBreakPending = false;
    this._positionJumpPending = false;

    for (var c = 0; c < this._channels.Length; ++c) {
      var cell = pattern.Cell(this._row, c);
      this._channels[c].TriggerRow(this, cell, this._mod);
    }
  }

  private void ProcessTickEffects() {
    var pattern = CurrentPattern();
    for (var c = 0; c < this._channels.Length; ++c) {
      var cell = pattern.Cell(this._row, c);
      this._channels[c].TickEffects(this, cell, this._mod, this._tick);
    }
  }

  private void ScanRowTiming() {
    var pattern = CurrentPattern();
    if (this._row >= pattern.Rows) return;
    this._patternBreakPending = false;
    this._positionJumpPending = false;
    this._patternDelay = 0;
    for (var c = 0; c < this._channels.Length; ++c) {
      var cell = pattern.Cell(this._row, c);
      switch (cell.Effect) {
        case 0x0F: // Fxx speed/tempo
          if (cell.Param == 0) break;
          if (cell.Param < 0x20) this._tempo = cell.Param;
          else { this._bpm = cell.Param; UpdateTimingForBpm(); }
          break;
        case 0x0B: this._positionJumpPending = true; this._positionJumpOrder = cell.Param; break;
        case 0x0D: this._patternBreakPending = true; this._patternBreakRow = (cell.Param >> 4) * 10 + (cell.Param & 0x0F); break;
        case 0x0E when (cell.Param >> 4) == 0x0E: this._patternDelay = cell.Param & 0x0F; break;
      }
    }
  }

  // ── flow control hooks called from channels ──────────────────────────────────

  public void SetSpeed(int param) {
    if (param == 0) return;
    if (param < 0x20) this._tempo = param;
    else { this._bpm = param; UpdateTimingForBpm(); }
  }

  public void SetGlobalVolume(int v) => this._globalVolume = Math.Clamp(v, 0, 64);
  public void SlideGlobalVolume(int delta) => this._globalVolume = Math.Clamp(this._globalVolume + delta, 0, 64);
  public int GlobalVolume => this._globalVolume;

  public void RequestPositionJump(int order) { this._positionJumpPending = true; this._positionJumpOrder = order; }
  public void RequestPatternBreak(int row) { this._patternBreakPending = true; this._patternBreakRow = row; }
  public void RequestPatternDelay(int rows) => this._patternDelay = rows;
  public int Tempo => this._tempo;

  public int CurrentRowForLoop() => this._row;

  // Per-channel E6x pattern-loop coordination (loop target/count are stored on the channel).
  public void RequestPatternLoopJump(int targetRow) {
    this._patternBreakPending = false;
    this._positionJumpPending = false;
    this._row = targetRow - 1; // AdvanceRow will +1
    this._patternLoopRow = targetRow;
  }

  // ── advancing ────────────────────────────────────────────────────────────────

  private void AdvanceTick(ref bool done, HashSet<int> visited) {
    ++this._tick;
    if (this._tick < this._tempo + this._patternDelay * this._tempo)
      return;

    this._tick = 0;
    this._patternDelay = 0;
    AdvanceRow(ref done);
  }

  private void AdvanceTickTimingOnly(ref bool done) {
    ++this._tick;
    if (this._tick < this._tempo + this._patternDelay * this._tempo)
      return;
    this._tick = 0;
    this._patternDelay = 0;
    AdvanceRow(ref done);
  }

  private void AdvanceRow(ref bool done) {
    if (this._positionJumpPending) {
      this._orderIndex = this._positionJumpOrder;
      this._row = this._patternBreakPending ? this._patternBreakRow : 0;
      this._positionJumpPending = false;
      this._patternBreakPending = false;
      if (this._orderIndex >= this._mod.Order.Length) done = true;
      return;
    }
    if (this._patternBreakPending) {
      this._row = this._patternBreakRow;
      this._patternBreakPending = false;
      AdvanceOrder(ref done);
      return;
    }

    ++this._row;
    var pattern = CurrentPattern();
    if (this._row >= pattern.Rows) {
      this._row = 0;
      AdvanceOrder(ref done);
    }
  }

  private void AdvanceOrder(ref bool done) {
    ++this._orderIndex;
    if (this._orderIndex >= this._mod.Order.Length) {
      // Restart position or stop.
      if (this._mod.RestartPosition < this._mod.Order.Length && this._mod.Order.Length > 0)
        this._orderIndex = this._mod.RestartPosition;
      else
        done = true;
    }
  }

  // ── mixing ────────────────────────────────────────────────────────────────────

  private void MixTick(float[] stereoBuffer) {
    var frames = stereoBuffer.Length / 2;
    var gv = this._globalVolume / 64.0f;
    foreach (var ch in this._channels)
      ch.Mix(stereoBuffer, frames, this._sampleRate, gv);
  }

  private static short ClampToShort(float v) {
    var i = (int)MathF.Round(v);
    if (i > short.MaxValue) i = short.MaxValue;
    if (i < short.MinValue) i = short.MinValue;
    return (short)i;
  }

  internal int SampleRate => this._sampleRate;
  internal bool LinearFrequency => this._mod.LinearFrequency;
}
